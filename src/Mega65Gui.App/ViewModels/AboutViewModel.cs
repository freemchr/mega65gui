using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.App.ViewModels;

public sealed partial class AboutViewModel : PageViewModel
{
    public const string RepositoryUrl = "https://github.com/freemchr/mega65gui";
    public const string IssuesUrl = RepositoryUrl + "/issues";

    private readonly AppServices services;

    public AboutViewModel(AppServices services) : base("About", "")
    {
        this.services = services;
        _checkForNewVersions = services.Settings.CheckForNewVersions;
        services.NewReleaseChanged += () => Application.Current.Dispatcher.Invoke(ShowCheck);
        ShowCheck();
    }

    public string Version { get; } = AppReleaseCheck.Display(AppServices.AppVersion);

    [ObservableProperty] private bool _checkForNewVersions;
    [ObservableProperty] private string _newVersionText = "";
    [ObservableProperty] private string? _newVersionUrl;

    partial void OnCheckForNewVersionsChanged(bool value)
    {
        services.Settings.CheckForNewVersions = value;
        try { services.Settings.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = $"The setting could not be saved: {e.Message}"; }
    }

    private void ShowCheck()
    {
        NewVersionText = services.NewReleaseCheckText ?? "";
        NewVersionUrl = services.NewRelease?.Url;
    }

    [RelayCommand]
    private Task CheckNowAsync() => GuardAsync(async () => { await services.CheckForNewVersionAsync(); Status = ""; }, "Checking GitHub for a new version…");

    /// <summary>Set by the shell: opens the Report a problem window.</summary>
    public Action? ReportProblem { get; set; }

    [RelayCommand] private void OpenReportProblem() => ReportProblem?.Invoke();

    [RelayCommand]
    private static void Open(string url) => OpenUrl(url);

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
