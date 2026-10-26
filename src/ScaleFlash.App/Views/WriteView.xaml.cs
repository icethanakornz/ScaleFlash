using System.Windows.Controls;
using ScaleFlash.App.ViewModels;

namespace ScaleFlash.App.Views;

public partial class WriteView
{
    public WriteView() => InitializeComponent();

    private async void OnRecentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RecentBox.SelectedItem is not string path || DataContext is not WriteViewModel vm) return;
        RecentBox.SelectedIndex = -1;
        await vm.LoadImageAsync(path);
    }
}
