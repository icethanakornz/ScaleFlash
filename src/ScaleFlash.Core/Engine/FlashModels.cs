using ScaleFlash.Core.IO;

namespace ScaleFlash.Core.Engine;

public enum FlashPhase { Preparing, Writing, Verifying, Finishing, Completed, Failed, Cancelled }

public sealed record FlashOptions
{
    /// <summary>I/O unit. 4 MiB matches the allocation unit of SDHC cards, so every write covers whole AUs.</summary>
    public int ChunkSize { get; init; } = 4 << 20;

    /// <summary>
    /// Read each chunk from the target first and skip the write when it already matches.
    /// Adaptive: after a run of differing chunks it stops reading for a while, so a blank card costs
    /// almost nothing extra while a card that already holds a similar image is updated much faster.
    /// </summary>
    public bool SkipIdenticalChunks { get; init; } = true;

    /// <summary>Read back every written chunk and compare with the source.</summary>
    public bool Verify { get; init; }

    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Zero the first 4 KiB before starting and write chunk 0 last, so a card whose write was
    /// interrupted never boots a half-written system.
    /// </summary>
    public bool ProtectAgainstInterruption { get; init; } = true;

    public int PrefetchBuffers { get; init; } = 4;

    /// <summary>Called before a write is retried (e.g. to dismount a volume Windows re-mounted).</summary>
    public Action<DeviceIoException>? BeforeRetry { get; init; }
}

public readonly record struct FlashProgress(
    FlashPhase Phase,
    long BytesDone,
    long BytesTotal,
    long BytesWritten,
    long BytesSkipped,
    double BytesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    string? Message)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1) : 0;
}

public sealed record FlashResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? ErrorMessage { get; init; }
    public int Win32Error { get; init; }
    public long? FailedOffset { get; init; }
    public long TotalBytes { get; init; }
    /// <summary>Bytes the write phase got through (written + skipped); equals TotalBytes on success.</summary>
    public long BytesProcessed { get; init; }
    public long BytesWritten { get; init; }
    public long BytesSkipped { get; init; }
    public long BytesVerified { get; init; }
    public TimeSpan WriteDuration { get; init; }
    public TimeSpan VerifyDuration { get; init; }
    public TimeSpan TotalDuration { get; init; }

    /// <summary>Average speed of the write phase (bytes processed incl. skipped / write time).</summary>
    public double WriteBytesPerSecond => WriteDuration.TotalSeconds > 0 ? BytesProcessed / WriteDuration.TotalSeconds : 0;
}

/// <summary>A data mismatch found while verifying.</summary>
public sealed class VerifyMismatchException(long offset) : Exception($"Verify mismatch at offset {offset}.")
{
    public long Offset { get; } = offset;
}
