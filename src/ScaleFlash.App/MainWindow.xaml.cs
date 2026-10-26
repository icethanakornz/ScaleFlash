using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using ScaleFlash.App.Services;
using ScaleFlash.App.ViewModels;

namespace ScaleFlash.App;

public partial class MainWindow
{
    private const int WM_DEVICECHANGE = 0x0219;
    private readonly MainViewModel _vm = new();
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Dialogs.Host = DialogHost;
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        _vm.Write.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WriteViewModel.AnyRunning) or nameof(WriteViewModel.OverallProgress) or nameof(WriteViewModel.LastBatchFailed))
                UpdateTaskbar();
        };

        AllowDrop = true;
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE) DiskWatcher.Shared.RefreshNow();
        return 0;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
        {
            if (_vm.SelectedIndex == 1)
            {
                _vm.Inspect.OpenFile(files[0]);
                return;
            }
            _vm.SelectedIndex = 0;
            await _vm.Write.LoadImageAsync(files[0]);
        }
    }

    private void UpdateTaskbar()
    {
        var w = _vm.Write;
        if (w.AnyRunning)
        {
            Taskbar.ProgressState = TaskbarItemProgressState.Normal;
            Taskbar.ProgressValue = w.OverallProgress;
        }
        else
        {
            Taskbar.ProgressState = w.LastBatchFailed ? TaskbarItemProgressState.Error : TaskbarItemProgressState.None;
            Taskbar.ProgressValue = w.LastBatchFailed ? 1 : 0;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.AnyRunning && !_closeConfirmed)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        App.IsExiting = true;
        // Backstop: whatever happens during WPF shutdown, the process must not outlive its window.
        new Thread(() =>
        {
            Thread.Sleep(5000);
            Environment.Exit(0);
        }) { IsBackground = true }.Start();
        base.OnClosed(e);
    }

    private async Task ConfirmCloseAsync()
    {
        bool ok = await Dialogs.ConfirmAsync("ยังเขียนการ์ดไม่เสร็จ",
            "ถ้าปิดโปรแกรมตอนนี้ การ์ดที่กำลังเขียนจะใช้งานไม่ได้ และต้องเขียนใหม่ ต้องการหยุดและปิดโปรแกรมหรือไม่?",
            "หยุดและปิด", "ทำงานต่อ", danger: true);
        if (!ok) return;
        _vm.CancelAll();
        for (int i = 0; i < 50 && _vm.AnyRunning; i++) await Task.Delay(100);
        _closeConfirmed = true;
        Close();
    }
}
