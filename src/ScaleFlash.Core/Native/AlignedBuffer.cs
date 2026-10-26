using System.Runtime.InteropServices;

namespace ScaleFlash.Core.Native;

/// <summary>
/// Page-aligned unmanaged buffer. Unbuffered (FILE_FLAG_NO_BUFFERING) disk I/O requires
/// sector-aligned memory, so every I/O buffer in the engine comes from here.
/// </summary>
public sealed unsafe class AlignedBuffer : IDisposable
{
    public const int Alignment = 4096;

    private byte* _ptr;

    public AlignedBuffer(int size)
    {
        if (size <= 0 || size % Alignment != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be a positive multiple of 4096.");
        Size = size;
        _ptr = (byte*)NativeMemory.AlignedAlloc((nuint)size, Alignment);
        NativeMemory.Clear(_ptr, (nuint)size);
    }

    public int Size { get; }

    public byte* Pointer => _ptr != null ? _ptr : throw new ObjectDisposedException(nameof(AlignedBuffer));

    public Span<byte> Span => new(Pointer, Size);

    public Span<byte> Slice(int start, int length) => Span.Slice(start, length);

    public void Dispose()
    {
        if (_ptr != null)
        {
            NativeMemory.AlignedFree(_ptr);
            _ptr = null;
        }
        GC.SuppressFinalize(this);
    }

    ~AlignedBuffer() => Dispose();
}
