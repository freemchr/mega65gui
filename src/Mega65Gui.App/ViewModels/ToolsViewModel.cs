using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Tools;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

public sealed partial class ToolsViewModel : PageViewModel
{
    private readonly AppServices services;

    public ToolsViewModel(AppServices services) : base("Device Tools", "\uE90F")
    {
        this.services = services;
        services.ToolsChanged += () => System.Windows.Application.Current.Dispatcher.Invoke(() => Show(services.Tools.Current));
    }

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
            InstalledText = "Not installed. The Device page needs the official MEGA65 command-line tools (m65, mega65_ftp, etherload) to talk to your MEGA65. Click Install device tools; the development build is recommended because it includes etherload for Ethernet.";
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
        await services.InstallDeviceToolsAsync(channel, progress);
        Status = "Installed.";
    }, "Installing…");

    [RelayCommand]
    private Task UseFolderAsync()
    {
        var dlg = new OpenFolderDialog { Title = "Folder containing m65.exe / mega65_ftp.exe" };
        if (dlg.ShowDialog() != true) return Task.CompletedTask;
        return GuardAsync(async () =>
        {
            await services.Tools.UseFolderAsync(dlg.FolderName);
            services.NotifyToolsChanged();
            Status = "Using tools from folder.";
        });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (services.Tools.Current is { } t) Process.Start("explorer.exe", t.Folder);
    }
}
