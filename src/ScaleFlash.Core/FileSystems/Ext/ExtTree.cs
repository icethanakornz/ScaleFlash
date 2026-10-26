using System.Security.Cryptography;
using System.Text;

namespace ScaleFlash.Core.FileSystems.Ext;

public sealed record ExtTreeEntry(string Path, ExtInode Inode)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>Whole-tree helpers: recursive walk, manifest (for tests/diagnostics) and file extraction.</summary>
public static class ExtTree
{
    /// <summary>Depth-first walk of every entry below <paramref name="path"/> (not including it).</summary>
    public static IEnumerable<ExtTreeEntry> Walk(ExtFileSystem fs, string path = "/", CancellationToken ct = default)
    {
        var start = fs.Lookup(path) ?? throw new DirectoryNotFoundException(path);
        var stack = new Stack<(string Path, ExtInode Dir, int Depth)>();
        stack.Push((path.TrimEnd('/'), start, 0));
        var visited = new HashSet<uint> { start.Number };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dirPath, dir, depth) = stack.Pop();
            foreach (var e in fs.ListDirectory(dir).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var inode = fs.GetInode(e.Inode);
                string p = dirPath + "/" + e.Name;
                yield return new ExtTreeEntry(p, inode);
                if (inode.Type == ExtFileType.Directory && depth < 256 && visited.Add(inode.Number))
                    stack.Push((p, inode, depth + 1));
            }
        }
    }

    /// <summary>"type TAB path TAB size TAB sha256|target" lines sorted by UTF-8 byte order (matches the Linux fixture script).</summary>
    public static List<string> Manifest(ExtFileSystem fs, CancellationToken ct = default)
    {
        var lines = new List<(byte[] Key, string Line)>();
        foreach (var e in Walk(fs, "/", ct))
        {
            string line = e.Inode.Type switch
            {
                ExtFileType.Directory => $"d\t{e.Path}\t0\t-",
                ExtFileType.File => $"f\t{e.Path}\t{e.Inode.Size}\t{Sha256(fs, e.Inode, ct)}",
                ExtFileType.Symlink => $"l\t{e.Path}\t{e.Inode.Size}\t{fs.ReadSymlink(e.Inode)}",
                _ => $"o\t{e.Path}\t0\t-",
            };
            lines.Add((Encoding.UTF8.GetBytes(e.Path), line));
        }
        lines.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        return lines.Select(l => l.Line).ToList();
    }

    public static string Sha256(ExtFileSystem fs, ExtInode inode, CancellationToken ct = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var runs = fs.GetBlockRuns(inode);
        var buf = new byte[1 << 20];
        for (long pos = 0; pos < inode.Size;)
        {
            ct.ThrowIfCancellationRequested();
            int n = fs.ReadFile(inode, runs, pos, buf);
            sha.AppendData(buf, 0, n);
            pos += n;
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Makes a Linux file name safe for Windows.</summary>
    public static string WindowsSafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        string s = sb.ToString().TrimEnd(' ', '.');
        if (s.Length == 0) s = "_";
        string stem = s.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            s = "_" + s;
        return s;
    }

    /// <summary>
    /// Copies a file or a whole folder out of the file system into a Windows folder.
    /// Symlinks become small ".symlink.txt" files containing the link target.
    /// </summary>
    public static (int Files, long Bytes, int Skipped) Extract(ExtFileSystem fs, string sourcePath, string destFolder,
        IProgress<(long Bytes, string Path)>? progress = null, CancellationToken ct = default)
    {
        var root = fs.Lookup(sourcePath, followLast: false) ?? throw new FileNotFoundException(sourcePath);
        string rootName = sourcePath == "/" ? "root" : WindowsSafeName(sourcePath.TrimEnd('/')[(sourcePath.TrimEnd('/').LastIndexOf('/') + 1)..]);
        int files = 0, skipped = 0;
        long bytes = 0;

        void ExtractOne(string linuxPath, ExtInode inode, string target)
        {
            ct.ThrowIfCancellationRequested();
            switch (inode.Type)
            {
                case ExtFileType.File:
                    using (var src = fs.OpenRead(inode))
                    using (var dst = File.Create(target))
                    {
                        var buf = new byte[1 << 20];
                        int n;
                        while ((n = src.Read(buf)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            dst.Write(buf, 0, n);
                            bytes += n;
                            progress?.Report((bytes, linuxPath));
                        }
                    }
                    File.SetLastWriteTimeUtc(target, inode.Mtime.UtcDateTime);
                    files++;
                    break;
                case ExtFileType.Symlink:
                    File.WriteAllText(target + ".symlink.txt", fs.ReadSymlink(inode));
                    files++;
                    break;
                case ExtFileType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        string rootTarget = Path.Combine(destFolder, rootName);
        ExtractOne(sourcePath, root, rootTarget);
        if (root.Type == ExtFileType.Directory)
        {
            foreach (var e in Walk(fs, sourcePath, ct))
            {
                string rel = e.Path[sourcePath.TrimEnd('/').Length..].TrimStart('/');
                string target = Path.Combine([rootTarget, .. rel.Split('/').Select(WindowsSafeName)]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                ExtractOne(e.Path, e.Inode, target);
            }
        }
        return (files, bytes, skipped);
    }
}
