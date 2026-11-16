using Avalonia.Input;
using Avalonia.Controls;

namespace NovaLauncher.Views;

public partial class ModSearchView : UserControl
{
    public ModSearchView()
    {
        InitializeComponent();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is ViewModels.ModSearchViewModel vm)
            {
                vm.SearchCommand.Execute(null);
            }
        }
    }
}