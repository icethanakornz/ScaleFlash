using System.Buffers.Binary;
using System.Text;
using ScaleFlash.Core.Imaging;

namespace ScaleFlash.Tests;

public class ImagingTests
{
    /// <summary>Builds a small image shaped like the scale card: MBR, U-Boot gap, one Linux ext3 partition.</summary>
    private static byte[] BuildScaleLikeImage(out long partStart, out long partLen)
    {
        const int sectors = 4096; // 2 MiB
        var img = new byte[sectors * 512];
        partStart = 1024L * 512;
        partLen = (sectors - 1024) * 512L;

        var e = img.AsSpan(446, 16);
        e[4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], sectors - 1024);
        img[510] = 0x55; img[511] = 0xAA;

        Encoding.ASCII.GetBytes("junk U-Boot SPL U-Boot 2017.03 (Nov 16 2021 - 15:01:43 +0800)").CopyTo(img, 70000);

        var sb = img.AsSpan((int)partStart + 1024, 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], 384);        // blocks (4 KiB)
        BinaryPrimitives.WriteUInt32LittleEndian(sb[12..], 96);        // free blocks
        BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], 2);         // log block size => 4096
        BinaryPrimitives.WriteUInt16LittleEndian(sb[0x38..], 0xEF53);
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x5C..], 0x3C);    // has_journal ...
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x60..], 0x6);     // filetype + needs_recovery
        BinaryPrimitives.WriteUInt32LittleEndian(sb[0x64..], 0x3);
        Encoding.ASCII.GetBytes("rootfs").CopyTo(sb[0x78..]);
        return img;
    }

    [Fact]
    public void Analyze_ScaleLikeImage()
    {
        var img = BuildScaleLikeImage(out long start, out long len);
        var info = ImageInfo.Analyze(new MemoryBlockDevice(img, 1), "test.img");

        Assert.Equal(PartitionScheme.Mbr, info.Layout.Scheme);
        var p = Assert.Single(info.Partitions);
        Assert.Equal(start, p.Partition.StartOffset);
        Assert.Equal(len, p.Partition.Length);
        Assert.Equal("Linux", p.Partition.TypeName);
        Assert.Equal("ext3", p.FileSystem.Type);
        Assert.Equal("rootfs", p.FileSystem.Label);
        Assert.Equal(384L * 4096, p.FileSystem.TotalBytes);
        Assert.Equal(288L * 4096, p.FileSystem.UsedBytes);
        Assert.True(p.FileSystem.NeedsRecovery);
        Assert.Equal("U-Boot 2017.03", info.Bootloader);
        Assert.Empty(info.Warnings);
    }

    [Fact]
    public void Analyze_RandomFile_Warns()
    {
        var info = ImageInfo.Analyze(new MemoryBlockDevice(MemoryBlockDevice.Random(100_000, 1), 1), "x.bin");
        Assert.Equal(PartitionScheme.None, info.Layout.Scheme);
        Assert.NotEmpty(info.Warnings);
    }

    [Fact]
    public void Parse_Gpt()
    {
        var img = new byte[64 * 1024];
        img[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(446 + 12), 127);
        img[510] = 0x55; img[511] = 0xAA;
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(img, 512);
        BinaryPrimitives.WriteInt64LittleEndian(img.AsSpan(512 + 72), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(512 + 80), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(512 + 84), 128);
        var entry = img.AsSpan(1024, 128);
        new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4").TryWriteBytes(entry);
        BinaryPrimitives.WriteInt64LittleEndian(entry[32..], 34);
        BinaryPrimitives.WriteInt64LittleEndian(entry[40..], 99);
        Encoding.Unicode.GetBytes("root").CopyTo(entry[56..]);

        var layout = PartitionLayout.Parse((o, l) => img.AsSpan((int)o, l).ToArray());

        Assert.Equal(PartitionScheme.Gpt, layout.Scheme);
        var p = Assert.Single(layout.Partitions);
        Assert.Equal(34 * 512, p.StartOffset);
        Assert.Equal(66 * 512, p.Length);
        Assert.Equal("Linux \"root\"", p.TypeName);
        Assert.Equal(100 * 512, layout.EndOfLastPartition);
    }

    [Fact]
    public void Parse_FatBootSector_IsNotAnMbr()
    {
        var bs = new byte[512];
        bs[0] = 0xEB; bs[1] = 0x58; bs[2] = 0x90;
        Encoding.ASCII.GetBytes("MSDOS5.0").CopyTo(bs, 3);
        bs[446] = 0x33; // boot code bytes where the partition table would be
        bs[510] = 0x55; bs[511] = 0xAA;
        Assert.Equal(PartitionScheme.None, PartitionLayout.Parse((o, l) => bs.AsSpan((int)o, l).ToArray()).Scheme);
    }

    /// <summary>Runs against the real scale image when it is present on this machine.</summary>
    [Fact]
    public void Analyze_RealScaleImage_WhenAvailable()
    {
        const string path = @"D:\SM5300XP_TOP_20261006";
        if (!File.Exists(path)) return;
        var info = ImageInfo.Analyze(path);
        Assert.Equal(7_813_988_352, info.Size);
        var p = Assert.Single(info.Partitions);
        Assert.Equal(16384L * 512, p.Partition.StartOffset);
        Assert.Equal("ext3", p.FileSystem.Type);
        Assert.Equal("U-Boot 2017.03", info.Bootloader);
    }
}
