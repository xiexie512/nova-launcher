using Avalonia.Controls;
using Avalonia.Threading;
using NovaLauncher.Services;

namespace NovaLauncher.Views;

public partial class ConsoleView : UserControl
{
    public ConsoleView()
    {
        InitializeComponent();
        Unloaded += (_, _) => LogService.LineAppended -= OnLine;
        LogService.LineAppended += OnLine;
    }

    private void OnLine(string line)
    {
        if (DataContext is ViewModels.ConsoleViewModel vm && vm.AutoScroll)
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd());
    }
}
