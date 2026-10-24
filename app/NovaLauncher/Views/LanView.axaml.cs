using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NovaLauncher.Services;

namespace NovaLauncher.Views;

public partial class LanView : UserControl
{
    public LanView() => InitializeComponent();

    private void CopyIp_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as ViewModels.LanViewModel;
        if (vm == null || vm.MyIp == "尚未分配") return;
        var tl = TopLevel.GetTopLevel(this);
        if (tl != null && tl.Clipboard != null)
            _ = tl.Clipboard.SetTextAsync(vm.MyIp);
    }

    private void OpenCentral_Click(object? sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo { FileName = "https://my.zerotier.com", UseShellExecute = true });

    private void Leave_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && DataContext is ViewModels.LanViewModel vm)
        {
            vm.LeaveCommand.Execute(id);
        }
    }

    private void AdminRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ZtAdminMember m } && DataContext is ViewModels.LanViewModel vm)
        {
            vm.RemoveMemberCommand.Execute(m);
        }
    }
}
