using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ScaleFlash.Core.Imaging;
using ScaleFlash.Core.Native;

namespace ScaleFlash.Core.Devices;

/// <summary>Enumerates \\.\PhysicalDriveN devices and describes them.</summary>
public static class DiskEnumerator
{
    private const int MaxDisks = 64;

    private static readonly Lazy<HashSet<int>> SystemDisks = new(ComputeSystemDisks);

    /// <param name="readPartitions">Read sector 0 of each disk with media (needs administrator rights).</param>
    /// <param name="skipDeepProbe">Disks currently being written: only basic info is gathered.</param>
    public static List<DiskInfo> Enumerate(bool readPartitions = true, IReadOnlySet<int>? skipDeepProbe = null)
    {
        var volumes = VolumeManager.GetVolumesByDisk();
        var system = SystemDisks.Value;
        var result = new List<DiskInfo>();
        for (int n = 0; n < MaxDisks; n++)
        {
            var info = Probe(n, volumes.GetValueOrDefault(n), system.Contains(n),
                readPartitions && skipDeepProbe?.Contains(n) != true);
            if (info is not null) result.Add(info);
        }
        return result;
    }

    public static DiskInfo? Probe(int number, bool readPartitions = true)
        => Probe(number, VolumeManager.GetVolumesByDisk().GetValueOrDefault(number), SystemDisks.Value.Contains(number), readPartitions);

    private static unsafe DiskInfo? Probe(int number, List<VolumeInfo>? volumes, bool isSystem, bool readPartitions)
    {
        string path = $@"\\.\PhysicalDrive{number}";
        // Access 0 = query-only; works without administrator rights.
        using var h = Win32.CreateFile(path, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, 0, Win32.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return null;

        var (vendor, product, serial, bus, removable) = QueryDescriptor(h);
        var (mediaPresent, size, sectorSize) = QueryGeometry(h);

        bool writeProtected = false;
        if (mediaPresent)
        {
            bool writable = Win32.Ioctl(h, Win32.IOCTL_DISK_IS_WRITABLE, [], [], out _, out int err);
            writeProtected = !writable && err == Win32.ERROR_WRITE_PROTECT;
        }

        PartitionLayout? layout = null;
        if (mediaPresent && readPartitions)
            layout = TryReadLayout(path, sectorSize);

        return new DiskInfo
        {
            Number = number,
            Vendor = vendor,
            Product = product,
            Serial = serial,
            Bus = bus,
            RemovableMedia = removable,
            MediaPresent = mediaPresent,
            Size = size,
            SectorSize = sectorSize,
            WriteProtected = writeProtected,
            IsSystemDisk = isSystem,
            Volumes = volumes ?? [],
            Layout = layout,
        };
    }

    private static (string Vendor, string Product, string Serial, BusType Bus, bool Removable) QueryDescriptor(SafeFileHandle h)
    {
        Span<byte> query = stackalloc byte[12]; // PropertyId = StorageDeviceProperty(0), QueryType = Standard(0)
        query.Clear();
        Span<byte> buf = stackalloc byte[1024];
        buf.Clear();
        if (!Win32.Ioctl(h, Win32.IOCTL_STORAGE_QUERY_PROPERTY, query, buf, out int got, out _) || got < 36)
            return ("", "", "", BusType.Unknown, false);

        bool removable = buf[10] != 0;
        string vendor = ReadAnsi(buf, BinaryPrimitives.ReadInt32LittleEndian(buf[12..]), got);
        string product = ReadAnsi(buf, BinaryPrimitives.ReadInt32LittleEndian(buf[16..]), got);
        string serial = ReadAnsi(buf, BinaryPrimitives.ReadInt32LittleEndian(buf[24..]), got);
        var bus = (BusType)BinaryPrimitives.ReadInt32LittleEndian(buf[28..]);
        return (vendor, product, serial, bus, removable);
    }

    private static string ReadAnsi(ReadOnlySpan<byte> buf, int offset, int limit)
    {
        if (offset <= 0 || offset >= limit) return "";
        var s = buf[offset..limit];
        int end = s.IndexOf((byte)0);
        if (end >= 0) s = s[..end];
        return Encoding.ASCII.GetString(s).Trim();
    }

    private static (bool MediaPresent, long Size, int SectorSize) QueryGeometry(SafeFileHandle h)
    {
        Span<byte> buf = stackalloc byte[256];
        if (!Win32.Ioctl(h, Win32.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, [], buf, out int got, out _) || got < 32)
            return (false, 0, 512);
        int sector = BinaryPrimitives.ReadInt32LittleEndian(buf[20..]);
        long size = BinaryPrimitives.ReadInt64LittleEndian(buf[24..]);
        if (sector is not (512 or 1024 or 2048 or 4096)) sector = 512;
        return (size > 0, size, sector);
    }

    private static PartitionLayout? TryReadLayout(string path, int sectorSize)
    {
        try
        {
            using var dev = DiskBlockDevice.Open(path, writable: false);
            return PartitionLayout.Parse((offset, length) =>
            {
                int aligned = (length + sectorSize - 1) / sectorSize * sectorSize;
                int bufSize = (aligned + AlignedBuffer.Alignment - 1) / AlignedBuffer.Alignment * AlignedBuffer.Alignment;
                using var buf = new AlignedBuffer(bufSize);
                dev.Read(buf.Slice(0, aligned), offset);
                return buf.Slice(0, length).ToArray();
            }, sectorSize);
        }
        catch (Exception)
        {
            return null; // not elevated, or the card was pulled out
        }
    }

    /// <summary>Disks that host Windows, the running program or the page file: never writable.</summary>
    private static HashSet<int> ComputeSystemDisks()
    {
        var set = new HashSet<int>();
        foreach (string p in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     Environment.SystemDirectory,
                     AppContext.BaseDirectory,
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                 })
        {
            if (string.IsNullOrEmpty(p)) continue;
            foreach (int d in VolumeManager.GetDiskNumbersForPath(p)) set.Add(d);
        }
        return set;
    }
}
