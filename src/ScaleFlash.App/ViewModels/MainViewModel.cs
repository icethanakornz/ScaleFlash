using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScaleFlash.App.Services;

namespace ScaleFlash.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Write.HistoryChanged += History.Refresh;
        Inspect.DefaultImagePath = () => Write.Image?.Path ?? AppSettings.Current.LastImagePath;
        Write.InspectRequested += disk =>
        {
            SelectedIndex = 1;
            Inspect.SelectDisk(disk);
        };
        Guard.Hidden += message => Notice = message;
        Backup.HistoryChanged += History.Refresh;
        Settings.DiskFilterChanged += () =>
        {
            Write.ReloadDiskFilter();
            DiskWatcher.Shared.RefreshNow();
        };
    }

    public WriteViewModel Write { get; } = new();
    public InspectViewModel Inspect { get; } = new();
    public FormatPromptGuard Guard { get; } = new();

    /// <summary>Short status message shown at the bottom of the navigation bar.</summary>
    [ObservableProperty] private string? _notice;
    public BackupViewModel Backup { get; } = new();
    public HistoryViewModel History { get; } = new();
    public SettingsViewModel Settings { get; } = new();

    public bool IsElevated => SystemHelpers.IsElevated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    private int _selectedIndex;

    public object CurrentPage => SelectedIndex switch
    {
        1 => Inspect,
        2 => Backup,
        3 => History,
        4 => Settings,
        _ => Write,
    };

    partial void OnSelectedIndexChanged(int value)
    {
        if (value == 3) History.Refresh();
    }

    public bool AnyRunning => Write.AnyRunning || Backup.Job is { IsRunning: true };

    public void CancelAll()
    {
        Write.CancelAll();
        Backup.CancelAll();
    }

    [RelayCommand]
    private void RestartAsAdmin() => SystemHelpers.RestartElevated();
}
