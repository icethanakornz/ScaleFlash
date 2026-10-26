using System.Diagnostics;
using System.Text;
using ScaleFlash.Core.Devices;
using ScaleFlash.Core.Engine;
using ScaleFlash.Core.FileSystems.Ext;
using ScaleFlash.Core.Inspection;
using ScaleFlash.Core.IO;

/// <summary>Read-only commands for looking inside USB sticks, SD cards and image files.</summary>
internal static class InspectCommands
{
    public const string Help = """

        Read-only inspection (SRC = image file path or disk:N):
          inspect SRC                           partitions, file systems, Linux system/version info
          ls SRC [PATH] [--part N] [--no-replay]   list a folder of the Linux file system
          cat SRC PATH [--part N]               print a file
          manifest SRC [--part N] [--no-replay] [--out FILE]   every entry with SHA-256 (diagnostics)
          diff SRC IMAGE                        files that differ between a device and an image
          rawdiff SRC IMAGE                     byte-for-byte comparison
          hide-letters --disk N                 remove drive letters Windows cannot read (stops "format?" prompts)
        """;

    public static int? Run(string[] a) => a[0] switch
    {
        "inspect" => Inspect(a),
        "ls" => Ls(a),
        "cat" => Cat(a),
        "manifest" => Manifest(a),
        "diff" => Diff(a),
        "rawdiff" => RawDiff(a),
        "hide-letters" => HideLetters(a),
        _ => null,
    };

