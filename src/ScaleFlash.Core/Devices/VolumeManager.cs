using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ScaleFlash.Core.Native;

namespace ScaleFlash.Core.Devices;

/// <summary>Maps Windows volumes (drive letters) to physical disks and locks/dismounts them before raw writes.</summary>
public static unsafe class VolumeManager
{
    /// <summary>Returns every volume grouped by the physical disk numbers it lives on.</summary>
    public static Dictionary<int, List<VolumeInfo>> GetVolumesByDisk()
    {
        var map = new Dictionary<int, List<VolumeInfo>>();
        foreach (string vol in EnumerateVolumeGuids())
        {
            var disks = GetDiskNumbersForVolume(vol);
            if (disks.Count == 0) continue;
            var info = new VolumeInfo(vol, GetMountPoints(vol));
            foreach (int d in disks)
            {
                if (!map.TryGetValue(d, out var list)) map[d] = list = [];
                list.Add(info);
            }
        }
        return map;
    }

    /// <summary>Physical disk numbers that host the volume containing <paramref name="path"/>.</summary>
    public static IReadOnlyList<int> GetDiskNumbersForPath(string path)
    {
        try
        {
            char* buf = stackalloc char[1024];
            if (!Win32.GetVolumePathName(Path.GetFullPath(path), buf, 1024)) return [];
            string mount = new(buf);
            char* vbuf = stackalloc char[1024];
            if (!Win32.GetVolumeNameForVolumeMountPoint(mount, vbuf, 1024)) return [];
            return GetDiskNumbersForVolume(new string(vbuf));
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<string> EnumerateVolumeGuids()
    {
        var result = new List<string>();
        char* buf = stackalloc char[1024];
        nint h = Win32.FindFirstVolume(buf, 1024);
        if (h == -1) return result;
        try
        {
            do { result.Add(new string(buf)); }
            while (Win32.FindNextVolume(h, buf, 1024));
        }
        finally { Win32.FindVolumeClose(h); }
        return result;
    }

    private static IReadOnlyList<string> GetMountPoints(string volumeGuidPath)
    {
        char* buf = stackalloc char[4096];
        if (!Win32.GetVolumePathNamesForVolumeName(volumeGuidPath, buf, 4096, out _)) return [];
        var list = new List<string>();
        char* p = buf;
        while (*p != '\0')
        {
            string s = new(p);
            list.Add(s);
            p += s.Length + 1;
        }
        return list;
    }

    /// <param name="volumeGuidPath">"\\?\Volume{GUID}\" (with or without trailing backslash).</param>
    public static List<int> GetDiskNumbersForVolume(string volumeGuidPath)
    {
        var disks = new List<int>();
        using var h = Win32.CreateFile(volumeGuidPath.TrimEnd('\\'), 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, 0, Win32.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return disks;
        Span<byte> outBuf = stackalloc byte[8 + 24 * 32];
        if (!Win32.Ioctl(h, Win32.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, [], outBuf, out _, out _)) return disks;
        int n = BinaryPrimitives.ReadInt32LittleEndian(outBuf);
        for (int i = 0; i < Math.Min(n, 32); i++)
        {
            int disk = BinaryPrimitives.ReadInt32LittleEndian(outBuf[(8 + i * 24)..]);
            if (!disks.Contains(disk)) disks.Add(disk);
        }
        return disks;
    }

    /// <summary>File system name Windows mounted on the volume ("NTFS", "FAT32"…), or null when Windows
    /// cannot read it (RAW — e.g. a Linux ext partition, which makes Explorer offer to format it).</summary>
    public static string? GetFileSystemName(string volumeGuidPath, out bool unreadable)
    {
        char* fs = stackalloc char[64];
        string root = volumeGuidPath.EndsWith('\\') ? volumeGuidPath : volumeGuidPath + "\\";
        bool ok = Win32.GetVolumeInformation(root, null, 0, out _, out _, out _, fs, 64);
        unreadable = !ok && Marshal.GetLastPInvokeError() == Win32.ERROR_UNRECOGNIZED_VOLUME;
        return ok ? new string(fs) : null;
    }

    /// <summary>
    /// Removes the drive letters of the volumes on <paramref name="diskNumber"/> that Windows cannot read,
    /// so Explorer stops offering to format them. Same as "Remove drive letter" in Disk Management;
    /// the data on the device is not touched. Returns the letters removed.
    /// </summary>
    public static List<string> HideUnreadableVolumes(int diskNumber)
    {
        var removed = new List<string>();
        foreach (var v in GetVolumesByDisk().GetValueOrDefault(diskNumber) ?? [])
        {
            var letters = v.MountPoints.Where(m => m.Length <= 3).ToList();
            if (letters.Count == 0) continue;
            GetFileSystemName(v.VolumeGuidPath, out bool unreadable);
            if (!unreadable) continue;
            foreach (string l in letters)
                if (Win32.DeleteVolumeMountPoint(l)) removed.Add(l.TrimEnd('\\'));
        }
        return removed;
    }

    /// <summary>
    /// Locks and dismounts every volume on <paramref name="diskNumber"/> so Windows stops caching or
    /// guarding those sectors. Keep the returned object alive for the whole write.
    /// </summary>
    public static VolumeLockSet LockAndDismount(int diskNumber)
    {
        var set = new VolumeLockSet();
        var volumes = GetVolumesByDisk().GetValueOrDefault(diskNumber) ?? [];
        foreach (var v in volumes)
            set.Add(v.VolumeGuidPath.TrimEnd('\\'));
        return set;
    }
}

/// <summary>Holds open, locked and dismounted volume handles; disposing unlocks them.</summary>
public sealed unsafe class VolumeLockSet : IDisposable
{
    private readonly List<(string Path, SafeFileHandle Handle, bool Locked)> _volumes = [];

    public IReadOnlyList<string> Unlocked => _volumes.Where(v => !v.Locked).Select(v => v.Path).ToList();

    internal void Add(string path)
    {
        var h = Win32.CreateFile(path, Win32.GENERIC_READ | Win32.GENERIC_WRITE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, 0, Win32.OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) { h.Dispose(); return; }

        bool locked = false;
        for (int attempt = 0; attempt < 10 && !locked; attempt++)
        {
            locked = Win32.Ioctl(h, Win32.FSCTL_LOCK_VOLUME, [], [], out _, out _);
            if (!locked) Thread.Sleep(200);
        }
        // Dismount even if the lock failed: this force-closes handles other programs hold on the card.
        Win32.Ioctl(h, Win32.FSCTL_DISMOUNT_VOLUME, [], [], out _, out _);
        _volumes.Add((path, h, locked));
    }

    /// <summary>Dismounts again (used when Windows re-mounted an unlocked volume mid-write).</summary>
    public void Redismount()
    {
        foreach (var v in _volumes)
            Win32.Ioctl(v.Handle, Win32.FSCTL_DISMOUNT_VOLUME, [], [], out _, out _);
    }

    public void Dispose()
    {
        foreach (var v in _volumes)
        {
            if (v.Locked) Win32.Ioctl(v.Handle, Win32.FSCTL_UNLOCK_VOLUME, [], [], out _, out _);
            v.Handle.Dispose();
        }
        _volumes.Clear();
    }
}
