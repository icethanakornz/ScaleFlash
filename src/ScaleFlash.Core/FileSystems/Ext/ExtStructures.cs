using System.Buffers.Binary;
using System.Text;

namespace ScaleFlash.Core.FileSystems.Ext;

public sealed class ExtFormatException(string message) : Exception(message);

/// <summary>The ext2/3/4 superblock fields the reader and the UI need.</summary>
public sealed record ExtSuperblock
{
    public const ushort Magic = 0xEF53;

    // feature bits
    public const uint CompatHasJournal = 0x4;
    public const uint IncompatFiletype = 0x2;
    public const uint IncompatRecover = 0x4;
    public const uint IncompatJournalDev = 0x8;
    public const uint IncompatMetaBg = 0x10;
    public const uint IncompatExtents = 0x40;
    public const uint Incompat64Bit = 0x80;
    public const uint IncompatFlexBg = 0x200;
    public const uint IncompatInlineData = 0x8000;
    public const uint RoCompatHugeFile = 0x8;
    public const uint RoCompatGdtCsum = 0x10;
    public const uint RoCompatDirNlink = 0x20;
    public const uint RoCompatExtraIsize = 0x40;
    public const uint RoCompatBigalloc = 0x200;
    public const uint RoCompatMetadataCsum = 0x400;

    public long BlocksCount { get; init; }
    public long FreeBlocks { get; init; }
    public uint InodesCount { get; init; }
    public uint FreeInodes { get; init; }
    public uint FirstDataBlock { get; init; }
    public int BlockSize { get; init; }
    public uint BlocksPerGroup { get; init; }
    public uint InodesPerGroup { get; init; }
    public int InodeSize { get; init; }
    public uint RevLevel { get; init; }
    public uint FeatureCompat { get; init; }
    public uint FeatureIncompat { get; init; }
    public uint FeatureRoCompat { get; init; }
    public Guid Uuid { get; init; }
    public string VolumeName { get; init; } = "";
    public string LastMountedOn { get; init; } = "";
    public DateTimeOffset? MountTime { get; init; }
    public DateTimeOffset? WriteTime { get; init; }
    public DateTimeOffset? LastCheck { get; init; }
    public DateTimeOffset? CreatedTime { get; init; }
    public int MountCount { get; init; }
    public int MaxMountCount { get; init; }
    public ushort State { get; init; }
    public uint JournalInode { get; init; }
    public int DescSize { get; init; }
    public uint ErrorCount { get; init; }

    public bool Is64Bit => (FeatureIncompat & Incompat64Bit) != 0;
    public bool HasJournal => (FeatureCompat & CompatHasJournal) != 0;
    public bool NeedsRecovery => (FeatureIncompat & IncompatRecover) != 0;
    public bool HasFiletype => (FeatureIncompat & IncompatFiletype) != 0;

    /// <summary>s_state bit 1 = cleanly unmounted, bit 2 = errors detected.</summary>
    public bool CleanlyUnmounted => (State & 1) != 0;
    public bool HasErrors => (State & 2) != 0 || ErrorCount > 0;

    public long GroupCount => (BlocksCount - FirstDataBlock + BlocksPerGroup - 1) / BlocksPerGroup;
    public long UsedBytes => Math.Max(0, BlocksCount - FreeBlocks) * BlockSize;
    public long TotalBytes => BlocksCount * BlockSize;

    public string TypeName =>
        (FeatureIncompat & (IncompatExtents | Incompat64Bit | IncompatFlexBg)) != 0 ||
        (FeatureRoCompat & (RoCompatHugeFile | RoCompatGdtCsum | RoCompatDirNlink | RoCompatExtraIsize | RoCompatMetadataCsum)) != 0
            ? "ext4" : HasJournal ? "ext3" : "ext2";

