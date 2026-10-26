using ScaleFlash.Core.FileSystems.Ext;
using ScaleFlash.Core.IO;
using ScaleFlash.Core.Native;

namespace ScaleFlash.Core.Inspection;

public enum DiffKind { Modified, Added, Removed, TypeChanged }

public sealed record FileDifference(string Path, DiffKind Kind, string Detail)
{
    public string KindText => Kind switch
    {
        DiffKind.Modified => "เปลี่ยน",
        DiffKind.Added => "มีเพิ่ม",
        DiffKind.Removed => "หายไป",
        _ => "ชนิดเปลี่ยน",
    };
}

public readonly record struct CompareProgress(long BytesDone, long BytesTotal, string? CurrentPath)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;
}

public sealed record TreeCompareResult(IReadOnlyList<FileDifference> Differences, int FilesCompared, long BytesCompared)
{
    public int Count(DiffKind k) => Differences.Count(d => d.Kind == k);
}

/// <summary>File-by-file comparison of the Linux system on a device against a reference image.</summary>
public static class ExtTreeComparer
{
    /// <param name="device">What is on the USB/card now.</param>
    /// <param name="reference">The master image.</param>
    public static TreeCompareResult Compare(ExtFileSystem device, ExtFileSystem reference,
        IProgress<CompareProgress>? progress, CancellationToken ct)
    {
        var a = ExtTree.Walk(device, "/", ct).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var b = ExtTree.Walk(reference, "/", ct).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var diffs = new List<FileDifference>();
        var toCompare = new List<(string Path, ExtInode A, ExtInode B)>();

        foreach (var (path, ea) in a)
        {
            if (!b.TryGetValue(path, out var eb))
            {
                diffs.Add(new FileDifference(path, DiffKind.Added, Describe(ea.Inode)));
                continue;
            }
            if (ea.Inode.Type != eb.Inode.Type)
            {
                diffs.Add(new FileDifference(path, DiffKind.TypeChanged, $"{TypeName(eb.Inode.Type)} → {TypeName(ea.Inode.Type)}"));
                continue;
            }
            switch (ea.Inode.Type)
            {
                case ExtFileType.File when ea.Inode.Size != eb.Inode.Size:
                    diffs.Add(new FileDifference(path, DiffKind.Modified, $"ขนาด {Format(eb.Inode.Size)} → {Format(ea.Inode.Size)}"));
                    break;
                case ExtFileType.File:
                    toCompare.Add((path, ea.Inode, eb.Inode));
                    break;
                case ExtFileType.Symlink:
                    string ta = device.ReadSymlink(ea.Inode), tb = reference.ReadSymlink(eb.Inode);
                    if (ta != tb) diffs.Add(new FileDifference(path, DiffKind.Modified, $"ลิงก์ {tb} → {ta}"));
                    break;
            }
        }
        foreach (var (path, eb) in b)
            if (!a.ContainsKey(path)) diffs.Add(new FileDifference(path, DiffKind.Removed, Describe(eb.Inode)));

        long total = toCompare.Sum(t => t.A.Size), done = 0;
        var bufA = new byte[1 << 20];
        var bufB = new byte[1 << 20];
        long lastReport = 0;
        foreach (var (path, ia, ib) in toCompare)
        {
            ct.ThrowIfCancellationRequested();
            var runsA = device.GetBlockRuns(ia);
            var runsB = reference.GetBlockRuns(ib);
            bool same = true;
            long pos = 0;
            while (pos < ia.Size)
            {
                ct.ThrowIfCancellationRequested();
                int na = device.ReadFile(ia, runsA, pos, bufA);
                reference.ReadFile(ib, runsB, pos, bufB);
                if (!bufA.AsSpan(0, na).SequenceEqual(bufB.AsSpan(0, na)))
                {
                    same = false;
                    done += ia.Size - pos;
                    break;
                }
                pos += na;
                done += na;
                if (progress is not null && done - lastReport > (8 << 20))
                {
                    lastReport = done;
                    progress.Report(new CompareProgress(done, total, path));
                }
            }
            if (!same) diffs.Add(new FileDifference(path, DiffKind.Modified, $"เนื้อหาต่างกัน ({Format(ia.Size)})"));
        }
        progress?.Report(new CompareProgress(total, total, null));
        diffs.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
        return new TreeCompareResult(diffs, toCompare.Count, total);
    }

    private static string Describe(ExtInode i) => i.Type == ExtFileType.File ? $"ไฟล์ {Format(i.Size)}" : TypeName(i.Type);

    private static string TypeName(ExtFileType t) => t switch
    {
        ExtFileType.File => "ไฟล์",
        ExtFileType.Directory => "โฟลเดอร์",
        ExtFileType.Symlink => "ลิงก์",
        _ => "ไฟล์พิเศษ",
    };

    private static string Format(long b) => Engine.Format.Bytes(b);
}

public sealed record RawCompareResult(long BytesCompared, long DifferentBytes, int DifferentChunks, long? FirstDifference, bool LengthMismatch)
{
    public bool Identical => DifferentChunks == 0 && !LengthMismatch;
}

/// <summary>Byte-for-byte comparison of a device against an image (4 MiB granularity for the report).</summary>
public static class RawComparer
{
    public static RawCompareResult Compare(IBlockDevice device, IBlockDevice image, IProgress<CompareProgress>? progress, CancellationToken ct)
    {
        const int Chunk = 4 << 20;
        long length = image.Length;
        bool mismatch = device.Length < length;
        long cmpLen = Math.Min(length, device.Length);
        using var bufA = new AlignedBuffer(Chunk);
        using var bufB = new AlignedBuffer(Chunk);
        int sector = Math.Max(Math.Max(device.SectorSize, image.SectorSize), 1);
        long diffBytes = 0;
        int diffChunks = 0;
        long? first = null;
        for (long off = 0; off < cmpLen; off += Chunk)
        {
            ct.ThrowIfCancellationRequested();
            int len = (int)Math.Min(Chunk, cmpLen - off);
            int aligned = (len + sector - 1) / sector * sector;
            var a = bufA.Slice(0, aligned);
            var b = bufB.Slice(0, aligned);
            a.Clear();
            b.Clear();
            device.Read(a[..(int)Math.Min(aligned, device.Length - off)], off);
            image.Read(b[..(int)Math.Min(len, image.Length - off)], off);
            if (!a[..len].SequenceEqual(b[..len]))
            {
                diffChunks++;
                for (int i = 0; i < len; i += 4096)
                {
                    int n = Math.Min(4096, len - i);
                    if (!a.Slice(i, n).SequenceEqual(b.Slice(i, n)))
                    {
                        diffBytes += n;
                        first ??= off + i;
                    }
                }
            }
            progress?.Report(new CompareProgress(off + len, cmpLen, null));
        }
        return new RawCompareResult(cmpLen, diffBytes, diffChunks, first, mismatch);
    }
}
