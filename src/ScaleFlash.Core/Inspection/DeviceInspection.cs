using System.Text;
using System.Text.RegularExpressions;
using ScaleFlash.Core.FileSystems.Ext;
using ScaleFlash.Core.Imaging;
using ScaleFlash.Core.IO;

namespace ScaleFlash.Core.Inspection;

public sealed record InspectedPartition(PartitionEntry Partition, Imaging.FileSystemInfo FileSystem, ExtFileSystem? Ext, string? Error)
{
    public string Title => $"พาร์ทิชัน {Partition.Index + 1}: {Partition.TypeName}";
}

public sealed record VersionFile(string Path, string Content);

/// <summary>Facts read from inside the Linux system on the device.</summary>
public sealed record SystemInfo(
    string? OsName,
    string? Hostname,
    IReadOnlyList<string> Kernels,
    IReadOnlyList<VersionFile> VersionFiles);

/// <summary>
/// Opens a USB stick, SD card or image file read-only and describes what is on it:
/// partitions, file systems (with journal replay), and system/version information.
/// </summary>
public sealed partial class DeviceInspection : IDisposable
{
    private readonly DeviceReader _reader;
    private readonly List<DeviceReader> _slices = [];

    private DeviceInspection(DeviceReader reader, string name)
    {
        _reader = reader;
        DisplayName = name;
    }

    public string DisplayName { get; }
    public long Size => _reader.Length;
    public IBlockDevice Device => _reader.Device;
    public PartitionLayout Layout { get; private set; } = PartitionLayout.Empty;
    public List<InspectedPartition> Partitions { get; } = [];
    public string? Bootloader { get; private set; }
    public SystemInfo? System { get; private set; }

    /// <summary>The ext partition holding the operating system (has /etc), else the largest ext partition.</summary>
    public InspectedPartition? MainPartition { get; private set; }

    /// <param name="device">Taken over: disposed together with the inspection.</param>
    public static DeviceInspection Open(IBlockDevice device, string displayName, CancellationToken ct = default)
    {
        var reader = new DeviceReader(device, cacheMegabytes: 32, ownsDevice: true);
        var ins = new DeviceInspection(reader, displayName);
        try
        {
            ins.Analyze(ct);
            return ins;
        }
        catch
        {
            ins.Dispose();
            throw;
        }
    }

    private void Analyze(CancellationToken ct)
    {
        Layout = PartitionLayout.Parse((o, l) => _reader.Read(o, l), Math.Max(512, _reader.Device.SectorSize));
        var parts = Layout.Partitions.ToList();
        if (parts.Count == 0 && _reader.Length > 4096)
            parts.Add(new PartitionEntry(0, 0, _reader.Length, "ทั้งอุปกรณ์", 0, Guid.Empty, false)); // superfloppy

        foreach (var p in parts)
        {
            ct.ThrowIfCancellationRequested();
            if (p.StartOffset + 4096 > _reader.Length)
            {
                Partitions.Add(new InspectedPartition(p, Imaging.FileSystemInfo.Unknown, null, "พาร์ทิชันอยู่นอกขนาดอุปกรณ์"));
                continue;
            }
            var slice = _reader.Slice(p.StartOffset, Math.Min(p.Length, _reader.Length - p.StartOffset));
            _slices.Add(slice);
            var fsInfo = FileSystemProbe.Detect(slice.Read(0, 4096));
            ExtFileSystem? ext = null;
            string? error = null;
            if (ExtFileSystem.Probe(slice))
            {
                try { ext = ExtFileSystem.Open(slice); }
                catch (Exception ex) when (ex is ExtFormatException or IOException) { error = ex.Message; }
            }
            Partitions.Add(new InspectedPartition(p, fsInfo, ext, error));
        }

        var exts = Partitions.Where(p => p.Ext is not null).ToList();
        MainPartition = exts.FirstOrDefault(p => SafeLookup(p.Ext!, "/etc") is not null)
                        ?? exts.OrderByDescending(p => p.Partition.Length).FirstOrDefault();
        if (MainPartition?.Ext is { } fs) System = ReadSystemInfo(fs, ct);

        long gapEnd = parts.Count > 0 ? parts.Min(p => p.StartOffset) : 0;
        if (gapEnd > 512) Bootloader = BootloaderBanner.Find(_reader.Read(0, (int)Math.Min(gapEnd, 16 << 20)));
    }

