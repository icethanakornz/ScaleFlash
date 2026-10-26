using ScaleFlash.Core.Engine;

namespace ScaleFlash.Tests;

public class FlashEngineTests
{
    private const int Chunk = 64 * 1024;

    private static FlashOptions Opts(bool skip = false, bool verify = false, bool protect = true) => new()
    {
        ChunkSize = Chunk,
        SkipIdenticalChunks = skip,
        Verify = verify,
        ProtectAgainstInterruption = protect,
    };

    [Fact]
    public void FullWrite_CopiesSourceAndPadsLastSector()
    {
        int srcLen = 10 * Chunk + 1234; // not a sector multiple
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(srcLen, 1), sectorSize: 1);
        var dst = new MemoryBlockDevice(MemoryBlockDevice.Random(16 * Chunk, 2));
        var before = (byte[])dst.Data.Clone();

        var r = FlashEngine.Run(src, srcLen, dst, Opts(), null, CancellationToken.None);

        Assert.True(r.Success, r.ErrorMessage);
        int aligned = (srcLen + 511) / 512 * 512;
        Assert.Equal(aligned, r.TotalBytes);
        Assert.True(dst.Data.AsSpan(0, srcLen).SequenceEqual(src.Data));
        Assert.True(dst.Data.AsSpan(srcLen, aligned - srcLen).IndexOfAnyExcept((byte)0) < 0, "padding must be zero");
        Assert.True(dst.Data.AsSpan(aligned).SequenceEqual(before.AsSpan(aligned)), "bytes past the image are untouched");
        Assert.Equal(1, dst.FlushCount);
    }

    [Fact]
    public void Protect_ZeroesHeaderFirst_AndWritesChunkZeroLast()
    {
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(8 * Chunk, 3));
        var dst = new MemoryBlockDevice(new byte[8 * Chunk]);

        var r = FlashEngine.Run(src, src.Length, dst, Opts(), null, CancellationToken.None);

        Assert.True(r.Success);
        Assert.Equal((0L, 4096), dst.Writes[0]);
        Assert.Equal((0L, Chunk), dst.Writes[^1]);
        Assert.Equal(Chunk, dst.Writes[1].Offset);
        Assert.True(dst.Data.AsSpan().SequenceEqual(src.Data));
    }

    [Fact]
    public void WithoutProtect_WritesInOrder()
    {
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(4 * Chunk, 4));
        var dst = new MemoryBlockDevice(new byte[4 * Chunk]);
        FlashEngine.Run(src, src.Length, dst, Opts(protect: false), null, CancellationToken.None);
        Assert.Equal([0L, Chunk, 2L * Chunk, 3L * Chunk], dst.Writes.Select(w => w.Offset));
    }

    [Fact]
    public void SkipIdentical_OnlyWritesChangedChunks()
    {
        var data = MemoryBlockDevice.Random(40 * Chunk, 5);
        var src = new MemoryBlockDevice(data);
        var card = (byte[])data.Clone();
        card[3 * Chunk + 17] ^= 1;   // two chunks differ on the card
        card[30 * Chunk + 999] ^= 1;
        var dst = new MemoryBlockDevice(card);

        var r = FlashEngine.Run(src, src.Length, dst, Opts(skip: true), null, CancellationToken.None);

        Assert.True(r.Success);
        Assert.True(dst.Data.AsSpan().SequenceEqual(data));
        // chunk 0 (forced, header protection) + 2 changed chunks
        Assert.Equal(3L * Chunk, r.BytesWritten);
        Assert.Equal(37L * Chunk, r.BytesSkipped);
        Assert.Equal(r.TotalBytes, r.BytesWritten + r.BytesSkipped);
        Assert.Equal(r.TotalBytes, r.BytesProcessed);
    }

    [Fact]
    public void SkipIdentical_BacksOffOnBlankCard()
    {
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(400 * Chunk, 6));
        var dst = new MemoryBlockDevice(new byte[400 * Chunk]);

        var r = FlashEngine.Run(src, src.Length, dst, Opts(skip: true), null, CancellationToken.None);

        Assert.True(r.Success);
        Assert.True(dst.Data.AsSpan().SequenceEqual(src.Data));
        Assert.Equal(r.TotalBytes, r.BytesWritten);
        // 4 probes, pause 16, probe, pause 32, probe, pause 64, probe, pause 128, probe, ...
        Assert.InRange(dst.ReadCount, 4, 12);
    }

    [Fact]
    public void AdaptiveCompare_ResumesAfterMatch()
    {
        var c = new FlashEngine.AdaptiveCompare(true);
        for (int i = 0; i < 4; i++) { Assert.True(c.ShouldCompare()); c.Record(false); }
        for (int i = 0; i < 16; i++) Assert.False(c.ShouldCompare());
        Assert.True(c.ShouldCompare());
        c.Record(true); // probe matched: compare every chunk again
        for (int i = 0; i < 10; i++) { Assert.True(c.ShouldCompare()); c.Record(true); }
    }

    [Fact]
    public void Verify_DetectsBadCard()
    {
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(20 * Chunk, 7));
        var dst = new MemoryBlockDevice(new byte[20 * Chunk]) { CorruptOffset = 12 * Chunk + 100 };

        var r = FlashEngine.Run(src, src.Length, dst, Opts(verify: true), null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Equal(12L * Chunk + 100, r.FailedOffset);
        Assert.Contains("ตรวจสอบไม่ผ่าน", r.ErrorMessage);
    }

    [Fact]
    public void Verify_PassesOnGoodCard_AndOnlyChecksWrittenChunks()
    {
        var data = MemoryBlockDevice.Random(20 * Chunk, 8);
        var card = (byte[])data.Clone();
        card[5 * Chunk] ^= 1;
        var src = new MemoryBlockDevice(data);
        var dst = new MemoryBlockDevice(card);

        var r = FlashEngine.Run(src, src.Length, dst, Opts(skip: true, verify: true), null, CancellationToken.None);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2L * Chunk, r.BytesVerified); // chunk 0 + chunk 5
    }

    [Fact]
    public void TransientWriteErrors_AreRetried()
    {
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(6 * Chunk, 9));
        var dst = new MemoryBlockDevice(new byte[6 * Chunk]);
        int failures = 0, retryHook = 0;
        dst.FailWrite = (off, _) => off == 2 * Chunk && failures++ < 2 ? 121 : null;

        var r = FlashEngine.Run(src, src.Length, dst, Opts() with { BeforeRetry = _ => retryHook++ }, null, CancellationToken.None);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(2, retryHook);
        Assert.True(dst.Data.AsSpan().SequenceEqual(src.Data));
    }

    [Fact]
    public void WriteProtected_FailsWithoutRetry()
    {
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(6 * Chunk, 10));
        var dst = new MemoryBlockDevice(new byte[6 * Chunk]);
        int attempts = 0;
        dst.FailWrite = (_, _) => { attempts++; return 19; };

        var r = FlashEngine.Run(src, src.Length, dst, Opts(), null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Equal(1, attempts);
        Assert.Equal(19, r.Win32Error);
        Assert.Contains("ล็อก", r.ErrorMessage);
    }

    [Fact]
    public void SourceReadError_IsReportedAsSourceProblem()
    {
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(6 * Chunk, 11)) { DisplayName = "image" };
        src.FailRead = (off, _) => off == 4 * Chunk ? 23 : null;
        var dst = new MemoryBlockDevice(new byte[6 * Chunk]);

        var r = FlashEngine.Run(src, src.Length, dst, Opts(), null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("ต้นฉบับ", r.ErrorMessage);
    }

    [Fact]
    public void Cancellation_StopsTheJob()
    {
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(200 * Chunk, 12));
        var dst = new MemoryBlockDevice(MemoryBlockDevice.Random(200 * Chunk, 99));
        using var cts = new CancellationTokenSource();
        dst.FailWrite = (off, _) => { if (off == 10 * Chunk) cts.Cancel(); return null; };

        var r = FlashEngine.Run(src, src.Length, dst, Opts(), null, cts.Token);

        Assert.True(r.Cancelled);
        Assert.False(r.Success);
        Assert.True(r.BytesProcessed < r.TotalBytes);
        Assert.True(dst.Writes.Count < 50);
        // header stays zeroed: an interrupted card must not look valid
        Assert.True(dst.Data.AsSpan(0, 4096).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void TargetTooSmall_Throws()
    {
        var src = new MemoryBlockDevice(new byte[4 * Chunk]);
        var dst = new MemoryBlockDevice(new byte[3 * Chunk]);
        Assert.Throws<ArgumentException>(() => FlashEngine.Run(src, src.Length, dst, Opts(), null, CancellationToken.None));
    }

    [Fact]
    public void Progress_ReachesCompleted()
    {
        var src = new MemoryBlockDevice(MemoryBlockDevice.Random(30 * Chunk, 13));
        var dst = new MemoryBlockDevice(new byte[30 * Chunk]);
        var phases = new List<FlashProgress>();
        var progress = new SyncProgress<FlashProgress>(phases.Add);

        var r = FlashEngine.Run(src, src.Length, dst, Opts(verify: true), progress, CancellationToken.None);

        Assert.True(r.Success);
        Assert.Contains(phases, p => p.Phase == FlashPhase.Writing);
        Assert.Contains(phases, p => p.Phase == FlashPhase.Verifying);
        Assert.Equal(FlashPhase.Completed, phases[^1].Phase);
    }

    [Fact]
    public void FileToFile_RoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "scaleflash-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "a.img"), b = Path.Combine(dir, "b.img");
            var data = MemoryBlockDevice.Random(5 * 1024 * 1024 + 512, 14);
            File.WriteAllBytes(a, data);
            using (var src = Core.IO.FileBlockDevice.OpenRead(a))
            using (var dst = Core.IO.FileBlockDevice.Create(b, src.Length))
            {
                var r = FlashEngine.Run(src, src.Length, dst, new FlashOptions { ChunkSize = 1 << 20, Verify = true }, null, CancellationToken.None);
                Assert.True(r.Success, r.ErrorMessage);
            }
            Assert.Equal(data, File.ReadAllBytes(b));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
