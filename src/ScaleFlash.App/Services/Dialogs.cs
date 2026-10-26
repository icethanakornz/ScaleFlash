using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ScaleFlash.App.Services;

/// <summary>Fluent in-window dialogs hosted by MainWindow's ContentDialogHost.</summary>
public static class Dialogs
{
    public static ContentDialogHost? Host { get; set; }

    public static async Task<bool> ConfirmAsync(string title, object content, string primaryText,
        string closeText = "ยกเลิก", bool danger = false)
    {
        var dialog = new ContentDialog(Host)
        {
            Title = title,
            Content = Wrap(content),
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            DialogMaxWidth = 560,
        };
        return await dialog.ShowAsync(CancellationToken.None) == ContentDialogResult.Primary;
    }

    public static async Task AlertAsync(string title, object content)
    {
        var dialog = new ContentDialog(Host)
        {
            Title = title,
            Content = Wrap(content),
            CloseButtonText = "ตกลง",
            DialogMaxWidth = 560,
        };
        await dialog.ShowAsync(CancellationToken.None);
    }

    /// <summary>Read-only text viewer (file preview).</summary>
    public static async Task ShowTextAsync(string title, string text)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Leelawadee UI"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = 760,
            Height = 460,
        };
        var dialog = new ContentDialog(Host)
        {
            Title = title,
            Content = box,
            CloseButtonText = "ปิด",
            DialogMaxWidth = 840,
        };
        await dialog.ShowAsync(CancellationToken.None);
    }

    private static object Wrap(object content) => content is string s
        ? new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 14 }
        : content;

    /// <summary>Builds a paragraph followed by bullet lines, for confirmation dialogs.</summary>
    public static FrameworkElement Bullets(string intro, IEnumerable<string> lines, string? footer = null, bool footerIsWarning = false)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });
        foreach (string line in lines)
            panel.Children.Add(new TextBlock { Text = "•  " + line, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 2, 0, 2) });
        if (footer is not null)
        {
            var tb = new TextBlock { Text = footer, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 12, 0, 0) };
            if (footerIsWarning) tb.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            else tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            panel.Children.Add(tb);
        }
        return panel;
    }
}
