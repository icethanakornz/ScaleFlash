using System.Buffers.Binary;
using System.Text;

namespace ScaleFlash.Core.Imaging;

public enum PartitionScheme { None, Mbr, Gpt }

public sealed record PartitionEntry(
    int Index,
    long StartOffset,
    long Length,
    string TypeName,
    byte MbrType,
    Guid GptType,
    bool Bootable)
{
    public long EndOffset => StartOffset + Length;
}

/// <summary>MBR/GPT partition table parser. Works the same for image files and physical disks.</summary>
public sealed record PartitionLayout(PartitionScheme Scheme, IReadOnlyList<PartitionEntry> Partitions)
{
    public static readonly PartitionLayout Empty = new(PartitionScheme.None, []);

    /// <summary>Byte offset just past the last partition, or 0 when there are none.</summary>
    public long EndOfLastPartition => Partitions.Count == 0 ? 0 : Partitions.Max(p => p.EndOffset);

    public string SchemeName => Scheme switch
    {
        PartitionScheme.Mbr => "MBR",
        PartitionScheme.Gpt => "GPT",
        _ => "ไม่มีตารางพาร์ทิชัน",
    };

    /// <summary>
    /// Parses a partition table. <paramref name="read"/> returns <c>length</c> bytes at <c>offset</c>
    /// (offset and length are always multiples of 512).
    /// </summary>
    public static PartitionLayout Parse(Func<long, int, byte[]> read, int sectorSize = 512)
    {
        byte[] s0 = read(0, 512);
        if (s0.Length < 512 || s0[510] != 0x55 || s0[511] != 0xAA) return Empty;

        var raw = new (byte Boot, byte Type, uint Lba, uint Count)[4];
        for (int i = 0; i < 4; i++)
        {
            var e = s0.AsSpan(446 + i * 16, 16);
            raw[i] = (e[0], e[4], BinaryPrimitives.ReadUInt32LittleEndian(e[8..]), BinaryPrimitives.ReadUInt32LittleEndian(e[12..]));
            if (raw[i].Boot is not (0x00 or 0x80)) return Empty; // not an MBR (e.g. a FAT boot sector)
        }

        if (raw.Any(r => r.Type == 0xEE))
        {
            var gpt = TryParseGpt(read, sectorSize);
            if (gpt is not null) return gpt;
        }

        var parts = new List<PartitionEntry>();
        int index = 0;
        foreach (var r in raw)
        {
            if (r.Type == 0 || r.Count == 0) continue;
            long start = (long)r.Lba * sectorSize, len = (long)r.Count * sectorSize;
            if (r.Type is 0x05 or 0x0F or 0x85)
            {
                ParseExtended(read, sectorSize, r.Lba, parts, ref index);
                continue;
            }
            parts.Add(new PartitionEntry(index++, start, len, MbrTypeName(r.Type), r.Type, Guid.Empty, r.Boot == 0x80));
        }
        return new PartitionLayout(PartitionScheme.Mbr, parts);
    }

    private static void ParseExtended(Func<long, int, byte[]> read, int sectorSize, uint extBase, List<PartitionEntry> parts, ref int index)
    {
        uint ebr = extBase;
        for (int guard = 0; guard < 128; guard++)
        {
            byte[] s;
            try { s = read((long)ebr * sectorSize, 512); }
            catch (Exception) { return; }
            if (s[510] != 0x55 || s[511] != 0xAA) return;
            var e1 = s.AsSpan(446, 16);
            var e2 = s.AsSpan(462, 16);
            byte t1 = e1[4];
            uint l1 = BinaryPrimitives.ReadUInt32LittleEndian(e1[8..]), c1 = BinaryPrimitives.ReadUInt32LittleEndian(e1[12..]);
            if (t1 != 0 && c1 != 0)
                parts.Add(new PartitionEntry(index++, (long)(ebr + l1) * sectorSize, (long)c1 * sectorSize, MbrTypeName(t1), t1, Guid.Empty, e1[0] == 0x80));
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(e2[8..]);
            if (e2[4] == 0 || next == 0) return;
            ebr = extBase + next;
        }
    }

    private static PartitionLayout? TryParseGpt(Func<long, int, byte[]> read, int sectorSize)
    {
        byte[] h;
        try { h = read(sectorSize, 512); } catch (Exception) { return null; }
        if (Encoding.ASCII.GetString(h, 0, 8) != "EFI PART") return null;
        long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(72));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(80));
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(84));
        if (count == 0 || count > 1024 || entrySize < 128 || entrySize > 4096) return null;
        int total = (int)(count * entrySize);
        int toRead = (total + 511) / 512 * 512;
        byte[] entries;
        try { entries = read(entriesLba * sectorSize, toRead); } catch (Exception) { return null; }

        var parts = new List<PartitionEntry>();
        for (int i = 0; i < count; i++)
        {
            var e = entries.AsSpan((int)(i * entrySize), (int)entrySize);
            var type = new Guid(e[..16]);
            if (type == Guid.Empty) continue;
            long first = BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
            long last = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
            if (last < first) continue;
            string name = Encoding.Unicode.GetString(e.Slice(56, 72)).TrimEnd('\0');
            string typeName = GptTypeName(type);
            if (name.Length > 0) typeName += $" \"{name}\"";
            parts.Add(new PartitionEntry(parts.Count, first * sectorSize, (last - first + 1) * sectorSize, typeName, 0, type, false));
        }
        return new PartitionLayout(PartitionScheme.Gpt, parts);
    }

    public static string MbrTypeName(byte t) => t switch
    {
        0x01 => "FAT12",
        0x04 or 0x06 or 0x0E => "FAT16",
        0x07 => "NTFS/exFAT",
        0x0B or 0x0C => "FAT32",
        0x82 => "Linux swap",
        0x83 => "Linux",
        0x8E => "Linux LVM",
        0xEE => "GPT",
        0xEF => "EFI",
        _ => $"Type 0x{t:X2}",
    };

    private static string GptTypeName(Guid g) => g.ToString().ToUpperInvariant() switch
    {
        "0FC63DAF-8483-4772-8E79-3D69D8477DE4" => "Linux",
        "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F" => "Linux swap",
        "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" => "EFI",
        "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" => "Microsoft data",
        "E3C9E316-0B5C-4DB8-817D-F92DF00215AE" => "Microsoft reserved",
        _ => "GPT " + g.ToString()[..8],
    };
}
