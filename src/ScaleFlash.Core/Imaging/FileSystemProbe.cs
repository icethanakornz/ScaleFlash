using System.Buffers.Binary;
using System.Text;

namespace ScaleFlash.Core.Imaging;

public sealed record FileSystemInfo(
    string Type,
    string Label,
    long TotalBytes,
    long? UsedBytes,
    bool NeedsRecovery)
{
    public static readonly FileSystemInfo Unknown = new("ไม่ทราบชนิด", "", 0, null, false);

    public double? UsedFraction => UsedBytes is { } u && TotalBytes > 0 ? (double)u / TotalBytes : null;
}

/// <summary>Identifies the file system at the start of a partition (informational only).</summary>
public static class FileSystemProbe
{
    /// <param name="head">At least the first 4096 bytes of the partition.</param>
    public static FileSystemInfo Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4096) return FileSystemInfo.Unknown;
        return TryExt(head) ?? TryNtfs(head) ?? TryExFat(head) ?? TryFat(head) ?? FileSystemInfo.Unknown;
    }

    private static FileSystemInfo? TryExt(ReadOnlySpan<byte> head)
    {
        var sb = head.Slice(1024, 1024);
        if (BinaryPrimitives.ReadUInt16LittleEndian(sb[0x38..]) != 0xEF53) return null;
        uint logBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb[24..]);
        if (logBlock > 6) return null;
        long blockSize = 1024L << (int)logBlock;
        uint compat = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x5C..]);
        uint incompat = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x60..]);
        uint roCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x64..]);
        bool is64 = (incompat & 0x80) != 0;
        long blocks = BinaryPrimitives.ReadUInt32LittleEndian(sb[4..]);
        long free = BinaryPrimitives.ReadUInt32LittleEndian(sb[12..]);
        if (is64)
        {
            blocks |= (long)BinaryPrimitives.ReadUInt32LittleEndian(sb[0x150..]) << 32;
            free |= (long)BinaryPrimitives.ReadUInt32LittleEndian(sb[0x158..]) << 32;
        }

        bool ext4 = (incompat & (0x40 | 0x80 | 0x200)) != 0 || (roCompat & (0x8 | 0x10 | 0x20 | 0x40 | 0x400)) != 0;
        bool journal = (compat & 0x4) != 0;
        string type = ext4 ? "ext4" : journal ? "ext3" : "ext2";
        string label = Encoding.UTF8.GetString(sb.Slice(0x78, 16)).TrimEnd('\0');
        return new FileSystemInfo(type, label, blocks * blockSize, Math.Max(0, blocks - free) * blockSize, (incompat & 0x4) != 0);
    }

    private static FileSystemInfo? TryNtfs(ReadOnlySpan<byte> head)
    {
        if (Encoding.ASCII.GetString(head.Slice(3, 8)) != "NTFS    ") return null;
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(head[11..]);
        long sectors = BinaryPrimitives.ReadInt64LittleEndian(head[40..]);
        return new FileSystemInfo("NTFS", "", sectors * bps, null, false);
    }

    private static FileSystemInfo? TryExFat(ReadOnlySpan<byte> head)
    {
        if (Encoding.ASCII.GetString(head.Slice(3, 8)) != "EXFAT   ") return null;
        long sectors = BinaryPrimitives.ReadInt64LittleEndian(head[72..]);
        int bpsShift = head[108];
        int percent = head[112];
        long total = sectors << bpsShift;
        long? used = percent <= 100 ? total * percent / 100 : null;
        return new FileSystemInfo("exFAT", "", total, used, false);
    }

    private static FileSystemInfo? TryFat(ReadOnlySpan<byte> head)
    {
        if (head[510] != 0x55 || head[511] != 0xAA) return null;
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(head[11..]);
        int spc = head[13];
        if (bps is not (512 or 1024 or 2048 or 4096) || spc == 0 || (spc & (spc - 1)) != 0) return null;
        long total16 = BinaryPrimitives.ReadUInt16LittleEndian(head[19..]);
        long total32 = BinaryPrimitives.ReadUInt32LittleEndian(head[32..]);
        long totalBytes = (total16 != 0 ? total16 : total32) * bps;

        string t32 = Encoding.ASCII.GetString(head.Slice(82, 8));
        if (t32.StartsWith("FAT32", StringComparison.Ordinal))
        {
            string label = Encoding.ASCII.GetString(head.Slice(71, 11)).Trim();
            long? used = null;
            // FSInfo sector (normally sector 1) carries the free cluster count.
            int fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(head[48..]);
            if (fsInfoSector is > 0 and < 8 && (fsInfoSector + 1) * bps <= head.Length)
            {
                var fi = head.Slice(fsInfoSector * bps, bps);
                if (BinaryPrimitives.ReadUInt32LittleEndian(fi) == 0x41615252 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(fi[484..]) == 0x61417272)
                {
                    uint freeClusters = BinaryPrimitives.ReadUInt32LittleEndian(fi[488..]);
                    if (freeClusters != 0xFFFFFFFF)
                        used = Math.Max(0, totalBytes - (long)freeClusters * spc * bps);
                }
            }
            return new FileSystemInfo("FAT32", label == "NO NAME" ? "" : label, totalBytes, used, false);
        }

        string t16 = Encoding.ASCII.GetString(head.Slice(54, 8));
        if (t16.StartsWith("FAT", StringComparison.Ordinal))
        {
            string label = Encoding.ASCII.GetString(head.Slice(43, 11)).Trim();
            return new FileSystemInfo(t16.Trim(), label == "NO NAME" ? "" : label, totalBytes, null, false);
        }
        return null;
    }
}
