using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ScaleFlash.App.Services;
using ScaleFlash.Core.Devices;
using ScaleFlash.Core.Engine;
using ScaleFlash.Core.History;

namespace ScaleFlash.App.ViewModels;

/// <summary>Card → image file (what the old tool did with the "&gt;&gt;" button).</summary>
public partial class BackupViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly DiskWatcher _watcher = DiskWatcher.Shared;

    public BackupViewModel()
    {
        _watcher.Changed += OnDisksChanged;
        OnDisksChanged(_watcher.Current);
    }

    public ObservableCollection<DiskItemViewModel> Disks { get; } = [];

    public bool IsElevated => SystemHelpers.IsElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DiskItemViewModel? _selectedDisk;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _outputPath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeText))]
    private bool _wholeDisk;

    [ObservableProperty] private bool _verify;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(HasJob))]
    private JobViewModel? _job;

    public bool HasJob => Job is not null;
    public bool HasDisks => Disks.Count > 0;

    public string EstimatedSizeText
    {
        get
        {
            if (SelectedDisk?.Info is not { MediaPresent: true } d) return "";
            long size = !WholeDisk && d.Layout is { } l && l.EndOfLastPartition > 0 ? l.EndOfLastPartition : d.Size;
            return $"ขนาดไฟล์ที่จะได้ประมาณ {Format.Bytes(size)}";
        }
    }

    private void OnDisksChanged(IReadOnlyList<DiskInfo> all)
    {
        var visible = all.Where(d => !d.IsSystemDisk && d.MediaPresent &&
                                     (d.IsCardLike || d.IsSmallUsb || (_settings.ShowFixedUsbDisks && d.IsUsbLike))).ToList();
        foreach (var d in visible)
        {
            var vm = Disks.FirstOrDefault(x => x.Number == d.Number);
            if (vm is null) Disks.Add(new DiskItemViewModel(d));
            else vm.Info = d;
        }
        foreach (var gone in Disks.Where(x => !x.IsBusy && visible.All(d => d.Number != x.Number)).ToList())
        {
            Disks.Remove(gone);
            if (SelectedDisk == gone) SelectedDisk = null;
        }
        if (SelectedDisk is null && Disks.Count == 1) SelectedDisk = Disks[0];
        OnPropertyChanged(nameof(HasDisks));
        OnPropertyChanged(nameof(EstimatedSizeText));
    }

    partial void OnSelectedDiskChanged(DiskItemViewModel? value)
    {
        if (value is null) return;
        string folder = _settings.LastBackupFolder is { } f && Directory.Exists(f) ? f
            : _settings.LastImagePath is { } p && Path.GetDirectoryName(p) is { } pd && Directory.Exists(pd) ? pd
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string name = string.Concat(value.Title.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        OutputPath = Path.Combine(folder, $"Backup_{name}_{DateTime.Now:yyyyMMdd_HHmm}.img");
    }

    [RelayCommand]
    private void Browse()
    {
        var dlg = new SaveFileDialog
        {
            Title = "บันทึกไฟล์สำรองการ์ด",
            Filter = "ไฟล์ image (*.img)|*.img|ไฟล์ทั้งหมด (*.*)|*.*",
            FileName = Path.GetFileName(OutputPath),
            OverwritePrompt = false,
        };
        string? dir = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog() == true) OutputPath = dlg.FileName;
    }

    private bool CanStart() => SelectedDisk is { CanSelectForRead: true } && !string.IsNullOrWhiteSpace(OutputPath)
                               && Job is not { IsRunning: true } && IsElevated;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var disk = SelectedDisk!;
        string output = OutputPath.Trim();
        if (File.Exists(output) &&
            !await Dialogs.ConfirmAsync("มีไฟล์นี้อยู่แล้ว", $"ต้องการเขียนทับไฟล์\n{output}\nหรือไม่?", "เขียนทับ", danger: true))
            return;

        _settings.LastBackupFolder = Path.GetDirectoryName(output);
        _settings.Save();

        var info = disk.Info;
        disk.IsBusy = true;
        _watcher.SetBusy(info.Number, true);
        var job = new JobViewModel(info.Model, $"ดิสก์ #{info.Number} → {Path.GetFileName(output)}", info.Number, isBackup: true);
        job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(JobViewModel.IsRunning)) StartCommand.NotifyCanExecuteChanged(); };
        job.RemoveRequested += _ => Job = null;
        job.Finished += j =>
        {
            disk.IsBusy = false;
            _watcher.SetBusy(info.Number, false);
            var r = j.Result!;
            HistoryStore.Append(new HistoryEntry
            {
                Kind = "backup",
                ImagePath = output,
                ImageSize = r.TotalBytes,
                DiskModel = info.Model,
                DiskSize = info.Size,
                Success = r.Success,
                Cancelled = r.Cancelled,
                Error = r.ErrorMessage,
                BytesWritten = r.BytesWritten,
                Verified = r.Success && Verify,
                Seconds = r.TotalDuration.TotalSeconds,
                AverageMBps = r.WriteBytesPerSecond / 1e6,
            });
            if (r.Success) SystemHelpers.PlaySuccess();
            else if (!r.Cancelled) SystemHelpers.PlayError();
            StartCommand.NotifyCanExecuteChanged();
            HistoryChanged?.Invoke();
        };
        Job = job;

        var request = new BackupRequest(DiskIdentity.Of(info), output, WholeDisk, Verify)
        {
            ChunkSize = _settings.ChunkSizeMB << 20,
        };
        job.Start((progress, ct) => CardJobs.Backup(request, progress, ct));
    }

    public event Action? HistoryChanged;

    public void CancelAll() => Job?.CancelSilently();
}
