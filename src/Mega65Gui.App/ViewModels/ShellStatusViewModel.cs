using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.App.ViewModels;

/// <summary>Always-visible sidebar footer: Filehost account, device connection and tools.</summary>
public sealed partial class ShellStatusViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ShellStatusViewModel(AppServices services)
    {
        _services = services;
        services.Session.Changed += () => Application.Current.Dispatcher.Invoke(Refresh);
        Refresh();
    }

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _accountName = "Filehost";
    [ObservableProperty] private string _accountTooltip = "";
    [ObservableProperty] private string _accountDetail = "Not signed in";
    [ObservableProperty] private string _initial = "?";
    [ObservableProperty] private string _deviceText = "No device";
    [ObservableProperty] private bool _deviceConnected;
    [ObservableProperty] private string _toolsText = "Tools not installed";
    [ObservableProperty] private bool _toolsInstalled;
    [ObservableProperty] private string? _message;

    public void Refresh()
    {
        IsSignedIn = _services.Session.IsSignedIn;
        var user = _services.Session.UserName;
        // E-mail addresses are long; show the local part and keep the full name in the tooltip.
        AccountName = user is null ? "Filehost account" : user.Split('@')[0];
        AccountTooltip = user is null ? "Sign in to files.mega65.org" : $"Signed in to files.mega65.org as {user}";
        AccountDetail = IsSignedIn ? "Signed in to Filehost" : "Not signed in";
        Initial = IsSignedIn ? _services.Session.UserName![..1].ToUpperInvariant() : "?";

        DeviceConnected = _services.Connection is not null;
        DeviceText = _services.Connection is { } c
            ? _services.Device.Model == Core.Device.HardwareModel.Unknown ? c.ToString() : $"MEGA65 {_services.Device.Model} · {c.Address}"
            : "No device connected";

        ToolsInstalled = _services.Tools.Current is not null;
        ToolsText = _services.Tools.Current is { } t ? $"Tools {ShortVersion(t.Version)}" : "Tools not installed";
    }

    private static string ShortVersion(string? v) => v is null ? "installed" : v.Split('-')[0];

    [RelayCommand]
    private void SignIn()
    {
        Message = null;
        Views.SignInWindow.Show(_services);
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _services.Session.SignOutAsync();
        if (_services.Credentials.Load() is { } saved) _services.Credentials.Save(saved.UserName, null);
    }

    /// <summary>Signs in silently at startup if the user chose to remember the password.</summary>
    public async Task TryAutoSignInAsync()
    {
        if (_services.Session.IsSignedIn) return;
        if (_services.Credentials.Load() is not { Password: { } password } saved) return;
        try
        {
            var r = await _services.Session.SignInAsync(saved.UserName, password);
            if (r.Result == SignInResult.InvalidCredentials) Message = "Saved password rejected. Please sign in again.";
        }
        catch (HttpRequestException) { /* offline: stay signed out */ }
    }
}