    private static string? Opt(string[] a, string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    private static bool Flag(string[] a, string name) => a.Contains(name);

    private static IBlockDevice OpenSource(string spec) =>
        spec.StartsWith("disk:", StringComparison.OrdinalIgnoreCase)
            ? DiskBlockDevice.Open(int.Parse(spec[5..]), writable: false)
            : FileBlockDevice.OpenRead(spec);

    private static DeviceInspection OpenInspection(string spec) => DeviceInspection.Open(OpenSource(spec), spec);

    private static ExtFileSystem PickFs(DeviceInspection ins, string[] a)
    {
        InspectedPartition p;
        if (int.TryParse(Opt(a, "--part"), out int part))
            p = ins.Partitions.ElementAtOrDefault(part - 1) ?? throw new InvalidOperationException($"no partition {part}");
        else
            p = ins.MainPartition ?? throw new InvalidOperationException("no ext2/3/4 file system found");
        if (Flag(a, "--no-replay"))
            return ExtFileSystem.Open(new DeviceReader(ins.Device, p.Partition.StartOffset, p.Partition.Length), replayJournal: false);
        return p.Ext ?? throw new InvalidOperationException(p.Error ?? "not an ext file system");
    }

    private static int Inspect(string[] a)
    {
        using var ins = OpenInspection(a[1]);
        Console.WriteLine($"Device:     {ins.DisplayName} ({Format.Bytes(ins.Size)})");
        Console.WriteLine($"Partitions: {ins.Layout.SchemeName}");
        foreach (var p in ins.Partitions)
        {
            Console.WriteLine($"  P{p.Partition.Index + 1}: {p.Partition.TypeName}, {Format.Bytes(p.Partition.Length)} at {p.Partition.StartOffset}, fs {p.FileSystem.Type}");
            if (p.Error is not null) Console.WriteLine($"      error: {p.Error}");
            if (p.Ext is not { } fs) continue;
            var sb = fs.Superblock;
            Console.WriteLine($"      {sb.TypeName} label='{sb.VolumeName}' used {Format.Bytes(sb.UsedBytes)}/{Format.Bytes(sb.TotalBytes)} " +
                              $"last write {sb.WriteTime?.LocalDateTime:yyyy-MM-dd HH:mm} mounts {sb.MountCount}");
            Console.WriteLine($"      clean={sb.CleanlyUnmounted} needs_recovery={sb.NeedsRecovery} errors={sb.HasErrors} " +
                              $"replayed={fs.ReplayedBlocks} blocks/{fs.ReplayedTransactions} transactions");
            if (fs.Warning is not null) Console.WriteLine($"      warning: {fs.Warning}");
        }
        if (ins.Bootloader is not null) Console.WriteLine($"Bootloader: {ins.Bootloader}");
        if (ins.System is { } si)
        {
            Console.WriteLine($"OS:         {si.OsName}");
            Console.WriteLine($"Hostname:   {si.Hostname}");
            Console.WriteLine($"Kernels:    {string.Join(", ", si.Kernels)}");
            foreach (var v in si.VersionFiles) Console.WriteLine($"  {v.Path}: {v.Content.ReplaceLineEndings(" | ")}");
        }
        return 0;
    }

    private static int Ls(string[] a)
    {
        using var ins = OpenInspection(a[1]);
        var fs = PickFs(ins, a);
        string path = a.Length > 2 && !a[2].StartsWith("--", StringComparison.Ordinal) ? a[2] : "/";
        var dir = fs.Lookup(path) ?? throw new FileNotFoundException(path);
        foreach (var e in fs.ListDirectory(dir).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var i = fs.GetInode(e.Inode);
            char t = i.Type switch { ExtFileType.Directory => 'd', ExtFileType.Symlink => 'l', _ => '-' };
            string extra = i.Type == ExtFileType.Symlink ? " -> " + fs.ReadSymlink(i) : "";
            Console.WriteLine($"{t}{i.Permissions} {i.Size,12} {i.Mtime.LocalDateTime:yyyy-MM-dd HH:mm} {e.Name}{extra}");
        }
        return 0;
    }

    private static int Cat(string[] a)
    {
        using var ins = OpenInspection(a[1]);
        var fs = PickFs(ins, a);
        var f = fs.Lookup(a[2]) ?? throw new FileNotFoundException(a[2]);
        using var stdout = Console.OpenStandardOutput();
        using var src = fs.OpenRead(f);
        src.CopyTo(stdout);
        return 0;
    }

    private static int Manifest(string[] a)
    {
        using var ins = OpenInspection(a[1]);
        var fs = PickFs(ins, a);
        var sw = Stopwatch.StartNew();
        var lines = ExtTree.Manifest(fs);
        if (Opt(a, "--out") is { } outFile)
        {
            File.WriteAllLines(outFile, lines, new UTF8Encoding(false));
            Console.WriteLine($"{lines.Count} entries in {sw.Elapsed.TotalSeconds:0.0}s (replayed {fs.ReplayedBlocks} blocks)");
        }
        else foreach (var l in lines) Console.WriteLine(l);
        return 0;
    }

    private static int Diff(string[] a)
    {
        using var dev = OpenInspection(a[1]);
        using var img = OpenInspection(a[2]);
        var fa = dev.MainPartition?.Ext ?? throw new InvalidOperationException("no ext file system on " + a[1]);
        var fb = img.MainPartition?.Ext ?? throw new InvalidOperationException("no ext file system on " + a[2]);
        var sw = Stopwatch.StartNew();
        var r = ExtTreeComparer.Compare(fa, fb, null, CancellationToken.None);
        foreach (var d in r.Differences) Console.WriteLine($"{d.Kind,-11} {d.Path}  ({d.Detail})");
        Console.WriteLine($"{r.Differences.Count} differences; compared {r.FilesCompared} files ({Format.Bytes(r.BytesCompared)}) in {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }

    private static int RawDiff(string[] a)
    {
        using var dev = OpenSource(a[1]);
        using var img = OpenSource(a[2]);
        var r = RawComparer.Compare(dev, img, null, CancellationToken.None);
        Console.WriteLine(r.Identical ? "IDENTICAL"
            : $"DIFFERENT: {Format.Bytes(r.DifferentBytes)} in {r.DifferentChunks} chunks, first at {r.FirstDifference}{(r.LengthMismatch ? ", device smaller than image" : "")}");
        return r.Identical ? 0 : 1;
    }

    private static int HideLetters(string[] a)
    {
        if (!int.TryParse(Opt(a, "--disk"), out int n)) throw new ArgumentException("hide-letters needs --disk N");
        var removed = VolumeManager.HideUnreadableVolumes(n);
        Console.WriteLine(removed.Count == 0 ? "nothing to hide" : "removed: " + string.Join(", ", removed));
        return 0;
    }
}
