using ScaleFlash.Core.Imaging;

namespace ScaleFlash.Core.Devices;

/// <summary>Values of the Windows STORAGE_BUS_TYPE enumeration that matter to us.</summary>
public enum BusType
{
    Unknown = 0, Scsi = 1, Atapi = 2, Ata = 3, Ieee1394 = 4, Ssa = 5, Fibre = 6, Usb = 7, Raid = 8,
    iScsi = 9, Sas = 10, Sata = 11, Sd = 12, Mmc = 13, Virtual = 14, FileBackedVirtual = 15,
    Spaces = 16, Nvme = 17, Scm = 18, Ufs = 19,
}

/// <summary>Why a disk may not be used as a write target.</summary>
public enum DiskEligibility
{
    Eligible,
    NoMedia,
    SystemDisk,
    NotRemovable,
    WriteProtected,
}

public sealed record VolumeInfo(string VolumeGuidPath, IReadOnlyList<string> MountPoints);

public sealed record DiskInfo
{
    public required int Number { get; init; }
    public string Path => $@"\\.\PhysicalDrive{Number}";
    public string Vendor { get; init; } = "";
    public string Product { get; init; } = "";
    public string Serial { get; init; } = "";
    public BusType Bus { get; init; }
    public bool RemovableMedia { get; init; }
    public bool MediaPresent { get; init; }
    public long Size { get; init; }
    public int SectorSize { get; init; } = 512;
    public bool WriteProtected { get; init; }
    public bool IsSystemDisk { get; init; }
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];

    /// <summary>Partition table read from sector 0 (null when it could not be read).</summary>
    public PartitionLayout? Layout { get; init; }

    public string Model
    {
        get
        {
            string s = $"{Vendor.Trim()} {Product.Trim()}".Trim();
            return s.Length > 0 ? s : $"Disk {Number}";
        }
    }

    public IEnumerable<string> DriveLetters =>
        Volumes.SelectMany(v => v.MountPoints).Where(m => m.Length <= 3).Select(m => m.TrimEnd('\\'));

    /// <summary>Card readers / SD slots report removable media on USB, SD or MMC buses.</summary>
    public bool IsCardLike => (Bus is BusType.Usb or BusType.Sd or BusType.Mmc) && RemovableMedia;

    public bool IsUsbLike => Bus is BusType.Usb or BusType.Sd or BusType.Mmc;

    /// <summary>
    /// Many modern USB flash drives report themselves as "fixed" disks. Treat fixed USB disks up to this
    /// size as flash drives; bigger ones (external HDD/SSD) stay hidden unless the user opts in.
    /// </summary>
    public const long SmallUsbLimit = 130_000_000_000;

    public bool IsSmallUsb => IsUsbLike && MediaPresent && Size > 0 && Size <= SmallUsbLimit;

    public bool IsFixedUsb => IsUsbLike && !RemovableMedia;

    /// <summary>Best guess whether this is a card reader slot (vs. a USB flash drive), for icons and wording.</summary>
    public bool LooksLikeCardReader
    {
        get
        {
            if (Bus is BusType.Sd or BusType.Mmc) return true;
            string p = (Vendor + " " + Product).ToUpperInvariant();
            return p.Contains("CARD") || p.Contains("READER") || p.Contains("SD/") || p.Contains("MMC") || p.Contains("MULTI")
                   || System.Text.RegularExpressions.Regex.IsMatch(p, @"\bRW\d") || p.Contains(" SD ") || p.EndsWith(" SD");
        }
    }

    /// <summary>
    /// Safety gate for write targets. System disks are never eligible; fixed USB disks only when
    /// <paramref name="allowFixedUsb"/>; virtual disks only when <paramref name="allowVirtual"/> (testing).
    /// </summary>
    public DiskEligibility GetEligibility(bool allowFixedUsb = false, bool allowVirtual = false)
    {
        if (IsSystemDisk) return DiskEligibility.SystemDisk;
        bool busOk = IsCardLike
                     || IsSmallUsb
                     || (allowFixedUsb && IsUsbLike)
                     || (allowVirtual && Bus is BusType.FileBackedVirtual or BusType.Virtual);
        if (!busOk) return DiskEligibility.NotRemovable;
        if (!MediaPresent || Size <= 0) return DiskEligibility.NoMedia;
        if (WriteProtected) return DiskEligibility.WriteProtected;
        return DiskEligibility.Eligible;
    }
}
