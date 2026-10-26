using Microsoft.Win32.SafeHandles;

namespace ScaleFlash.Core.IO;

/// <summary>A regular file used as an image source or a backup/test target.</summary>
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    private long _length;

    private FileBlockDevice(SafeFileHandle handle, string path)
    {
        _handle = handle;
        Path = path;
        _length = RandomAccess.GetLength(handle);
    }

    public string Path { get; }
    public string DisplayName => Path;
    public long Length => _length;
    public int SectorSize => 1;

    public static FileBlockDevice OpenRead(string path)
    {
        try
        {
            var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            return new FileBlockDevice(h, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DeviceIoException("Open", DeviceIoException.Win32FromException(ex), 0, path, ex);
        }
    }

    /// <summary>Opens an existing file for read/write without truncating it.</summary>
    public static FileBlockDevice OpenReadWrite(string path)
    {
        var h = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        return new FileBlockDevice(h, path);
    }

    /// <summary>Creates (or overwrites) a file of exactly <paramref name="length"/> bytes.</summary>
    public static FileBlockDevice Create(string path, long length)
    {
        try
        {
            var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.None, preallocationSize: length);
            RandomAccess.SetLength(h, length);
            return new FileBlockDevice(h, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DeviceIoException("Create", DeviceIoException.Win32FromException(ex), 0, path, ex);
        }
    }

    public void Read(Span<byte> buffer, long offset)
    {
        int done = 0;
        try
        {
            while (done < buffer.Length)
            {
                int n = RandomAccess.Read(_handle, buffer[done..], offset + done);
                if (n == 0) throw new EndOfStreamException($"Unexpected end of file at {offset + done}.");
                done += n;
            }
        }
        catch (Exception ex) when (ex is IOException and not EndOfStreamException and not DeviceIoException)
        {
            throw new DeviceIoException("Read", DeviceIoException.Win32FromException(ex), offset + done, Path, ex);
        }
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        try
        {
            RandomAccess.Write(_handle, buffer, offset);
            if (offset + buffer.Length > _length) _length = offset + buffer.Length;
        }
        catch (Exception ex) when (ex is IOException and not DeviceIoException)
        {
            throw new DeviceIoException("Write", DeviceIoException.Win32FromException(ex), offset, Path, ex);
        }
    }

    public void Flush() => RandomAccess.FlushToDisk(_handle);

    public void Dispose() => _handle.Dispose();
}
