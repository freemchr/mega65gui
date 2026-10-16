using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Tools;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

public sealed partial class ToolsViewModel(AppServices services) : PageViewModel("Tools", "")
{
    public ObservableCollection<KeyValuePair<string, string>> Executables { get; } = [];

    [ObservableProperty] private string _installedText = "";

    public override Task OnShownAsync()
    {
        Show(services.Tools.Current);
        return Task.CompletedTask;
    }

    private void Show(ToolInstallation? t)
    {
        Executables.Clear();
        if (t is null)
        {
            InstalledText = "The official MEGA65 tools (m65, mega65_ftp, etherload) are not installed yet. Device features need them.";
            return;
        }
        InstalledText = $"Version {t.Version ?? "unknown"} · {t.Source} · installed {t.InstalledAt.LocalDateTime:g}";
        foreach (var e in t.Executables.OrderBy(e => e.Key)) Executables.Add(e);
    }

    [RelayCommand] private Task InstallReleaseAsync() => InstallAsync(ToolChannel.Release);
    [RelayCommand] private Task InstallDevelopmentAsync() => InstallAsync(ToolChannel.Development);

    private Task InstallAsync(ToolChannel channel) => GuardAsync(async () =>
    {
        var progress = new Progress<string>(s => Status = s);
        var t = await services.Tools.InstallFromFilehostAsync(services.Catalogue, services.Downloads, channel, progress);
        Show(t);
        Status = "Installed.";
    }, "Installing…");

    [RelayCommand]
    private Task UseFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Folder containing m65.exe / mega65_ftp.exe" };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        return GuardAsync(async () => { Show(await services.Tools.UseFolderAsync(dlg.FolderName)); Status = "Using tools from folder."; });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (services.Tools.Current is { } t) Process.Start("explorer.exe", t.Folder);
    }
}
