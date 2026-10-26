using System.IO.Compression;
using System.Text;
using ScaleFlash.Core.FileSystems.Ext;
using ScaleFlash.Core.IO;

namespace ScaleFlash.Tests;

/// <summary>
/// Compares the reader against manifests produced by the Linux kernel (see Fixtures/make-fixtures.sh).
/// The *_dirty images were copied while mounted, so they only read correctly with journal replay.
/// </summary>
public sealed class ExtFileSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scaleflash-ext-" + Guid.NewGuid().ToString("N"));

    public ExtFileSystemTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private (ExtFileSystem Fs, DeviceReader Reader) Open(string name, bool replay = true)
    {
        string img = Path.Combine(_dir, name + ".img");
        if (!File.Exists(img))
        {
            using var gz = new GZipStream(File.OpenRead(Fixture(name + ".img.gz")), CompressionMode.Decompress);
            using var outFile = File.Create(img);
            gz.CopyTo(outFile);
        }
        var reader = new DeviceReader(FileBlockDevice.OpenRead(img), ownsDevice: true);
        return (ExtFileSystem.Open(reader, replay), reader);
    }

    private static List<string> Expected(string file) =>
        File.ReadAllLines(Fixture(file), Encoding.UTF8).Where(l => l.Length > 0).ToList();

    [Theory]
    [InlineData("ext2_1k", "ext2")]
    [InlineData("ext3_4k", "ext3")]
    [InlineData("ext4", "ext4")]
    [InlineData("ext3_dirty", "ext3")]
    [InlineData("ext4_dirty", "ext4")]
    public void Manifest_MatchesLinux(string name, string type)
    {
        var (fs, reader) = Open(name);
        using (reader)
        {
            Assert.Equal(type, fs.Superblock.TypeName);
            var actual = ExtTree.Manifest(fs);
            var expected = Expected(name + ".manifest");
            AssertSameLines(expected, actual);
        }
    }

    [Theory]
    [InlineData("ext3_dirty")]
    [InlineData("ext4_dirty")]
    public void DirtyImage_NeedsJournal(string name)
    {
        var (fs, reader) = Open(name);
        using (reader)
        {
            Assert.True(fs.Superblock.NeedsRecovery || fs.ReplayedBlocks > 0);
            Assert.True(fs.ReplayedBlocks > 0, "journal should contain blocks to replay");
            Assert.True(fs.ReplayedTransactions > 0);
            Assert.Null(fs.Warning);
        }

        // Without replay we must see exactly what Linux sees with "-o noload".
        var (raw, rawReader) = Open(name, replay: false);
        using (rawReader)
        {
            Assert.Equal(0, raw.ReplayedBlocks);
            AssertSameLines(Expected(name + ".noreplay.manifest"), ExtTree.Manifest(raw));
        }
    }

    [Fact]
    public void Lookup_FollowsSymlinks_AndReadsFiles()
    {
        var (fs, reader) = Open("ext4");
        using (reader)
        {
            var link = fs.Lookup("/link_fast", followLast: false)!;
            Assert.Equal(ExtFileType.Symlink, link.Type);
            Assert.Equal("hello.txt", fs.ReadSymlink(link));
            var target = fs.Lookup("/link_fast")!;
            Assert.Equal("hello world\n", Encoding.UTF8.GetString(fs.ReadAllBytes(target)));
            Assert.Equal("deep file\n", Encoding.UTF8.GetString(fs.ReadAllBytes(fs.Lookup("/dir/sub/../sub/deep/file.txt")!)));
            Assert.Null(fs.Lookup("/does/not/exist"));
            var thai = fs.Lookup("/thai/ไฟล์ภาษาไทย.txt")!;
            Assert.Equal("สวัสดีครับ เครื่องชั่ง\n", Encoding.UTF8.GetString(fs.ReadAllBytes(thai)));
        }
    }

    [Fact]
    public void Extract_CopiesFolderToWindows()
    {
        var (fs, reader) = Open("ext3_4k");
        using (reader)
        {
            string dest = Path.Combine(_dir, "out");
            Directory.CreateDirectory(dest);
            var (files, bytes, _) = ExtTree.Extract(fs, "/dir", dest);
            Assert.Equal(1, files);
            Assert.Equal("deep file\n", File.ReadAllText(Path.Combine(dest, "dir", "sub", "deep", "file.txt")));
            Assert.Equal(10, bytes);

            ExtTree.Extract(fs, "/link_slow", dest);
            Assert.StartsWith("/this/is/a/very/long", File.ReadAllText(Path.Combine(dest, "link_slow.symlink.txt")));
        }
    }

    [Fact]
    public void DecodeName_FallsBackToLatin1()
    {
        Assert.Equal("Räucherlachs", ExtFileSystem.DecodeName([0x52, 0xE4, 0x75, 0x63, 0x68, 0x65, 0x72, 0x6C, 0x61, 0x63, 0x68, 0x73]));
        Assert.Equal("ไฟล์", ExtFileSystem.DecodeName(Encoding.UTF8.GetBytes("ไฟล์")));
    }

    [Theory]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("CON", "_CON")]
    [InlineData("com1.log", "_com1.log")]
    [InlineData("trailing. ", "trailing")]
    public void WindowsSafeName(string input, string expected) => Assert.Equal(expected, ExtTree.WindowsSafeName(input));

    private static void AssertSameLines(List<string> expected, List<string> actual)
    {
        var missing = expected.Except(actual).Take(5).ToList();
        var extra = actual.Except(expected).Take(5).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0 && expected.Count == actual.Count,
            $"expected {expected.Count} lines, got {actual.Count}\nmissing:\n  {string.Join("\n  ", missing)}\nextra:\n  {string.Join("\n  ", extra)}");
        Assert.Equal(expected, actual);
    }
}
