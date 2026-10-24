using Avalonia.Controls;

namespace NovaLauncher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => Services.MonitorService.Stop();
    }
}
