using System.Windows.Controls;
using System.Windows.Input;
using ScaleFlash.App.ViewModels;

namespace ScaleFlash.App.Views;

public partial class InspectView
{
    public InspectView() => InitializeComponent();

    private InspectViewModel? Vm => DataContext as InspectViewModel;

    private async void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm && FileGrid.SelectedItem is FileRow row)
            await vm.OpenEntryCommand.ExecuteAsync(row);
    }

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm is { } vm && sender is TextBox box)
            await vm.NavigateAsync(string.IsNullOrWhiteSpace(box.Text) ? "/" : box.Text.Trim());
    }
}
