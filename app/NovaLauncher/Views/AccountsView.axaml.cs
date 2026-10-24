using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NovaLauncher.ViewModels;

namespace NovaLauncher.Views;

public partial class AccountsView : UserControl
{
    public AccountsView() => InitializeComponent();

    private async void Yggdrasil_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccountsViewModel vm) return;

        var server = new TextBox { Watermark = "外置登录服务器，如 https://littleskin.cn/api/yggdrasil", Width = 400 };
        var user = new TextBox { Watermark = "用户名 / 邮箱", Width = 400, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        var pass = new TextBox { Watermark = "密码", Width = 400, PasswordChar = '●', Margin = new Avalonia.Thickness(0, 8, 0, 0) };

        var dlg = new Window
        {
            Title = "Yggdrasil 外置登录",
            Width = 480,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "支持 LittleSkin、Blessing Skin、自建 authlib-injector 等任意外置登录服务器", FontSize = 12, Foreground = Brushes.Gray },
                    server, user, pass,
                    new Button
                    {
                        Content = "登录",
                        Classes = { "primary" },
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Avalonia.Thickness(0, 14, 0, 0),
                    },
                },
            },
        };
        var okBtn = (Button)((StackPanel)dlg.Content).Children[4];
        okBtn.Click += async (_, _) =>
        {
            okBtn.IsEnabled = false;
            await vm.AddYggdrasilCommand.ExecuteAsync($"{server.Text}|{user.Text}|{pass.Text}");
            dlg.Close();
        };
        await dlg.ShowDialog(TopLevel.GetTopLevel(this) as Window ?? (Window)this.VisualRoot!);
    }
}
