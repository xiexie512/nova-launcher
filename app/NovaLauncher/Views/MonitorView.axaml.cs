using Avalonia.Controls;

namespace NovaLauncher.Views;

public partial class MonitorView : UserControl
{
    public MonitorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MonitorViewModel vm)
                Chart.ViewModel = vm;
        };
    }
}
