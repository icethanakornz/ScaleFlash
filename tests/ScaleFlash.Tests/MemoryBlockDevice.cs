using ScaleFlash.Core.IO;

namespace ScaleFlash.Tests;

/// <summary>In-memory device with I/O accounting and fault injection.</summary>
internal sealed class MemoryBlockDevice(byte[] data, int sectorSize = 512) : IBlockDevice
{
    public byte[] Data { get; } = data;
    public string DisplayName { get; init; } = "mem";
    public long Length => Data.Length;
    public int SectorSize { get; } = sectorSize;

    public List<(long Offset, int Length)> Writes { get; } = [];
    public int ReadCount { get; private set; }
    public int FlushCount { get; private set; }

    /// <summary>Return a Win32 error code to fail this write, or null to let it through.</summary>
    public Func<long, int, int?>? FailWrite { get; set; }
    public Func<long, int, int?>? FailRead { get; set; }

    /// <summary>Simulates a bad card: corrupts what is stored at this offset.</summary>
    public long? CorruptOffset { get; set; }

    public void Read(Span<byte> buffer, long offset)
    {
        Check(buffer.Length, offset);
        if (FailRead?.Invoke(offset, buffer.Length) is { } err) throw new DeviceIoException("Read", err, offset, DisplayName);
        ReadCount++;
        Data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        Check(buffer.Length, offset);
        if (FailWrite?.Invoke(offset, buffer.Length) is { } err) throw new DeviceIoException("Write", err, offset, DisplayName);
        buffer.CopyTo(Data.AsSpan((int)offset));
        Writes.Add((offset, buffer.Length));
        if (CorruptOffset is { } c && c >= offset && c < offset + buffer.Length) Data[c] ^= 0xFF;
    }

    public void Flush() => FlushCount++;

    public void Dispose() { }

    private void Check(int length, long offset)
    {
        if (offset % SectorSize != 0 || length % SectorSize != 0)
            throw new InvalidOperationException($"Unaligned I/O offset={offset} length={length}");
        if (offset + length > Data.Length)
            throw new InvalidOperationException($"I/O past end offset={offset} length={length}");
    }

    public static byte[] Random(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }
}
