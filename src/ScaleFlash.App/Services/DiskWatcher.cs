using System.Collections.Concurrent;
using System.Windows;
using ScaleFlash.Core.Devices;

namespace ScaleFlash.App.Services;

/// <summary>
/// Polls the physical disks every 1.5 s (plus immediately on WM_DEVICECHANGE) and raises
/// <see cref="Changed"/> on the UI thread whenever something visible changed.
/// </summary>
public sealed class DiskWatcher : IDisposable
{
    private readonly Timer _timer;
    private readonly ConcurrentDictionary<int, byte> _busy = new();
    private readonly object _gate = new();
    private string _signature = "";
    private int _running;

    public DiskWatcher()
    {
        _timer = new Timer(_ => Poll(), null, 0, 1500);
    }

    public static DiskWatcher Shared { get; } = new();

    public IReadOnlyList<DiskInfo> Current { get; private set; } = [];

    public event Action<IReadOnlyList<DiskInfo>>? Changed;

    /// <summary>Raised (on the caller's thread) when a disk starts or stops being written.</summary>
    public event Action<int, bool>? BusyChanged;

    public void SetBusy(int disk, bool busy)
    {
        if (busy) _busy[disk] = 0;
        else _busy.TryRemove(disk, out _);
        BusyChanged?.Invoke(disk, busy);
    }

    public bool IsBusy(int disk) => _busy.ContainsKey(disk);

    public void RefreshNow() => ThreadPool.QueueUserWorkItem(_ => Poll(force: true));

    private void Poll(bool force = false)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var busy = _busy.Keys.ToHashSet();
            List<DiskInfo> disks;
            try { disks = DiskEnumerator.Enumerate(readPartitions: true, skipDeepProbe: busy); }
            catch (Exception) { return; }

            // Keep the last known partition layout for disks we did not probe deeply.
            var previous = Current.ToDictionary(d => d.Number);
            for (int i = 0; i < disks.Count; i++)
                if (busy.Contains(disks[i].Number) && previous.TryGetValue(disks[i].Number, out var old))
                    disks[i] = disks[i] with { Layout = old.Layout };

            string sig = string.Join("|", disks.Select(Signature));
            lock (_gate)
            {
                if (!force && sig == _signature) return;
                _signature = sig;
                Current = disks;
            }
            Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(disks));
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static string Signature(DiskInfo d) =>
        $"{d.Number}:{d.Model}:{d.MediaPresent}:{d.Size}:{d.WriteProtected}:{string.Join(",", d.DriveLetters)}:" +
        (d.Layout is null ? "?" : string.Join(",", d.Layout.Partitions.Select(p => $"{p.MbrType}/{p.StartOffset}/{p.Length}")));

    public void Dispose() => _timer.Dispose();
}
