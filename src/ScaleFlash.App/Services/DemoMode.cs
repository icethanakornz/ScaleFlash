#if DEBUG
using System.IO;
using ScaleFlash.Core.Devices;
using ScaleFlash.Core.Engine;
using ScaleFlash.Core.IO;

namespace ScaleFlash.App.Services;

/// <summary>
/// Debug-build only: when SCALEFLASH_DEMO_TARGET points to a file, a fake card (#99) appears whose
/// "writes" go to that file at a simulated SD-card speed. Lets the UI be exercised without admin rights
/// or a real card. Not compiled into Release.
/// </summary>
internal static class DemoMode
{
    public const int DiskNumber = 99;

    public static string? TargetPath => Environment.GetEnvironmentVariable("SCALEFLASH_DEMO_TARGET");

    public static bool Enabled => !string.IsNullOrEmpty(TargetPath);

    public static double BytesPerSecond =>
        double.TryParse(Environment.GetEnvironmentVariable("SCALEFLASH_DEMO_MBPS"), out double mb) ? mb * 1e6 : 40e6;

    public static DiskInfo FakeDisk => new()
    {
        Number = DiskNumber,
        Vendor = "Demo",
        Product = "Virtual SD 32GB",
        Bus = BusType.Usb,
        RemovableMedia = true,
        MediaPresent = true,
        Size = 31_914_983_424,
    };

    public static FlashResult Run(string imagePath, FlashOptions options, IProgress<FlashProgress> progress, CancellationToken ct)
    {
        using var source = FileBlockDevice.OpenRead(imagePath);
        string target = TargetPath!;
        using var file = File.Exists(target) ? FileBlockDevice.OpenReadWrite(target) : FileBlockDevice.Create(target, source.Length);
        using var slow = new ThrottledDevice(file, BytesPerSecond, FakeDisk.Size);
        return FlashEngine.Run(source, source.Length, slow, options, progress, ct);
    }

    private sealed class ThrottledDevice(IBlockDevice inner, double bytesPerSecond, long length) : IBlockDevice
    {
        public string DisplayName => inner.DisplayName;
        public long Length => length;
        public int SectorSize => 512;

        public void Read(Span<byte> buffer, long offset)
        {
            if (offset + buffer.Length <= inner.Length) inner.Read(buffer, offset);
            else buffer.Clear();
            Thread.Sleep((int)(buffer.Length / (bytesPerSecond * 3) * 1000)); // reads ~3x faster than writes
        }

        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            inner.Write(buffer, offset);
            Thread.Sleep((int)(buffer.Length / bytesPerSecond * 1000));
        }

        public void Flush() => inner.Flush();

        public void Dispose() { }
    }
}
#endif
