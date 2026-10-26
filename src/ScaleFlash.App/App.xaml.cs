using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace ScaleFlash.App;

public partial class App : Application
{
#if DEBUG || SNAPSHOT
    private static readonly string MutexName = Environment.GetEnvironmentVariable("SCALEFLASH_MUTEX") ?? @"Local\ScaleFlash.SingleInstance";
#else
    private const string MutexName = @"Local\ScaleFlash.SingleInstance";
#endif
    private Mutex? _singleInstance;

    /// <summary>Set once the main window has closed; from then on the process must exit, never linger.</summary>
    public static bool IsExiting { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG || SNAPSHOT
        if (Environment.GetEnvironmentVariable("SCALEFLASH_SNAPSHOT") is { Length: > 0 } snapshot)
        {
            base.OnStartup(e);
            ApplicationThemeManager.Apply(ApplicationTheme.Light);
            _ = Services.Snapshot.RunAsync(snapshot);
            return;
        }
#endif
        DispatcherUnhandledException += OnUnhandledException;
        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();
        var window = new MainWindow();
        MainWindow = window;
#if DEBUG || SNAPSHOT
        if (Environment.GetEnvironmentVariable("SCALEFLASH_OFFSCREEN") is { Length: > 0 })
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = window.Top = -30000;
            window.ShowActivated = false;
        }
#endif
        window.Show();
#if DEBUG || SNAPSHOT
        if (Environment.GetEnvironmentVariable("SCALEFLASH_ZOMBIE") is { Length: > 0 })
            window.Hide(); // test only: imitate an instance stuck without a window
#endif
    }

    /// <summary>
    /// One ScaleFlash per desktop. If another copy is running with a window, bring it to the front.
    /// If it has no window and has been alive for a while, it is stuck (e.g. it failed while closing):
    /// end it so the user is never locked out.
    /// </summary>
    private bool AcquireSingleInstance()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _singleInstance = new Mutex(true, MutexName, out bool first);
            if (first) return true;
            _singleInstance.Dispose();
            _singleInstance = null;

            int me = Environment.ProcessId;
            var others = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Where(p => p.Id != me).ToList();
            var withWindow = others.FirstOrDefault(p => SafeHandle(p) != 0);
            if (withWindow is not null)
            {
                ShowWindow(withWindow.MainWindowHandle, 9 /* SW_RESTORE */);
                SetForegroundWindow(withWindow.MainWindowHandle);
                return false;
            }

            bool killedAny = false;
            foreach (var p in others)
            {
                // a copy that is still starting up has no window yet: leave it alone
                if (SafeStartTime(p) is { } t && DateTime.Now - t < TimeSpan.FromSeconds(15)) return false;
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                    killedAny = true;
                    LogError($"ended a stuck ScaleFlash process (PID {p.Id}) that had no window");
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
            if (!killedAny && others.Count > 0)
            {
                MessageBox.Show("มี ScaleFlash ตัวเก่าค้างอยู่ในเครื่องและปิดอัตโนมัติไม่ได้\nเปิด Task Manager แล้ว End task \"ScaleFlash\" จากนั้นเปิดโปรแกรมใหม่",
                    "ScaleFlash", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            Thread.Sleep(300);
        }
        return false;
    }

    private static nint SafeHandle(Process p)
    {
        try { return p.MainWindowHandle; } catch (InvalidOperationException) { return 0; }
    }

    private static DateTime? SafeStartTime(Process p)
    {
        try { return p.StartTime; } catch (Exception) { return null; }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception.ToString());
        if (IsExiting || Dispatcher.HasShutdownStarted)
        {
            // Never swallow errors while closing: that leaves an invisible process behind.
            Environment.Exit(1);
            return;
        }
        if (MainWindow is not { IsLoaded: true })
        {
            MessageBox.Show("เปิดโปรแกรมไม่สำเร็จ:" + Environment.NewLine + e.Exception.Message, "ScaleFlash", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
            return;
        }
        MessageBox.Show("เกิดข้อผิดพลาดที่ไม่คาดคิด:\n" + e.Exception.Message, "ScaleFlash", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    internal static void LogError(string text)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScaleFlash");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTime.Now:s} {text}{Environment.NewLine}");
        }
        catch (Exception) { /* best effort */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int cmd);
}
