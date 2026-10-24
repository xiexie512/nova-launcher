using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NovaLauncher.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void AddArg_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm)
            vm.AddArgCommand.Execute(ArgBox.Text);
        ArgBox.Text = "";
    }
}
