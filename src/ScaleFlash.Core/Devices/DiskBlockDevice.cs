using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ScaleFlash.Core.IO;
using ScaleFlash.Core.Native;

namespace ScaleFlash.Core.Devices;

/// <summary>
/// Unbuffered raw access to \\.\PhysicalDriveN. Offsets and lengths must be sector multiples and
/// buffers must come from <see cref="AlignedBuffer"/>.
/// </summary>
public sealed unsafe class DiskBlockDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;

    private DiskBlockDevice(SafeFileHandle handle, string path, long length, int sectorSize)
    {
        _handle = handle;
        DisplayName = path;
        Length = length;
        SectorSize = sectorSize;
    }

    public string DisplayName { get; }
    public long Length { get; }
    public int SectorSize { get; }

    public static DiskBlockDevice Open(int diskNumber, bool writable) => Open($@"\\.\PhysicalDrive{diskNumber}", writable);

    public static DiskBlockDevice Open(string path, bool writable)
    {
        uint access = Win32.GENERIC_READ | (writable ? Win32.GENERIC_WRITE : 0);
        var h = Win32.CreateFile(path, access, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, 0,
            Win32.OPEN_EXISTING, Win32.FILE_FLAG_NO_BUFFERING, 0);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new DeviceIoException("Open", err, 0, path);
        }

        Span<byte> geo = stackalloc byte[256];
        if (!Win32.Ioctl(h, Win32.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, [], geo, out _, out int gerr))
        {
            h.Dispose();
            throw new DeviceIoException("Open", gerr, 0, path);
        }
        int sector = BinaryPrimitives.ReadInt32LittleEndian(geo[20..]);
        long size = BinaryPrimitives.ReadInt64LittleEndian(geo[24..]);
        if (sector is not (512 or 1024 or 2048 or 4096)) sector = 512;
        return new DiskBlockDevice(h, path, size, sector);
    }

    public void Read(Span<byte> buffer, long offset)
    {
        CheckAligned(buffer.Length, offset);
        fixed (byte* p = buffer)
        {
            NativeOverlapped ov = MakeOverlapped(offset);
            if (!Win32.ReadFile(_handle, p, buffer.Length, out int n, &ov))
                throw new DeviceIoException("Read", Marshal.GetLastPInvokeError(), offset, DisplayName);
            if (n != buffer.Length)
                throw new DeviceIoException("Read", 38 /* ERROR_HANDLE_EOF */, offset + n, DisplayName);
        }
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        CheckAligned(buffer.Length, offset);
        fixed (byte* p = buffer)
        {
            NativeOverlapped ov = MakeOverlapped(offset);
            if (!Win32.WriteFile(_handle, p, buffer.Length, out int n, &ov))
                throw new DeviceIoException("Write", Marshal.GetLastPInvokeError(), offset, DisplayName);
            if (n != buffer.Length)
                throw new DeviceIoException("Write", 112 /* ERROR_DISK_FULL */, offset + n, DisplayName);
        }
    }

    public void Flush()
    {
        if (!Win32.FlushFileBuffers(_handle))
            throw new DeviceIoException("Flush", Marshal.GetLastPInvokeError(), 0, DisplayName);
    }

    /// <summary>Asks Windows to re-read the partition table (after a successful write).</summary>
    public void UpdateProperties() => Win32.Ioctl(_handle, Win32.IOCTL_DISK_UPDATE_PROPERTIES, [], [], out _, out _);

    private void CheckAligned(int length, long offset)
    {
        if (length % SectorSize != 0 || offset % SectorSize != 0)
            throw new ArgumentException($"Unaligned disk I/O: offset {offset}, length {length}, sector {SectorSize}.");
    }

    private static NativeOverlapped MakeOverlapped(long offset) => new()
    {
        OffsetLow = (int)(offset & 0xFFFFFFFF),
        OffsetHigh = (int)(offset >> 32),
    };

    public void Dispose() => _handle.Dispose();
}
