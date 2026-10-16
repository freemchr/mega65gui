using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.App.Views;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Sd;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

public sealed record LocalItem(string Name, string FullPath, bool IsDirectory, long? Size, DateTime Modified);

/// <summary>
/// Two-pane file manager: a PC folder on the left, the SD card inside the MEGA65 on the right
/// (over USB/JTAG or Ethernet via an interactive mega65_ftp session).
/// </summary>
public sealed partial class CardBrowserViewModel : PageViewModel
{
    private readonly AppServices services;

    public CardBrowserViewModel(AppServices services) : base("SD Card Browser", "\uE8B7")
    {
        this.services = services;
        _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
            ? Path.Combine(home, "Downloads") : "C:\\";
        services.FtpSessionChanged += () => Application.Current.Dispatcher.Invoke(() =>
        {
            IsOpen = services.HasFtpSession;
            if (!IsOpen) { RemoteItems.Clear(); RemotePath = ""; }
        });
        services.ConnectionChanged += () => IsConnected = services.Connection is not null;
        _idle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _idle.Tick += async (_, _) => await CloseIfIdleAsync();
    }

    private readonly System.Windows.Threading.DispatcherTimer _idle;
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(90);

    public ObservableCollection<LocalItem> LocalItems { get; } = [];
    public ObservableCollection<RemoteEntry> RemoteItems { get; } = [];
    public ObservableCollection<string> Output { get; } = [];

    [ObservableProperty] private string _localPath;
    [ObservableProperty] private string _remotePath = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isConnected;

    public override Task OnShownAsync()
    {
        IsConnected = services.Connection is not null;
        LoadLocal();
        _idle.Start();
        return Task.CompletedTask;
    }

    /// <summary>Called when the user leaves the page: release the port.</summary>
    public async Task OnHiddenAsync()
    {
        _idle.Stop();
        await services.CloseFtpSessionAsync();
    }

    private async Task CloseIfIdleAsync()
    {
        if (IsBusy || services.CurrentFtpSession is not { IsOpen: true } s) return;
        if (DateTime.Now - s.LastUsed > IdleClose)
        {
            await services.CloseFtpSessionAsync();
            Status = "Closed the card connection after a period without use. Click Open to reconnect.";
        }
    }

    // ---------------- PC side ----------------

