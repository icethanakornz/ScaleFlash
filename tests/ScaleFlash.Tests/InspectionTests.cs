using System.IO.Compression;
using ScaleFlash.Core.FileSystems.Ext;
using ScaleFlash.Core.Inspection;
using ScaleFlash.Core.IO;

namespace ScaleFlash.Tests;

public sealed class InspectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scaleflash-insp-" + Guid.NewGuid().ToString("N"));

    public InspectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Unpack(string name)
    {
        string img = Path.Combine(_dir, name + ".img");
        if (File.Exists(img)) return img;
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".img.gz")), CompressionMode.Decompress);
        using var f = File.Create(img);
        gz.CopyTo(f);
        return img;
    }

    [Fact]
    public void Inspection_FindsFileSystemWithoutPartitionTable()
    {
        using var ins = DeviceInspection.Open(FileBlockDevice.OpenRead(Unpack("ext4")), "ext4");
        var p = Assert.Single(ins.Partitions);
        Assert.NotNull(p.Ext);
        Assert.Equal("ext4test", p.Ext!.Superblock.VolumeName);
        Assert.Same(p, ins.MainPartition);
    }

    [Fact]
    public void TreeCompare_ReportsWhatTheJournalChanged()
    {
        string img = Unpack("ext3_dirty");
        using var a = new DeviceReader(FileBlockDevice.OpenRead(img), ownsDevice: true);
        using var b = new DeviceReader(FileBlockDevice.OpenRead(img), ownsDevice: true);
        var now = ExtFileSystem.Open(a, replayJournal: true);     // state after the changes
        var before = ExtFileSystem.Open(b, replayJournal: false); // stale on-disk state

        var r = ExtTreeComparer.Compare(now, before, null, CancellationToken.None);

        Assert.Contains(r.Differences, d => d is { Path: "/new_1.txt", Kind: DiffKind.Added });
        Assert.Contains(r.Differences, d => d is { Path: "/renamed_hello.txt", Kind: DiffKind.Added });
        Assert.Contains(r.Differences, d => d is { Path: "/hello.txt", Kind: DiffKind.Removed });
        Assert.Contains(r.Differences, d => d is { Path: "/many/entry_number_1.txt", Kind: DiffKind.Removed });
        Assert.Contains(r.Differences, d => d is { Path: "/dir/sub/deep/file.txt", Kind: DiffKind.Modified });
        Assert.DoesNotContain(r.Differences, d => d.Path == "/indirect.bin");

        var same = ExtTreeComparer.Compare(now, now, null, CancellationToken.None);
        Assert.Empty(same.Differences);
    }

    [Fact]
    public void RawCompare_FindsDifferences()
    {
        string img = Unpack("ext2_1k");
        string copy = Path.Combine(_dir, "copy.img");
        File.Copy(img, copy);
        using (var f = File.OpenWrite(copy)) { f.Position = 5 * 1024 * 1024 + 7; f.WriteByte(0xAB); }

        using var a = FileBlockDevice.OpenRead(img);
        using var b = FileBlockDevice.OpenRead(img);
        Assert.True(RawComparer.Compare(a, b, null, CancellationToken.None).Identical);

        using var c = FileBlockDevice.OpenRead(copy);
        var r = RawComparer.Compare(c, a, null, CancellationToken.None);
        Assert.False(r.Identical);
        Assert.Equal(1, r.DifferentChunks);
        Assert.Equal(5L * 1024 * 1024, r.FirstDifference);
    }
}
