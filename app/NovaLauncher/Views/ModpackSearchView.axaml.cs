using Avalonia.Input;
using Avalonia.Controls;

namespace NovaLauncher.Views;

public partial class ModpackSearchView : UserControl
{
    public ModpackSearchView()
    {
        InitializeComponent();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is ViewModels.ModpackSearchViewModel vm)
            {
                vm.SearchCommand.Execute(null);
            }
        }
    }
}