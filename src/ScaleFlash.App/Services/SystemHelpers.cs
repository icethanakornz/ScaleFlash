using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;

namespace ScaleFlash.App.Services;

public static partial class SystemHelpers
{
    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Starts a new elevated copy of the program and closes this one.</summary>
    public static void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user declined the UAC prompt.
        }
    }

    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    /// <summary>Keeps the PC from sleeping while cards are being written (call on the UI thread).</summary>
    public static void PreventSleep(bool prevent) =>
        SetThreadExecutionState(prevent ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

    public static void PlaySuccess() => SystemSounds.Asterisk.Play();

    public static void PlayError() => SystemSounds.Hand.Play();

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception) { /* ignore */ }
    }
}
