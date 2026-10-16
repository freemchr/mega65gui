using System.Net.Http;
using System.Windows;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.App.Views;

public partial class SignInWindow : Window
{
    private readonly AppServices _services;

    private SignInWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        if (services.Credentials.Load() is { } saved)
        {
            UserBox.Text = saved.UserName;
            RememberBox.IsChecked = saved.Password is not null;
        }
        Loaded += (_, _) => (UserBox.Text.Length == 0 ? (UIElement)UserBox : PasswordBox).Focus();
    }

    public static bool Show(AppServices services) =>
        new SignInWindow(services) { Owner = Application.Current.MainWindow }.ShowDialog() == true;

    private async void SignIn(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        if (user.Length == 0 || PasswordBox.Password.Length == 0) { ShowMessage("Enter your user name and password."); return; }

        SignInButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        Message.Visibility = Visibility.Collapsed;
        try
        {
            var outcome = await _services.Session.SignInAsync(user, PasswordBox.Password);
            if (outcome.Result != SignInResult.Success) { ShowMessage(outcome.Message ?? "Sign-in failed."); return; }

            _services.Credentials.Save(user, RememberBox.IsChecked == true ? PasswordBox.Password : null);
            DialogResult = true;
        }
        catch (HttpRequestException ex)
        {
            ShowMessage($"Could not reach the Filehost: {ex.Message}");
        }
        finally
        {
            SignInButton.IsEnabled = true;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }
}