    /// <param name="sb">The 1024 bytes at offset 1024 of the partition.</param>
    public static ExtSuperblock Parse(ReadOnlySpan<byte> sb)
    {
        if (sb.Length < 1024 || BinaryPrimitives.ReadUInt16LittleEndian(sb[0x38..]) != Magic)
            throw new ExtFormatException("ไม่พบระบบไฟล์ ext2/3/4");
        uint logBlock = U32(sb, 24);
        if (logBlock > 6) throw new ExtFormatException("ขนาดบล็อกผิดปกติ");
        uint incompat = U32(sb, 0x60);
        bool is64 = (incompat & Incompat64Bit) != 0;
        long blocks = U32(sb, 4), free = U32(sb, 12);
        if (is64)
        {
            blocks |= (long)U32(sb, 0x150) << 32;
            free |= (long)U32(sb, 0x158) << 32;
        }
        uint rev = U32(sb, 76);
        int inodeSize = rev == 0 ? 128 : BinaryPrimitives.ReadUInt16LittleEndian(sb[88..]);
        int descSize = is64 ? BinaryPrimitives.ReadUInt16LittleEndian(sb[0xFE..]) : 32;
        if (descSize < 32) descSize = 32;
        var s = new ExtSuperblock
        {
            InodesCount = U32(sb, 0),
            BlocksCount = blocks,
            FreeBlocks = free,
            FreeInodes = U32(sb, 16),
            FirstDataBlock = U32(sb, 20),
            BlockSize = 1024 << (int)logBlock,
            BlocksPerGroup = U32(sb, 32),
            InodesPerGroup = U32(sb, 40),
            MountTime = Time(U32(sb, 44)),
            WriteTime = Time(U32(sb, 48)),
            MountCount = BinaryPrimitives.ReadUInt16LittleEndian(sb[52..]),
            MaxMountCount = BinaryPrimitives.ReadInt16LittleEndian(sb[54..]),
            State = BinaryPrimitives.ReadUInt16LittleEndian(sb[58..]),
            LastCheck = Time(U32(sb, 64)),
            RevLevel = rev,
            InodeSize = inodeSize,
            FeatureCompat = U32(sb, 0x5C),
            FeatureIncompat = incompat,
            FeatureRoCompat = U32(sb, 0x64),
            Uuid = new Guid(sb.Slice(0x68, 16), bigEndian: true),
            VolumeName = Str(sb.Slice(0x78, 16)),
            LastMountedOn = Str(sb.Slice(0x88, 64)),
            JournalInode = U32(sb, 0xE0),
            DescSize = descSize,
            CreatedTime = Time(U32(sb, 0x108)),
            ErrorCount = U32(sb, 0x194),
        };
        if (s.BlocksPerGroup == 0 || s.InodesPerGroup == 0 || s.InodeSize < 128 || s.InodeSize > s.BlockSize)
            throw new ExtFormatException("โครงสร้างระบบไฟล์เสียหาย");
        return s;
    }

    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    private static string Str(ReadOnlySpan<byte> b)
    {
        int end = b.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? b[..end] : b);
    }

    internal static DateTimeOffset? Time(uint seconds) => seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds);
}

public enum ExtFileType { Unknown, File, Directory, Symlink, CharDevice, BlockDevice, Fifo, Socket }

public sealed class ExtInode
{
    public const uint ExtentsFlag = 0x80000;
    public const uint InlineDataFlag = 0x10000000;

    internal ExtInode(uint number, ReadOnlySpan<byte> raw, int inodeSize)
    {
        Number = number;
        Mode = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        Uid = BinaryPrimitives.ReadUInt16LittleEndian(raw[2..]) | (uint)BinaryPrimitives.ReadUInt16LittleEndian(raw[0x78..]) << 16;
        Gid = BinaryPrimitives.ReadUInt16LittleEndian(raw[24..]) | (uint)BinaryPrimitives.ReadUInt16LittleEndian(raw[0x7A..]) << 16;
        Size = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]) | (long)BinaryPrimitives.ReadUInt32LittleEndian(raw[108..]) << 32;
        LinksCount = BinaryPrimitives.ReadUInt16LittleEndian(raw[26..]);
        Flags = BinaryPrimitives.ReadUInt32LittleEndian(raw[32..]);
        BlockArea = raw.Slice(40, 60).ToArray();

        int extra = inodeSize > 128 ? BinaryPrimitives.ReadUInt16LittleEndian(raw[0x80..]) : 0;
        Mtime = Decode(BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]), extra >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(raw[0x88..]) : 0);
        Ctime = Decode(BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]), extra >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(raw[0x84..]) : 0);
    }

    public uint Number { get; }
    public ushort Mode { get; }
    public uint Uid { get; }
    public uint Gid { get; }
    public long Size { get; }
    public ushort LinksCount { get; }
    public uint Flags { get; }
    public DateTimeOffset Mtime { get; }
    public DateTimeOffset Ctime { get; }
    internal byte[] BlockArea { get; }

    public bool UsesExtents => (Flags & ExtentsFlag) != 0;
    public bool HasInlineData => (Flags & InlineDataFlag) != 0;

    public ExtFileType Type => (Mode & 0xF000) switch
    {
        0x8000 => ExtFileType.File,
        0x4000 => ExtFileType.Directory,
        0xA000 => ExtFileType.Symlink,
        0x2000 => ExtFileType.CharDevice,
        0x6000 => ExtFileType.BlockDevice,
        0x1000 => ExtFileType.Fifo,
        0xC000 => ExtFileType.Socket,
        _ => ExtFileType.Unknown,
    };

    /// <summary>"rwxr-xr-x" style permission string.</summary>
    public string Permissions
    {
        get
        {
            Span<char> c = stackalloc char[9];
            const string rwx = "rwxrwxrwx";
            for (int i = 0; i < 9; i++) c[i] = (Mode & (1 << (8 - i))) != 0 ? rwx[i] : '-';
            return new string(c);
        }
    }

    private static DateTimeOffset Decode(uint seconds, uint extra)
    {
        long s = (int)seconds + ((long)(extra & 3) << 32);
        return DateTimeOffset.FromUnixTimeSeconds(s);
    }
}

public sealed record ExtDirEntry(string Name, uint Inode, ExtFileType Type);
