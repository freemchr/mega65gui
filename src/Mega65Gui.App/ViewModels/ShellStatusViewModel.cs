using System.IO;
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
        services.ToolsChanged += () => Application.Current.Dispatcher.Invoke(Refresh);
        services.ConnectionChanged += () => { Refresh(); AnnounceConnection(); };
        services.NewReleaseChanged += () => Application.Current.Dispatcher.Invoke(ShowNewRelease);
        Refresh();
        _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastVisible = false; };
    }

    // ---- Connection notification: a banner at the top of the window when the connection changes ----

    public enum ToastKind { Success, Warning }

    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private string _toastDetail = "";
    [ObservableProperty] private ToastKind _toastLevel;
    [ObservableProperty] private bool _toastVisible;
    private readonly System.Windows.Threading.DispatcherTimer _toastTimer;
    private string? _announced;

    /// <summary>
    /// Shows a banner when the device connects, stops answering or disconnects. Other changes
    /// (for example the running core's description) do not repeat it.
    /// </summary>
    private void AnnounceConnection()
    {
        var c = _services.Connection;
        var d = _services.Device;
        var key = c is null ? $"none:{_services.LostPort}" : $"{c}:{d.Model}:{d.NoResponse}";
        if (key == _announced) return;
        var first = _announced is null;
        _announced = key;
        if (first && c is null) return; // nothing to say at start-up

        if (c is null)
        {
            if (_services.LostPort is not { } lost) { ToastVisible = false; return; }
            Show(ToastKind.Warning, $"Disconnected from {lost}", "The MEGA65 was switched off or its USB cable unplugged. It reconnects automatically when it comes back.");
        }
        else if (d.NoResponse)
            Show(ToastKind.Warning, $"{c.Address} opened, but the MEGA65 is not answering",
                "Make sure it is switched on and showing its normal screen, then press Connect again.");
        else if (c.Kind == Core.Device.ConnectionKind.Ethernet)
            Show(ToastKind.Success, $"Connected over Ethernet ({c.Address})", "Model and core cannot be read over Ethernet.");
        else
            Show(ToastKind.Success,
                d.Model == Core.Device.HardwareModel.Unknown ? $"Connected on {c.Address}" : $"Connected to MEGA65 {d.Model} on {c.Address}",
                $"Core {d.CoreBuild ?? "not read"} · ROM {d.RomVersion ?? "not read"}");
    }

    private void Show(ToastKind kind, string text, string detail)
    {
        ToastLevel = kind;
        ToastText = text;
        ToastDetail = detail;
        // Toggle so the slide-in animation runs again even if a banner is already showing.
        ToastVisible = false;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [RelayCommand]
    private void CloseToast()
    {
        _toastTimer.Stop();
        ToastVisible = false;
    }

    // ---- A newer MEGA65 GUI on GitHub: a row in the sidebar ----

    [ObservableProperty] private string? _newVersionText;

    private void ShowNewRelease() =>
        NewVersionText = _services.NewRelease is { } r && AppReleaseCheck.ParseTag(r.Tag) is { } v
            ? $"Version {AppReleaseCheck.Display(v)} is available" : null;

    [RelayCommand]
    private void OpenNewVersion()
    {
        if (_services.NewRelease?.Url is { } url) AboutViewModel.OpenUrl(url);
    }

    /// <summary>Checks GitHub for a newer version at start-up, unless turned off on the About page.</summary>
    public Task CheckForNewVersionAtStartupAsync() =>
        _services.Settings.CheckForNewVersions ? _services.CheckForNewVersionAsync() : Task.CompletedTask;

    /// <summary>Set by the shell: opens the Report a problem window with the app's current details.</summary>
    public Action? ReportProblem { get; set; }

    [RelayCommand] private void OpenReportProblem() => ReportProblem?.Invoke();

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _accountName = "Filehost";
    [ObservableProperty] private string _accountTooltip = "";
    [ObservableProperty] private string _accountDetail = "Not signed in";
    [ObservableProperty] private string _initial = "?";
    [ObservableProperty] private string _deviceText = "No device";
    [ObservableProperty] private bool _deviceConnected;
    [ObservableProperty] private string _toolsText = "Device tools not installed";

    /// <summary>Set by the shell so status lines can jump to their page.</summary>
    public Action<string>? Navigate { get; set; }

    [RelayCommand] private void OpenTools() => Navigate?.Invoke("Device Tools");

    [ObservableProperty] private bool _installingTools;
    [ObservableProperty] private string _installProgress = "";

    [RelayCommand]
    private async Task InstallToolsAsync()
    {
        Message = null;
        try
        {
            await _services.InstallDeviceToolsAsync(progress: new Progress<string>(p => InstallProgress = p));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"Device tools could not be installed: {e.Message}";
        }
    }
    [RelayCommand] private void OpenDevice() => Navigate?.Invoke("Device");
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
            : _services.LostPort is { } lost ? $"Disconnected · {lost}" : "No device connected";
        if (_services.Connection is { Kind: Core.Device.ConnectionKind.Serial } && _services.Device.NoResponse)
            DeviceText = $"{_services.Connection.Address} · MEGA65 not answering";

        ToolsInstalled = _services.Tools.Current is not null;
        InstallingTools = _services.IsInstallingTools;
        if (!InstallingTools) InstallProgress = "";
        ToolsText = _services.Tools.Current is { } t ? $"Device tools {ShortVersion(t.Version)}" : "Device tools not installed";
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
