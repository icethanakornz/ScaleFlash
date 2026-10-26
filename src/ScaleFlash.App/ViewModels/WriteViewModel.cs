using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ScaleFlash.App.Services;
using ScaleFlash.Core.Devices;
using ScaleFlash.Core.Engine;
using ScaleFlash.Core.History;
using ScaleFlash.Core.Imaging;
using ScaleFlash.Core.IO;

namespace ScaleFlash.App.ViewModels;

public partial class WriteViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly DiskWatcher _watcher = DiskWatcher.Shared;
    private readonly HashSet<int> _knownWithMedia = [];
    private readonly Dictionary<int, DateTime> _finishedAt = [];
    private bool _suppressAutoPrompt;
    private bool _autoArmed;

    public WriteViewModel()
    {
        _skipIdentical = _settings.SkipIdentical;
        _verify = _settings.Verify;
        RecentImages = new ObservableCollection<string>(_settings.RecentImages.Where(File.Exists));
        Jobs.CollectionChanged += OnJobsChanged;
        _watcher.Changed += OnDisksChanged;
        OnDisksChanged(_watcher.Current);

        if (_settings.LastImagePath is { } last && File.Exists(last))
            _ = LoadImageAsync(last);
    }

    public ObservableCollection<DiskItemViewModel> Disks { get; } = [];
    public ObservableCollection<JobViewModel> Jobs { get; } = [];
    public ObservableCollection<string> RecentImages { get; }

    public bool IsElevated => SystemHelpers.IsElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(ImageChips), nameof(ImageWarnings), nameof(ImageTitle), nameof(ImageFolder), nameof(StartButtonText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private ImageInfo? _image;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isAnalyzing;

    [ObservableProperty] private string? _imageError;
    [ObservableProperty] private bool _skipIdentical;
    [ObservableProperty] private bool _verify;
    [ObservableProperty] private bool _autoMode;

    [ObservableProperty] private bool _anyRunning;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private bool _lastBatchFailed;

    public bool HasImage => Image is not null;
    public string ImageTitle => Image?.FileName ?? "";
    public string ImageFolder => Image is null ? "" : Path.GetDirectoryName(Image.Path) ?? "";
    public bool HasJobs => Jobs.Count > 0;
    public bool HasRecent => RecentImages.Count > 0;
    public bool HasDisks => Disks.Count > 0;

    public IReadOnlyList<string> ImageChips
    {
        get
        {
            if (Image is not { } img) return [];
            var chips = new List<string> { Format.Bytes(img.Size) };
            if (img.Layout.Scheme != PartitionScheme.None) chips.Add($"{img.Layout.SchemeName} · {img.Layout.Partitions.Count} พาร์ทิชัน");
            foreach (var p in img.Partitions)
            {
                string fs = p.FileSystem == Core.Imaging.FileSystemInfo.Unknown ? p.Partition.TypeName : $"{p.Partition.TypeName} {p.FileSystem.Type}";
                string used = p.FileSystem.UsedFraction is { } f ? $" · ใช้ {f:P0}" : "";
                chips.Add($"{fs} {Format.Bytes(p.Partition.Length)}{used}");
            }
            if (img.Bootloader is not null) chips.Add(img.Bootloader);
            return chips;
        }
    }

    public IReadOnlyList<string> ImageWarnings => Image?.Warnings ?? [];

    private IEnumerable<DiskItemViewModel> SelectedDisks => Disks.Where(d => d.IsSelected && d.CanSelect);

    public int SelectedCount => SelectedDisks.Count();

    public string StartButtonText => SelectedCount switch
    {
        0 => "เลือกการ์ด / USB ก่อนเริ่มเขียน",
        1 => "เริ่มเขียน",
        var n => $"เริ่มเขียน {n} อันพร้อมกัน",
    };

    // ----- image -----

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "เลือกไฟล์ image ของการ์ด",
            Filter = "ไฟล์ทั้งหมด (*.*)|*.*|ไฟล์ image (*.img;*.bin;*.raw;*.dd)|*.img;*.bin;*.raw;*.dd",
            CheckFileExists = true,
        };
        string? dir = Image is not null ? ImageFolder : _settings.LastImagePath is { } p ? Path.GetDirectoryName(p) : null;
        if (dir is not null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog() == true) await LoadImageAsync(dlg.FileName);
    }

    [RelayCommand]
    private Task OpenRecentAsync(string path) => LoadImageAsync(path);

    public async Task LoadImageAsync(string path)
    {
        if (AutoMode) AutoMode = false;
        IsAnalyzing = true;
        ImageError = null;
        try
        {
            var info = await Task.Run(() => ImageInfo.Analyze(path));
            Image = info;
            _settings.AddRecent(path);
            RecentImages.Clear();
            foreach (var r in _settings.RecentImages.Where(File.Exists)) RecentImages.Add(r);
            OnPropertyChanged(nameof(HasRecent));
        }
        catch (Exception ex)
        {
            Image = null;
            ImageError = ex is DeviceIoException io ? ErrorText.ForIo(io, isSource: true) : "เปิดไฟล์ไม่ได้: " + ex.Message;
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    partial void OnImageChanged(ImageInfo? value)
    {
        foreach (var d in Disks) d.SetContext(value, _settings.ShowFixedUsbDisks);
        RaiseSelectionChanged();
    }

    partial void OnSkipIdenticalChanged(bool value) { _settings.SkipIdentical = value; _settings.Save(); }
    partial void OnVerifyChanged(bool value) { _settings.Verify = value; _settings.Save(); }

    // ----- disks -----

    /// <summary>Re-applies "show fixed USB disks" after it was changed on the settings page.</summary>
    public void ReloadDiskFilter() => OnDisksChanged(_watcher.Current);

    private void OnDisksChanged(IReadOnlyList<DiskInfo> all)
    {
#if DEBUG
        if (DemoMode.Enabled) all = [.. all, DemoMode.FakeDisk];
#endif
        bool showFixed = _settings.ShowFixedUsbDisks;
        var visible = all.Where(d => !d.IsSystemDisk && (d.IsCardLike || d.IsSmallUsb || (showFixed && d.IsUsbLike))).ToList();

        foreach (var d in visible)
        {
            var vm = Disks.FirstOrDefault(x => x.Number == d.Number);
            if (vm is null)
            {
                vm = new DiskItemViewModel(d);
                vm.SetContext(Image, showFixed);
                vm.PropertyChanged += OnDiskPropertyChanged;
                Disks.Add(vm);
            }
            else vm.Info = d;
        }
        foreach (var gone in Disks.Where(x => !x.IsBusy && visible.All(d => d.Number != x.Number)).ToList())
        {
            gone.PropertyChanged -= OnDiskPropertyChanged;
            Disks.Remove(gone);
        }

        // Auto mode: a card that just appeared in a slot gets written straight away.
        var newlyInserted = new List<DiskItemViewModel>();
        foreach (var vm in Disks)
        {
            bool present = vm.Info.MediaPresent && !vm.IsBusy;
            if (!vm.Info.MediaPresent && !vm.IsBusy && !RecentlyFinished(vm.Number))
            {
                _knownWithMedia.Remove(vm.Number);
                continue;
            }
            if (present && _knownWithMedia.Add(vm.Number) && AutoMode && _autoArmed && vm.CanSelect && !vm.TooSmall)
                newlyInserted.Add(vm);
        }
        if (newlyInserted.Count > 0) StartJobs(newlyInserted);

        OnPropertyChanged(nameof(HasDisks));
        RaiseSelectionChanged();
    }

    private bool RecentlyFinished(int disk) =>
        _finishedAt.TryGetValue(disk, out var t) && DateTime.Now - t < TimeSpan.FromSeconds(5);

    private void OnDiskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiskItemViewModel.IsSelected) or nameof(DiskItemViewModel.CanSelect))
            RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(StartButtonText));
        StartCommand.NotifyCanExecuteChanged();
    }

    // ----- auto mode -----

    partial void OnAutoModeChanged(bool value)
    {
        _autoArmed = false;
        if (!value || _suppressAutoPrompt) return;
        _ = ConfirmAutoModeAsync();
    }

    private async Task ConfirmAutoModeAsync()
    {
        bool ok = false;
        if (Image is null)
            await Dialogs.AlertAsync("ยังไม่ได้เลือกไฟล์", "เลือกไฟล์ image ก่อนเปิดโหมดเขียนอัตโนมัติ");
        else if (!IsElevated)
            await Dialogs.AlertAsync("ต้องใช้สิทธิ์ Administrator", "เปิดโปรแกรมใหม่แบบ Administrator ก่อนใช้โหมดอัตโนมัติ");
        else
            ok = await Dialogs.ConfirmAsync("เปิดโหมดเขียนอัตโนมัติ?",
                Dialogs.Bullets("เมื่อเปิดโหมดนี้ ทุกการ์ด / USB ที่เสียบเข้ามาใหม่จะถูกลบและเขียนทับด้วยไฟล์นี้ทันทีโดยไม่ถามอีก:",
                    [Image.FileName],
                    "อันที่เสียบอยู่ตอนนี้จะไม่ถูกเขียน ให้ถอดแล้วเสียบใหม่ หรือกดเริ่มเขียนเอง",
                    footerIsWarning: false),
                "เปิดโหมดอัตโนมัติ");

        if (!ok)
        {
            _suppressAutoPrompt = true;
            AutoMode = false;
            _suppressAutoPrompt = false;
            return;
        }
        // Cards already inserted are "known" and will not be written automatically.
        foreach (var d in Disks.Where(d => d.Info.MediaPresent)) _knownWithMedia.Add(d.Number);
        _autoArmed = AutoMode;
    }

    // ----- jobs -----

    private bool CanStart() => Image is not null && !IsAnalyzing && SelectedCount > 0 && (IsElevated || OnlyDemoSelected);

