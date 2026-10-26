using System.Text;
using ScaleFlash.Core.IO;

namespace ScaleFlash.Core.Imaging;

public sealed record PartitionDetail(PartitionEntry Partition, FileSystemInfo FileSystem);

/// <summary>What we know about a disk image file before writing it.</summary>
public sealed record ImageInfo(
    string Path,
    long Size,
    PartitionLayout Layout,
    IReadOnlyList<PartitionDetail> Partitions,
    string? Bootloader,
    IReadOnlyList<string> Warnings)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Bytes that will be written to the card (image size rounded up to whole sectors).</summary>
    public long WriteLength => (Size + 511) / 512 * 512;

    public static ImageInfo Analyze(string path)
    {
        using var dev = FileBlockDevice.OpenRead(path);
        return Analyze(dev, path);
    }

    public static ImageInfo Analyze(IBlockDevice dev, string path)
    {
        long size = dev.Length;
        var warnings = new List<string>();

        byte[] Read(long offset, int length)
        {
            if (offset >= size) throw new EndOfStreamException();
            int n = (int)Math.Min(length, size - offset);
            var buf = new byte[length];
            dev.Read(buf.AsSpan(0, n), offset);
            return buf;
        }

        if (size < 512)
        {
            warnings.Add("ไฟล์เล็กเกินไป ไม่น่าจะเป็นไฟล์ image ของการ์ด");
            return new ImageInfo(path, size, PartitionLayout.Empty, [], null, warnings);
        }
        if (size % 512 != 0)
            warnings.Add("ขนาดไฟล์ไม่ลงตัวกับ 512 ไบต์ ส่วนท้ายจะถูกเติมด้วยศูนย์");

        var layout = PartitionLayout.Parse(Read);
        var details = new List<PartitionDetail>();
        foreach (var p in layout.Partitions)
        {
            var fs = FileSystemInfo.Unknown;
            if (p.StartOffset + 4096 <= size)
            {
                try { fs = FileSystemProbe.Detect(Read(p.StartOffset, 4096)); }
                catch (Exception) { /* informational only */ }
            }
            details.Add(new PartitionDetail(p, fs));
            if (p.EndOffset > size)
                warnings.Add($"พาร์ทิชัน {p.Index + 1} ยาวเกินขนาดไฟล์ (ไฟล์ image อาจไม่ครบ)");
        }

        if (layout.Scheme == PartitionScheme.None)
        {
            var fs = FileSystemProbe.Detect(Read(0, 4096));
            if (fs != FileSystemInfo.Unknown)
                details.Add(new PartitionDetail(new PartitionEntry(0, 0, size, fs.Type, 0, Guid.Empty, false), fs));
            else
                warnings.Add("ไม่พบตารางพาร์ทิชันในไฟล์ อาจไม่ใช่ไฟล์ image ของการ์ด");
        }

        string? bootloader = null;
        try { bootloader = FindBootloader(dev, layout, size); }
        catch (Exception) { /* informational only */ }

        return new ImageInfo(path, size, layout, details, bootloader, warnings);
    }

    /// <summary>Looks for a U-Boot / GRUB banner in the gap before the first partition.</summary>
    private static string? FindBootloader(IBlockDevice dev, PartitionLayout layout, long size)
    {
        long gapEnd = layout.Partitions.Count > 0 ? layout.Partitions.Min(p => p.StartOffset) : 0;
        int len = (int)Math.Min(Math.Min(gapEnd, size), 16L << 20);
        if (len <= 512) return null;
        var buf = new byte[len];
        dev.Read(buf, 0);
        return BootloaderBanner.Find(buf);
    }
}