    private static ExtInode? SafeLookup(ExtFileSystem fs, string path)
    {
        try { return fs.Lookup(path); }
        catch (Exception ex) when (ex is ExtFormatException or IOException or InvalidOperationException) { return null; }
    }

    private static string? ReadText(ExtFileSystem fs, string path, int max = 4096)
    {
        var inode = SafeLookup(fs, path);
        if (inode is not { Type: ExtFileType.File } || inode.Size > max) return null;
        try { return Encoding.UTF8.GetString(fs.ReadAllBytes(inode)).Trim(); }
        catch (Exception ex) when (ex is ExtFormatException or IOException) { return null; }
    }

    [GeneratedRegex(@"(version|release|build|firmware|fw[-_]?ver)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionName();

    /// <summary>Names that contain "version" but are code or database schemas, not version info.</summary>
    [GeneratedRegex(@"\.(tbl|sh|py|pl|js|c|h|so|ko|o|a|html?|css|xml|json|conf)$|^(get|set|check|update)", RegexOptions.IgnoreCase)]
    private static partial Regex NotVersionInfo();

    public static SystemInfo ReadSystemInfo(ExtFileSystem fs, CancellationToken ct = default)
    {
        string? os = null;
        foreach (string p in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            var text = ReadText(fs, p);
            if (text is null) continue;
            var dict = text.Split('\n').Select(l => l.Split('=', 2)).Where(a => a.Length == 2)
                .ToDictionary(a => a[0].Trim(), a => a[1].Trim().Trim('"'));
            os = dict.GetValueOrDefault("PRETTY_NAME") ?? dict.GetValueOrDefault("NAME");
            if (os is not null) break;
        }
        os ??= ReadText(fs, "/etc/issue")?.Split('\n')[0].Replace("\\n", "").Replace("\\l", "").Trim();
        string? host = ReadText(fs, "/etc/hostname")?.Split('\n')[0];

        var kernels = new List<string>();
        foreach (string dir in new[] { "/lib/modules", "/usr/lib/modules" })
        {
            var d = SafeLookup(fs, dir);
            if (d is { Type: ExtFileType.Directory })
                kernels.AddRange(fs.ListDirectory(d).Where(e => e.Type is ExtFileType.Directory or ExtFileType.Unknown).Select(e => e.Name));
        }

        // Small text files whose name says "version" in the usual places (bounded search).
        var versions = new List<VersionFile>();
        var seen = new HashSet<string>();
        int visited = 0;
        void Scan(string path, int depth)
        {
            if (depth > 3 || visited > 4000 || versions.Count >= 30) return;
            ct.ThrowIfCancellationRequested();
            var inode = SafeLookup(fs, path);
            if (inode is not { Type: ExtFileType.Directory }) return;
            List<ExtDirEntry> entries;
            try { entries = fs.ListDirectory(inode); }
            catch (Exception ex) when (ex is ExtFormatException or IOException) { return; }
            foreach (var e in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (++visited > 4000) return;
                string p = path.TrimEnd('/') + "/" + e.Name;
                if (e.Type == ExtFileType.Directory)
                {
                    if (e.Name is not ("proc" or "sys" or "dev" or "lib" or "usr" or "share" or "include" or "modules" or "python3" or "perl5"))
                        Scan(p, depth + 1);
                }
                else if (VersionName().IsMatch(e.Name) && !NotVersionInfo().IsMatch(e.Name) && seen.Add(p))
                {
                    var text = ReadText(fs, p, 2048);
                    // skip scripts that merely mention "version" in their name
                    if (text is not null && text.Length > 0 && !text.Contains('\0') && !text.StartsWith("#!", StringComparison.Ordinal))
                        versions.Add(new VersionFile(p, text.Length > 300 ? text[..300] + "…" : text));
                }
            }
        }
        foreach (string root in new[] { "/etc", "/opt", "/home", "/root", "/app", "/apps", "/data", "/usr/local", "/var/lib", "/" })
            Scan(root, root == "/" ? 2 : 0);

        return new SystemInfo(os, host, kernels, versions);
    }

    public void Dispose()
    {
        foreach (var s in _slices) s.Dispose();
        _reader.Dispose();
    }
}
