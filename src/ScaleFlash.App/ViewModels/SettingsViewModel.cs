using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScaleFlash.App.Services;
using ScaleFlash.Core.History;

namespace ScaleFlash.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Current;

    public SettingsViewModel()
    {
        _chunkSizeMB = _settings.ChunkSizeMB;
        _showFixedUsb = _settings.ShowFixedUsbDisks;
        _autoHideLinuxDrives = _settings.AutoHideLinuxDrives;
    }

    public IReadOnlyList<int> ChunkSizes { get; } = [1, 2, 4, 8, 16];

    [ObservableProperty] private int _chunkSizeMB;
    [ObservableProperty] private bool _showFixedUsb;
    [ObservableProperty] private bool _autoHideLinuxDrives;

    partial void OnAutoHideLinuxDrivesChanged(bool value)
    {
        _settings.AutoHideLinuxDrives = value;
        _settings.Save();
        if (value) Services.DiskWatcher.Shared.RefreshNow();
    }

    public string Version => "เวอร์ชัน " + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "2.0.0");
    public bool IsElevated => SystemHelpers.IsElevated;

    public event Action? DiskFilterChanged;

    partial void OnChunkSizeMBChanged(int value)
    {
        _settings.ChunkSizeMB = value;
        _settings.Save();
    }

    partial void OnShowFixedUsbChanged(bool value)
    {
        _settings.ShowFixedUsbDisks = value;
        _settings.Save();
        DiskFilterChanged?.Invoke();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        System.IO.Directory.CreateDirectory(HistoryStore.Folder);
        SystemHelpers.OpenFolder(HistoryStore.Folder);
    }
}