    private void LoadLocal()
    {
        LocalItems.Clear();
        try
        {
            var dir = new DirectoryInfo(LocalPath);
            foreach (var d in dir.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                LocalItems.Add(new LocalItem(d.Name, d.FullName, true, null, d.LastWriteTime));
            foreach (var f in dir.EnumerateFiles().Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                LocalItems.Add(new LocalItem(f.Name, f.FullName, false, f.Length, f.LastWriteTime));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = $"Cannot open {LocalPath}: {e.Message}";
        }
    }

    [RelayCommand]
    private void BrowseLocal()
    {
        var dlg = new OpenFolderDialog { InitialDirectory = LocalPath };
        if (dlg.ShowDialog() == true) { LocalPath = dlg.FolderName; LoadLocal(); }
    }

    [RelayCommand]
    private void LocalUp()
    {
        if (Directory.GetParent(LocalPath) is { } parent) { LocalPath = parent.FullName; LoadLocal(); }
    }

    [RelayCommand] private void LocalRefresh() => LoadLocal();

    [RelayCommand]
    private void OpenLocal(LocalItem? item)
    {
        if (item is null) return;
        if (item.IsDirectory) { LocalPath = item.FullPath; LoadLocal(); }
        else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FullPath}\""));
    }

    // ---------------- MEGA65 side ----------------

    private async Task<FtpSession> SessionAsync()
    {
        if (services.Connection is null)
            throw new InvalidOperationException("Connect to the MEGA65 first (Device page, or the Update page's Connect button).");
        var opening = !services.HasFtpSession;
        if (opening) Status = "Opening the MEGA65's SD card (this loads a helper into the MEGA65's memory)…";
        var s = await services.GetFtpSessionAsync();
        if (opening) s.Log += line => Application.Current.Dispatcher.BeginInvoke(() => AddOutput(line));
        return s;
    }

    private void AddOutput(string line)
    {
        Output.Add(line.TrimEnd());
        while (Output.Count > 300) Output.RemoveAt(0);
    }

    private async Task ListRemoteAsync(FtpSession s)
    {
        var items = await s.ListAsync();
        RemoteItems.Clear();
        foreach (var e in items.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)) RemoteItems.Add(e);
        RemotePath = s.CurrentDirectory;
    }

    /// <summary>Runs a card operation, reporting any error mega65_ftp printed.</summary>
    private Task CardAsync(string busy, Func<FtpSession, Task<string?>> op) => GuardAsync(async () =>
    {
        var s = await SessionAsync();
        Status = busy;
        var done = await op(s);
        Status = done ?? "";
    }, busy);

    private static string? Problems(params FtpReply[] replies)
    {
        var errors = replies.SelectMany(r => r.Errors).Distinct().ToList();
        return errors.Count == 0 ? null : "The MEGA65 reported: " + string.Join("; ", errors);
    }

    /// <summary>Warns before the helper overwrites memory; asked each time the card is opened.</summary>
    public static bool ConfirmMemoryUse() => MessageBox.Show(Application.Current.MainWindow,
        "Opening the card loads a small helper program into the MEGA65's memory.\n\n" +
        "• Anything in memory now, such as a BASIC program you have not saved, will be LOST.\n" +
        "• The MEGA65 RESETS when the card connection closes: when you leave this page, after 90 seconds without use, " +
        "or when you use another Device or Update action.\n\nSave any work on the MEGA65 first. Open the card now?",
        "Open the MEGA65's SD card", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    [RelayCommand]
    private Task OpenCardAsync()
    {
        if (!services.HasFtpSession && !ConfirmMemoryUse()) return Task.CompletedTask;
        return CardAsync("Reading the card…", async s => { await ListRemoteAsync(s); return null; });
    }
    [RelayCommand] private Task RemoteRefreshAsync() => OpenCardAsync();

    [RelayCommand]
    private Task RemoteUpAsync() => CardAsync("Opening folder…", async s =>
    {
        var r = await s.ChangeDirectoryAsync("..");
        await ListRemoteAsync(s);
        return Problems(r);
    });

    [RelayCommand]
    private Task OpenRemoteAsync(RemoteEntry? item)
    {
        if (item is null) return Task.CompletedTask;
        if (!item.IsDirectory) return DownloadAsync(new[] { item });
        return CardAsync($"Opening {item.Name}…", async s =>
        {
            var r = await s.ChangeDirectoryAsync(item.ShortName);
            await ListRemoteAsync(s);
            return Problems(r);
        });
    }

    [RelayCommand]
    private Task CloseCardAsync() => GuardAsync(async () =>
    {
        await services.CloseFtpSessionAsync();
        Status = "Card connection closed. The MEGA65 was reset.";
    });

    // ---------------- transfers ----------------

    [RelayCommand]
    private Task UploadAsync(IList? selected) => UploadFilesAsync(selected?.OfType<LocalItem>().Where(i => !i.IsDirectory).Select(i => i.FullPath).ToList() ?? []);

    /// <summary>Uploads PC files into the card's current folder (also used for drag-and-drop from Explorer).</summary>
    public Task UploadFilesAsync(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) { Status = "Select one or more files on the PC side (folders are not uploaded)."; return Task.CompletedTask; }
        return CardAsync($"Uploading {files.Count} file(s)…", async s =>
        {
            if (RemoteItems.Count == 0) await ListRemoteAsync(s);
            var clashes = files.Where(f => RemoteItems.Any(r => !r.IsDirectory && r.Matches(Path.GetFileName(f)))).Select(Path.GetFileName).ToList();
            if (clashes.Count > 0 && MessageBox.Show(Application.Current.MainWindow,
                    $"Replace {clashes.Count} file(s) on the MEGA65's card?\n\n{string.Join("\n", clashes.Take(10))}",
                    "Replace files", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return "Upload cancelled.";
            var replies = new List<FtpReply>();
            foreach (var f in files)
            {
                Status = $"Uploading {Path.GetFileName(f)}…";
                replies.Add(await s.UploadAsync(f, Path.GetFileName(f)));
            }
            await ListRemoteAsync(s);
            return Problems([.. replies]) ?? $"Uploaded {files.Count} file(s) to {s.CurrentDirectory}.";
        });
    }

    [RelayCommand]
    private Task DownloadSelectedAsync(IList? selected) => DownloadAsync(selected?.OfType<RemoteEntry>().ToList() ?? []);

    /// <summary>Downloads card files into the PC folder (also used for drag-and-drop to the PC side).</summary>
    public Task DownloadAsync(IReadOnlyList<RemoteEntry> items)
    {
        var files = items.Where(i => !i.IsDirectory).ToList();
        if (files.Count == 0) { Status = "Select one or more files on the MEGA65 side (folders are not downloaded)."; return Task.CompletedTask; }
        return CardAsync($"Downloading {files.Count} file(s)…", async s =>
        {
            var clashes = files.Where(f => File.Exists(Path.Combine(LocalPath, f.Name))).Select(f => f.Name).ToList();
            if (clashes.Count > 0 && MessageBox.Show(Application.Current.MainWindow,
                    $"Replace {clashes.Count} file(s) in {LocalPath}?\n\n{string.Join("\n", clashes.Take(10))}",
                    "Replace files", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return "Download cancelled.";
            var replies = new List<FtpReply>();
            foreach (var f in files)
            {
                Status = $"Downloading {f.Name}…";
                replies.Add(await s.DownloadAsync(f.ShortName, Path.Combine(LocalPath, f.Name)));
            }
            LoadLocal();
            return Problems([.. replies]) ?? $"Downloaded {files.Count} file(s) to {LocalPath}.";
        });
    }

    // ---------------- card file operations ----------------

    [RelayCommand]
    private Task NewFolderAsync()
    {
        var name = InputDialog.Ask("New folder", "Name of the new folder on the MEGA65's card:");
        if (name is null) return Task.CompletedTask;
        return CardAsync($"Creating {name}…", async s =>
        {
            var r = await s.MakeDirectoryAsync(name);
            await ListRemoteAsync(s);
            return Problems(r);
        });
    }

    [RelayCommand]
    private Task RenameAsync(IList? selected)
    {
        if (selected?.OfType<RemoteEntry>().ToList() is not [var item]) { Status = "Select one file or folder to rename."; return Task.CompletedTask; }
        var name = InputDialog.Ask("Rename", $"New name for {item.Name}:", item.Name);
        if (name is null || name == item.Name) return Task.CompletedTask;
        return CardAsync($"Renaming {item.Name}…", async s =>
        {
            var r = await s.RenameAsync(item.ShortName, name);
            await ListRemoteAsync(s);
            return Problems(r);
        });
    }

    [RelayCommand]
    private Task DeleteAsync(IList? selected)
    {
        var items = selected?.OfType<RemoteEntry>().ToList() ?? [];
        if (items.Count == 0) { Status = "Select what to delete on the MEGA65 side."; return Task.CompletedTask; }
        if (MessageBox.Show(Application.Current.MainWindow,
                $"Permanently delete {items.Count} item(s) from the MEGA65's SD card? This cannot be undone.\n\n{string.Join("\n", items.Take(10).Select(i => i.Name + (i.IsDirectory ? "/" : "")))}",
                "Delete from card", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        return CardAsync($"Deleting {items.Count} item(s)…", async s =>
        {
            var replies = new List<FtpReply>();
            foreach (var i in items) replies.Add(await s.DeleteAsync(i.ShortName));
            await ListRemoteAsync(s);
            return Problems([.. replies]) ?? $"Deleted {items.Count} item(s).";
        });
    }

    [RelayCommand]
    private Task MountAsync(IList? selected)
    {
        if (selected?.OfType<RemoteEntry>().ToList() is not [var item] || !item.Name.EndsWith(".d81", StringComparison.OrdinalIgnoreCase))
        { Status = "Select one .D81 disk image to mount."; return Task.CompletedTask; }
        return CardAsync($"Mounting {item.Name}…", async s =>
        {
            var r = await s.MountAsync(item.ShortName);
            return Problems(r) ?? $"{item.Name} is mounted as drive 8.";
        });
    }

    [RelayCommand]
    private Task InfoAsync(IList? selected)
    {
        if (selected?.OfType<RemoteEntry>().ToList() is not [var item] || item.IsDirectory) { Status = "Select one file to inspect."; return Task.CompletedTask; }
        return CardAsync($"Reading {item.Name}'s layout on the card…", async s =>
        {
            var r = await s.ClustersAsync(item.ShortName);
            foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) AddOutput(line);
            return Problems(r) ?? $"Cluster layout of {item.Name} is shown below.";
        });
    }
}
