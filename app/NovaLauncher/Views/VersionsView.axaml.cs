using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NovaLauncher.ViewModels;

namespace NovaLauncher.Views;

public partial class VersionsView : UserControl
{
    public VersionsView()
    {
        InitializeComponent();
    }

    private async Task OpenDownloadDialogAsync()
    {
        if (DataContext is not VersionsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this) as Window;
        if (top == null) return;
        var dlg = new VersionDownloadDialog();
        await dlg.ShowDialog(top);
        vm.RefreshDownloaded();
    }

    private async void Download_Click(object? sender, RoutedEventArgs e) => await OpenDownloadDialogAsync();
}
