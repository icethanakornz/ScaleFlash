namespace ScaleFlash.Core.IO;

/// <summary>A random-access byte device: an image file, an output file or a physical disk.</summary>
public interface IBlockDevice : IDisposable
{
    string DisplayName { get; }

    /// <summary>Total length in bytes.</summary>
    long Length { get; }

    /// <summary>Required alignment for offsets and lengths (1 for plain files).</summary>
    int SectorSize { get; }

    /// <summary>Reads exactly <c>buffer.Length</c> bytes at <paramref name="offset"/>.</summary>
    void Read(Span<byte> buffer, long offset);

    void Write(ReadOnlySpan<byte> buffer, long offset);

    void Flush();
}

/// <summary>An I/O failure carrying the Win32 error code so the UI can explain it.</summary>
public sealed class DeviceIoException : IOException
{
    public DeviceIoException(string operation, int win32Error, long offset, string device, Exception? inner = null)
        : base($"{operation} failed on {device} at offset {offset} (Win32 error {win32Error}).", inner)
    {
        Operation = operation;
        Win32Error = win32Error;
        Offset = offset;
        Device = device;
    }

    public string Operation { get; }
    public int Win32Error { get; }
    public long Offset { get; }
    public string Device { get; }

    internal static int Win32FromException(Exception ex)
    {
        // HRESULT_FROM_WIN32 => 0x8007xxxx
        int hr = ex.HResult;
        if ((hr & 0xFFFF0000) == 0x80070000) return hr & 0xFFFF;
        return hr;
    }
}
