#if DEBUG || SNAPSHOT
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScaleFlash.App.Services;

/// <summary>
/// Debug-build only: renders the main window off-screen to PNG files without showing or activating it,
/// so the UI can be checked without touching the user's desktop.
/// SCALEFLASH_SNAPSHOT=out-prefix, SCALEFLASH_PAGE=index, SCALEFLASH_INSPECT_FILE=image path.
/// </summary>
internal static class Snapshot
{
    public static async Task RunAsync(string prefix)
    {
        if (Environment.GetEnvironmentVariable("SCALEFLASH_ICONTEST") is { Length: > 0 } icons)
        {
            var panel = new System.Windows.Controls.WrapPanel { Width = 900, Background = Brushes.White };
            foreach (string name in icons.Split(','))
            {
                var sp = new System.Windows.Controls.StackPanel { Width = 150, Margin = new Thickness(6) };
                sp.Children.Add(new Wpf.Ui.Controls.SymbolIcon { Symbol = Enum.Parse<Wpf.Ui.Controls.SymbolRegular>(name), FontSize = 32, Foreground = Brushes.Black });
                sp.Children.Add(new System.Windows.Controls.TextBlock { Text = name, Foreground = Brushes.Black });
                panel.Children.Add(sp);
            }
            var win = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight, Left = -30000, Top = -30000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            win.Show();
            await Task.Delay(1500);
            Save(win, prefix + "_icons.png");
            Application.Current.Shutdown();
            return;
        }
        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        var vm = (ViewModels.MainViewModel)window.DataContext;
        if (int.TryParse(Environment.GetEnvironmentVariable("SCALEFLASH_PAGE"), out int page)) vm.SelectedIndex = page;
        if (Environment.GetEnvironmentVariable("SCALEFLASH_INSPECT_FILE") is { Length: > 0 } file) vm.Inspect.OpenFile(file);

        await Task.Delay(4000);
        Save(window, prefix + "_1.png");

        if (Environment.GetEnvironmentVariable("SCALEFLASH_BROWSE") is { Length: > 0 } path)
        {
            await vm.Inspect.NavigateAsync(path);
            await Task.Delay(800);
            Save(window, prefix + "_2.png");
        }
        if (Environment.GetEnvironmentVariable("SCALEFLASH_COMPARE") is { Length: > 0 } cmp)
        {
            await vm.Inspect.CompareFilesWithAsync(cmp);
            await Task.Delay(800);
            Save(window, prefix + "_cmp.png");
        }
        if (Environment.GetEnvironmentVariable("SCALEFLASH_TAB") is { Length: > 0 } tab)
        {
            vm.Inspect.SelectedTab = int.Parse(tab);
            await Task.Delay(500);
            Save(window, prefix + "_3.png");
        }
        if (Environment.GetEnvironmentVariable("SCALEFLASH_CLOSETEST") is { Length: > 0 })
        {
            window.Close(); // same path as the user clicking X: the process must exit on its own
            return;
        }
        Application.Current.Shutdown();
    }

    private static void Save(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        int w = (int)content.ActualWidth, h = (int)content.ActualHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
