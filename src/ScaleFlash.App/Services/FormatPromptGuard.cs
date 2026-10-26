using ScaleFlash.Core.Devices;

namespace ScaleFlash.App.Services;

/// <summary>
/// Optional: while the program runs, removes the drive letter of any Linux (Windows-unreadable) partition
/// on a USB stick or card, so Explorer stops asking to format it. Windows remembers this per partition,
/// so later insertions of cards made from the same image normally get no drive letter at all.
/// </summary>
public sealed class FormatPromptGuard
{
    private readonly HashSet<string> _handled = [];

    public FormatPromptGuard() => DiskWatcher.Shared.Changed += OnChanged;

    public event Action<string>? Hidden;

    private void OnChanged(IReadOnlyList<DiskInfo> disks)
    {
        if (!AppSettings.Current.AutoHideLinuxDrives || !SystemHelpers.IsElevated) return;
        foreach (var d in disks)
        {
            if (d.IsSystemDisk || !d.MediaPresent || !(d.IsCardLike || d.IsSmallUsb) || !d.DriveLetters.Any()) continue;
            if (DiskWatcher.Shared.IsBusy(d.Number)) continue;
            string key = $"{d.Number}:{d.Size}:{string.Join(",", d.DriveLetters)}";
            if (!_handled.Add(key)) continue;
            int number = d.Number;
            string model = d.Model;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<string> removed;
                try { removed = VolumeManager.HideUnreadableVolumes(number); }
                catch (Exception) { return; }
                if (removed.Count == 0) return;
                DiskWatcher.Shared.RefreshNow();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    Hidden?.Invoke($"ซ่อนไดรฟ์ {string.Join(", ", removed)} ของ {model} จาก Windows แล้ว (กันหน้าต่างให้ Format)"));
            });
        }
    }
}