#if DEBUG
    private bool OnlyDemoSelected => SelectedDisks.All(d => d.Number == DemoMode.DiskNumber);
#else
    private static bool OnlyDemoSelected => false;
#endif

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var targets = SelectedDisks.ToList();
        if (targets.Count == 0 || Image is null) return;

        var lines = targets.Select(d => $"{d.Title} — {Format.Bytes(d.Info.Size)}" +
                                        (d.Info.DriveLetters.Any() ? $" ({string.Join(" ", d.Info.DriveLetters)})" : "") +
                                        $"  [ดิสก์ #{d.Number}]");
        var small = targets.Where(d => d.TooSmall).ToList();
        string? footer = small.Count > 0
            ? $"คำเตือน: การ์ด {string.Join(", ", small.Select(s => "#" + s.Number))} เล็กกว่าไฟล์ image ({Format.Bytes(Image.WriteLength)}) ข้อมูลส่วนท้ายจะหาย เครื่องชั่งอาจใช้งานไม่ได้"
            : "ข้อมูลเดิมบนการ์ดจะหายทั้งหมด และกู้คืนไม่ได้";
        bool ok = await Dialogs.ConfirmAsync("ยืนยันการเขียน",
            Dialogs.Bullets($"จะเขียน \"{Image.FileName}\" ลงอุปกรณ์ต่อไปนี้:", lines, footer, footerIsWarning: true),
            targets.Count == 1 ? "ลบและเขียนเลย" : $"ลบและเขียน {targets.Count} ใบ", danger: true);
        if (!ok) return;

        StartJobs(targets);
    }

    private void StartJobs(IReadOnlyList<DiskItemViewModel> targets)
    {
        if (Image is not { } image) return;
        var options = new FlashOptions
        {
            ChunkSize = _settings.ChunkSizeMB << 20,
            SkipIdenticalChunks = SkipIdentical,
            Verify = Verify,
        };

        foreach (var d in targets)
        {
            if (d.IsBusy) continue;
            var info = d.Info;
            d.IsBusy = true;
            d.IsSelected = false;
            _watcher.SetBusy(info.Number, true);
            _knownWithMedia.Add(info.Number);

            var job = new JobViewModel(info.Model, $"ดิสก์ #{info.Number} · {Format.Bytes(info.Size)} · {image.FileName}", info.Number);
            job.Finished += j => OnJobFinished(j, d, info, image, options);
            job.RemoveRequested += j => Jobs.Remove(j);
            job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(JobViewModel.Percent)) UpdateOverall(); };
            Jobs.Insert(0, job);

            var request = new WriteRequest(image.Path, DiskIdentity.Of(info), options)
            {
                AllowSmallerTarget = d.TooSmall,
                AllowFixedUsb = _settings.ShowFixedUsbDisks,
            };
