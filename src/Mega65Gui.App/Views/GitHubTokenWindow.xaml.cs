using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.App.Views;

/// <summary>Saves, checks or removes the optional GitHub token, and shows the hourly allowance.</summary>
public partial class GitHubTokenWindow : Window
{
    // GitHub's page for a new fine-grained personal access token.
    private const string NewTokenUrl = "https://github.com/settings/personal-access-tokens/new";

    private readonly AppServices _services;

    private GitHubTokenWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        RemoveButton.Visibility = string.IsNullOrWhiteSpace(services.GitHub.Token) ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            TokenBox.Focus();
            await ShowAllowanceAsync();
        };
    }

    /// <summary>True when a token was saved or removed.</summary>
    public static bool Show(AppServices services) =>
        new GitHubTokenWindow(services) { Owner = Application.Current.MainWindow }.ShowDialog() == true;

    private async Task ShowAllowanceAsync()
    {
        var hasToken = !string.IsNullOrWhiteSpace(_services.GitHub.Token);
        try
        {
            var limit = await _services.GitHub.GetRateLimitAsync();
            StatusTitle.Text = hasToken ? "A token is saved" : "No token saved";
            StatusDetail.Text = $"{limit.Remaining:N0} of {limit.Limit:N0} GitHub look-ups left this hour" +
                (limit.Remaining < limit.Limit ? $", back to {limit.Limit:N0} at {limit.Reset.ToLocalTime():HH:mm}." : ".");
            StatusIcon.Text = limit.Remaining == 0 ? "" : hasToken ? "" : "";
        }
        catch (Exception e) when (e is HttpRequestException or FilehostUploadException or TaskCanceledException)
        {
            StatusTitle.Text = hasToken ? "A token is saved" : "No token saved";
            StatusDetail.Text = $"Could not check GitHub: {e.Message}";
        }
    }

    private void OpenGitHub(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(NewTokenUrl) { UseShellExecute = true });

    private void TokenChanged(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = TokenBox.Password.Trim().Length > 0;
        Message.Visibility = Visibility.Collapsed;
    }

    private async void Save(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(token, @"^[A-Za-z0-9_]{20,255}$"))
        {
            ShowMessage("That does not look like a GitHub token. Tokens start with github_pat_ (or ghp_ for classic ones).");
            return;
        }
        SaveButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        try
        {
            // Check the token before keeping it.
            var limit = await _services.GitHub.GetRateLimitAsync(token);
            _services.GitHubToken.Save(token);
            _services.GitHub.Token = token;
            MessageBox.Show(this, $"Token saved. GitHub now allows {limit.Limit:N0} look-ups an hour ({limit.Remaining:N0} left).",
                "GitHub token", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or FilehostUploadException or TaskCanceledException)
        {
            ShowMessage(ex.Message);
        }
        finally
        {
            SaveButton.IsEnabled = TokenBox.Password.Trim().Length > 0;
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private void Remove(object sender, RoutedEventArgs e)
    {
        _services.GitHubToken.Forget();
        _services.GitHub.Token = null;
        DialogResult = true;
    }

    private void ShowMessage(string text)
    {
        Message.Text = text;
        Message.Visibility = Visibility.Visible;
    }
}
