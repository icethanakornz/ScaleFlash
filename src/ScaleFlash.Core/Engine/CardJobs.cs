using ScaleFlash.Core.Devices;
using ScaleFlash.Core.Imaging;
using ScaleFlash.Core.IO;

namespace ScaleFlash.Core.Engine;

/// <summary>Identity of the card the user picked; re-checked right before writing.</summary>
public sealed record DiskIdentity(int Number, string Model, long Size)
{
    public static DiskIdentity Of(DiskInfo d) => new(d.Number, d.Model, d.Size);

    public bool Matches(DiskInfo d) => d.Number == Number && d.Model == Model && d.Size == Size;
}

public sealed record WriteRequest(string ImagePath, DiskIdentity Disk, FlashOptions Options)
{
    /// <summary>Write the beginning of the image even though the card is smaller (the user confirmed).</summary>
    public bool AllowSmallerTarget { get; init; }
    public bool AllowFixedUsb { get; init; }
    public bool AllowVirtual { get; init; }
}

public sealed record BackupRequest(DiskIdentity Disk, string OutputPath, bool WholeDisk, bool Verify)
{
    public int ChunkSize { get; init; } = 4 << 20;
}

/// <summary>High-level operations with all safety checks: image → card and card → image file.</summary>
public static class CardJobs
{
    public static FlashResult WriteImage(WriteRequest req, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        try
        {
            var disk = DiskEnumerator.Probe(req.Disk.Number, readPartitions: false);
            if (disk is null || !disk.MediaPresent) return Fail("ไม่พบการ์ด กรุณาเสียบการ์ดแล้วลองใหม่");
            if (!req.Disk.Matches(disk)) return Fail("การ์ดในเครื่องอ่านเปลี่ยนไปจากที่เลือกไว้ กรุณาเลือกการ์ดใหม่อีกครั้ง");

            var eligibility = disk.GetEligibility(req.AllowFixedUsb, req.AllowVirtual);
            if (eligibility != DiskEligibility.Eligible) return Fail(Describe(eligibility));
            if (VolumeManager.GetDiskNumbersForPath(req.ImagePath).Contains(disk.Number))
                return Fail("ไฟล์ image อยู่บนการ์ดใบนี้เอง ไม่สามารถเขียนทับได้");

            using var source = FileBlockDevice.OpenRead(req.ImagePath);
            using var locks = VolumeManager.LockAndDismount(disk.Number);
            using var target = DiskBlockDevice.Open(disk.Number, writable: true);

            long length = source.Length;
            long needed = (length + target.SectorSize - 1) / target.SectorSize * target.SectorSize;
            if (needed > target.Length)
            {
                if (!req.AllowSmallerTarget)
                    return Fail($"การ์ดเล็กเกินไป: ต้องการ {Format.Bytes(needed)} แต่การ์ดมี {Format.Bytes(target.Length)}");
                length = target.Length / target.SectorSize * target.SectorSize;
            }

            var options = req.Options with { BeforeRetry = _ => locks.Redismount() };
            return FlashEngine.Run(source, length, target, options, progress, ct);
        }
        catch (DeviceIoException ex)
        {
            return Fail(ex.Operation == "Open" && ex.Device.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? OpenDiskError(ex.Win32Error)
                : ErrorText.ForIo(ex, isSource: !ex.Device.StartsWith(@"\\.\", StringComparison.Ordinal)), ex.Win32Error);
        }
    }

    public static FlashResult Backup(BackupRequest req, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        string partial = req.OutputPath + ".partial";
        try
        {
            var disk = DiskEnumerator.Probe(req.Disk.Number, readPartitions: false);
            if (disk is null || !disk.MediaPresent) return Fail("ไม่พบการ์ด กรุณาเสียบการ์ดแล้วลองใหม่");
            if (!req.Disk.Matches(disk)) return Fail("การ์ดในเครื่องอ่านเปลี่ยนไปจากที่เลือกไว้ กรุณาเลือกการ์ดใหม่อีกครั้ง");

            string outDir = Path.GetDirectoryName(Path.GetFullPath(req.OutputPath))!;
            if (VolumeManager.GetDiskNumbersForPath(outDir).Contains(disk.Number))
                return Fail("ไม่สามารถบันทึกไฟล์ลงบนการ์ดใบเดียวกับที่กำลังอ่านได้");

            using var source = DiskBlockDevice.Open(disk.Number, writable: false);
            long length = GetBackupLength(source, req.WholeDisk);

            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(req.OutputPath))!).AvailableFreeSpace;
            if (File.Exists(req.OutputPath)) free += new FileInfo(req.OutputPath).Length;
            if (free < length) return Fail($"พื้นที่ว่างไม่พอ: ต้องการ {Format.Bytes(length)} แต่เหลือ {Format.Bytes(free)}");

            FlashResult result;
            using (var target = FileBlockDevice.Create(partial, length))
            {
                var options = new FlashOptions
                {
                    ChunkSize = req.ChunkSize,
                    SkipIdenticalChunks = false,
                    ProtectAgainstInterruption = false,
                    Verify = req.Verify,
                };
                result = FlashEngine.Run(source, length, target, options, progress, ct);
            }

            if (result.Success) File.Move(partial, req.OutputPath, overwrite: true);
            else TryDelete(partial);
            return result;
        }
        catch (DeviceIoException ex)
        {
            TryDelete(partial);
            return Fail(ex.Operation == "Open" && ex.Device.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? OpenDiskError(ex.Win32Error)
                : ErrorText.ForIo(ex, isSource: ex.Device.StartsWith(@"\\.\", StringComparison.Ordinal)), ex.Win32Error);
        }
        catch (IOException ex)
        {
            TryDelete(partial);
            return Fail("บันทึกไฟล์ไม่สำเร็จ: " + ex.Message);
        }
    }

    /// <summary>Up to the end of the last partition (like the old tool), or the whole card.</summary>
    public static long GetBackupLength(IBlockDevice disk, bool wholeDisk)
    {
        if (wholeDisk) return disk.Length;
        var layout = PartitionLayout.Parse((offset, length) =>
        {
            using var buf = new Native.AlignedBuffer(Math.Max(Native.AlignedBuffer.Alignment, (length + 4095) / 4096 * 4096));
            int aligned = (length + disk.SectorSize - 1) / disk.SectorSize * disk.SectorSize;
            disk.Read(buf.Slice(0, aligned), offset);
            return buf.Slice(0, length).ToArray();
        }, disk.SectorSize);
        long end = layout.EndOfLastPartition;
        if (end <= 0 || end > disk.Length) return disk.Length;
        return end;
    }

    public static string Describe(DiskEligibility e) => e switch
    {
        DiskEligibility.SystemDisk => "นี่คือดิสก์ของระบบ Windows ไม่อนุญาตให้เขียนทับ",
        DiskEligibility.NotRemovable => "อุปกรณ์นี้ไม่ใช่การ์ด SD / USB แบบถอดได้",
        DiskEligibility.NoMedia => "ไม่มีการ์ดในช่องนี้",
        DiskEligibility.WriteProtected => "การ์ดถูกล็อกป้องกันการเขียน (เลื่อนสวิตช์ Lock ข้างการ์ด)",
        _ => "",
    };

    private static string OpenDiskError(int err) => err switch
    {
        5 => "ไม่มีสิทธิ์เข้าถึงการ์ด: ต้องเปิดโปรแกรมแบบ Administrator",
        2 or 3 or 21 or 1112 => "ไม่พบการ์ด กรุณาเสียบการ์ดแล้วลองใหม่",
        32 => "มีโปรแกรมอื่นใช้งานการ์ดอยู่",
        _ => $"เปิดการ์ดไม่สำเร็จ (รหัส {err})",
    };

    private static FlashResult Fail(string message, int win32 = 0) => new() { Success = false, ErrorMessage = message, Win32Error = win32 };

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { /* best effort */ }
    }
}
