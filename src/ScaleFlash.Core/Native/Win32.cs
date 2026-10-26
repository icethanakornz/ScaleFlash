using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScaleFlash.Core.Native;

/// <summary>Raw Win32 declarations used for physical-disk access.</summary>
internal static unsafe partial class Win32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    // Error codes we treat specially.
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_WRITE_PROTECT = 19;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_NO_MEDIA_IN_DRIVE = 1112;

    // IOCTL / FSCTL codes.
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    public const uint IOCTL_STORAGE_CHECK_VERIFY2 = 0x002D0800;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_IS_WRITABLE = 0x00070024;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

    public const uint ES_CONTINUOUS = 0x80000000;
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, int inBufferSize,
        void* outBuffer, int outBufferSize, out int bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle file, byte* buffer, int numberOfBytesToRead, out int numberOfBytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(SafeFileHandle file, byte* buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    public static partial nint FindFirstVolume(char* volumeName, int bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextVolume(nint findVolume, char* volumeName, int bufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindVolumeClose(nint findVolume);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathNamesForVolumeName(string volumeName, char* volumePathNames, int bufferLength, out int returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string fileName, char* volumePathName, int bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, char* volumeName, int bufferLength);

    [LibraryImport("kernel32.dll")]
    public static partial uint SetThreadExecutionState(uint flags);

    public const int ERROR_UNRECOGNIZED_VOLUME = 1005;

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string rootPathName, char* volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemNameBuffer, int fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteVolumeMountPoint(string volumeMountPoint);

    /// <summary>Issue an IOCTL with a raw in/out buffer. Returns false and sets <paramref name="error"/> on failure.</summary>
    public static bool Ioctl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output, out int returned, out int error)
    {
        fixed (byte* pin = input)
        fixed (byte* pout = output)
        {
            bool ok = DeviceIoControl(handle, code, pin, input.Length, pout, output.Length, out returned, 0);
            error = ok ? 0 : Marshal.GetLastPInvokeError();
            return ok;
        }
    }
}