#if DEBUG
            if (info.Number == DemoMode.DiskNumber)
            {
                job.Start((progress, ct) => DemoMode.Run(image.Path, options, progress, ct));
                continue;
            }
#endif
            job.Start((progress, ct) => CardJobs.WriteImage(request, progress, ct));
        }
        LastBatchFailed = false;
        UpdateOverall();
    }

    private void OnJobFinished(JobViewModel job, DiskItemViewModel disk, DiskInfo info, ImageInfo image, FlashOptions options)
    {
        _finishedAt[info.Number] = DateTime.Now;
        disk.IsBusy = false;
        _watcher.SetBusy(info.Number, false);
        _watcher.RefreshNow();

        var r = job.Result!;
        HistoryStore.Append(new HistoryEntry
        {
            Kind = "write",
            ImagePath = image.Path,
            ImageSize = image.Size,
            DiskModel = info.Model,
            DiskSize = info.Size,
            Success = r.Success,
            Cancelled = r.Cancelled,
            Error = r.ErrorMessage,
            BytesWritten = r.BytesWritten,
            BytesSkipped = r.BytesSkipped,
            Verified = r.Success && options.Verify,
            Seconds = r.TotalDuration.TotalSeconds,
            AverageMBps = r.WriteBytesPerSecond / 1e6,
        });

        if (r.Success) SystemHelpers.PlaySuccess();
        else if (!r.Cancelled) { SystemHelpers.PlayError(); LastBatchFailed = true; }
        UpdateOverall();
        HistoryChanged?.Invoke();
    }

    public event Action? HistoryChanged;

    /// <summary>"ดูข้อมูลข้างใน" on a disk tile.</summary>
    public event Action<int>? InspectRequested;

    [RelayCommand]
    private void InspectDisk(DiskItemViewModel? disk)
    {
        if (disk is not null) InspectRequested?.Invoke(disk.Number);
    }

    private void UpdateOverall()
    {
        var running = Jobs.Where(j => j.IsRunning).ToList();
        AnyRunning = running.Count > 0;
        OverallProgress = running.Count > 0 ? running.Average(j => j.Fraction) : 0;
        SystemHelpers.PreventSleep(AnyRunning);
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(HasJobs));

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var j in Jobs.Where(j => !j.IsRunning).ToList()) Jobs.Remove(j);
    }

    public void CancelAll()
    {
        foreach (var j in Jobs.Where(j => j.IsRunning)) j.CancelSilently();
    }
}
