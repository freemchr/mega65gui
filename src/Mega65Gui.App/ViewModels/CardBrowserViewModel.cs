using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.App.Views;
using Mega65Gui.Core.Cores;
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
        RefreshDrives();
        _idle.Start();
        return Task.CompletedTask;
    }

    /// <summary>Set by the shell so the page can open another page.</summary>
    public Action<string>? Navigate { get; set; }

    [RelayCommand] private void OpenSdSync() => Navigate?.Invoke("SD Card Sync");
    [RelayCommand] private void OpenDevice() => Navigate?.Invoke("Device");

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

    [ObservableProperty] private bool _isTransferring;
    [ObservableProperty] private double _transferPercent;
    [ObservableProperty] private string _transferText = "";
    /// <summary>Write names to the card in capitals, as the MEGA65 shows them in a directory listing.</summary>
    [ObservableProperty] private bool _upperCaseNames;
    private CancellationTokenSource? _stop;

    [RelayCommand]
    private void StopTransfer()
    {
        _stop?.Cancel();
        TransferText = "Stopping after the file being copied…";
    }

    [RelayCommand]
    private Task UploadAsync(IList? selected) => UploadFilesAsync(selected?.OfType<LocalItem>().Select(i => i.FullPath).ToList() ?? []);

    /// <summary>Copies PC files and folders into the card's current folder (also used for drag-and-drop from Explorer).</summary>
    public Task UploadFilesAsync(IReadOnlyList<string> paths)
    {
        if (IsTransferring) { Status = "Wait for the copy to finish, or press Stop."; return Task.CompletedTask; }
        // A whole drive (E:\) has no folder name to give it on the card.
        var items = paths.Where(p => File.Exists(p) || Directory.Exists(p) && Path.GetDirectoryName(p.TrimEnd('\\')) is not null).ToList();
        if (items.Count == 0) { Status = "Select one or more files or folders on the PC side (not a whole drive)."; return Task.CompletedTask; }
        IReadOnlyList<string> warnings;
        try { warnings = CardNames.Warnings(LocalTree(items).Select(i => (UpperCaseNames ? i.Name.ToUpperInvariant() : i.Name, i.Size, i.IsDirectory)).ToList()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = $"Cannot read {e.Message}"; return Task.CompletedTask; }
        if (warnings.Count > 0 && MessageBox.Show(Application.Current.MainWindow,
                $"Some names may cause trouble on the MEGA65:\n\n{string.Join("\n", warnings.Take(12))}{(warnings.Count > 12 ? $"\n…and {warnings.Count - 12} more" : "")}\n\nCopy anyway?",
                "Check names", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return Task.CompletedTask;
        return TransferAsync($"Copying {Count(items)} to the card…", async (tree, here, stop) =>
        {
            var r = await new CardTransfer(tree) { UpperCaseNames = UpperCaseNames, AskOverwrite = AskReplace, Progress = TransferProgressReporter() }
                .UploadAsync(items, here, stop);
            return Summary(r, "to the card");
        });
    }

    /// <summary>Every file and folder under the chosen PC items, for the name check.</summary>
    private static IEnumerable<(string Name, long Size, bool IsDirectory)> LocalTree(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (File.Exists(p)) { yield return (Path.GetFileName(p), new FileInfo(p).Length, false); continue; }
            yield return (Path.GetFileName(p), 0, true);
            IEnumerable<FileSystemInfo> inside;
            try { inside = new DirectoryInfo(p).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var i in inside.Where(i => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0))
                yield return (i.Name, i is FileInfo f ? f.Length : 0, i is DirectoryInfo);
        }
    }

    private static string Count(IReadOnlyCollection<string> paths) =>
        paths.Count == 1 ? Path.GetFileName(paths.First()) : $"{paths.Count} items";

    [RelayCommand]
    private Task DownloadSelectedAsync(IList? selected) => DownloadAsync(selected?.OfType<RemoteEntry>().ToList() ?? []);

    /// <summary>Copies card files and folders into the PC folder (also used for drag-and-drop to the PC side).</summary>
    public Task DownloadAsync(IReadOnlyList<RemoteEntry> items)
    {
        if (items.Count == 0) { Status = "Select one or more files or folders on the MEGA65 side."; return Task.CompletedTask; }
        if (IsTransferring) { Status = "Wait for the copy to finish, or press Stop."; return Task.CompletedTask; }
        var target = LocalPath;
        return TransferAsync($"Copying {(items.Count == 1 ? items[0].Name : $"{items.Count} items")} to {target}…", async (tree, here, stop) =>
        {
            var r = await new CardTransfer(tree) { AskOverwrite = AskReplace, Progress = TransferProgressReporter() }
                .DownloadAsync(items, here, target, stop);
            LoadLocal();
            return Summary(r, $"to {target}");
        });
    }

    private static Task<OverwriteChoice> AskReplace(string what) => Task.FromResult(ReplaceDialog.Ask(what));

    private Progress<TransferProgress> TransferProgressReporter() => new(p =>
    {
        TransferPercent = p.Fraction * 100;
        if (_stop?.IsCancellationRequested != true)
            TransferText = p.Current.Length == 0 ? "" : $"{p.FilesDone + 1} of {p.FileCount}: {p.Current}";
    });

    private static string Summary(TransferResult r, string where)
    {
        var text = r.Stopped ? $"Stopped. Copied {r.Copied} file(s) {where}." : $"Copied {r.Copied} file(s) {where}.";
        if (r.FoldersMade > 0) text += $" Made {r.FoldersMade} folder(s).";
        if (r.Skipped > 0) text += $" Kept {r.Skipped} that were already there.";
        if (r.Failed > 0) text += $" {r.Failed} failed.";
        if (r.Problems.Count > 0) text += " Problems: " + string.Join("; ", r.Problems.Take(5)) + (r.Problems.Count > 5 ? "…" : "");
        return text;
    }

    /// <summary>Runs a copy in the card's current folder with progress and Stop, then shows that folder again.</summary>
    private Task TransferAsync(string busy, Func<FtpCardTree, string, CancellationToken, Task<string>> copy) => CardAsync(busy, async s =>
    {
        var here = s.CurrentDirectory.Trim('/');
        var tree = new FtpCardTree(s, "SD card in the MEGA65");
        tree.AlreadyIn(here);
        _stop = new CancellationTokenSource();
        IsTransferring = true;
        TransferPercent = 0;
        TransferText = "";
        try
        {
            return await copy(tree, here, _stop.Token);
        }
        finally
        {
            IsTransferring = false;
            _stop?.Dispose();
            _stop = null;
            // Back to the folder the user was looking at, whatever the copy walked through. If the
            // session has died, the copy's own error is the one to show.
            try
            {
                await tree.OpenAsync(here);
                await ListRemoteAsync(s);
            }
            catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException) { }
            TransferText = "";
        }
    });

    // ---------------- card information ----------------

    [ObservableProperty] private string _cardSpace = "";

    /// <summary>Free space ("df", which reads the whole allocation table, so slow on big cards) and the ROMs on the card.</summary>
    [RelayCommand]
    private Task CardInfoAsync() => CardAsync("Counting free space on the card (this takes a while on large cards)…", async s =>
    {
        var df = await s.FreeSpaceAsync();
        var roms = await s.ExecuteAsync("roms");
        foreach (var line in (df.Output + "\n" + roms.Output).Split('\n', StringSplitOptions.RemoveEmptyEntries)) AddOutput(line);
        CardSpace = SpaceText(df.Output);
        return Problems(df) ?? (CardSpace.Length > 0 ? $"Card: {CardSpace}. The ROMs on the card are listed under Card tool output." : "See Card tool output.");
    });

    private static string SpaceText(string output) =>
        Mega65Gui.Core.Sd.CardSpace.Parse(output) is { } x ? $"{x.FreeMb:N0} MB free of {x.TotalMb:N0} MB" : "";

    // ---------------- card readers on this PC ----------------

    public sealed record DriveChoice(string Root, string Label)
    {
        public override string ToString() => Label;
    }

    public ObservableCollection<DriveChoice> Drives { get; } = [];
    [ObservableProperty] private DriveChoice? _selectedDrive;
    [ObservableProperty] private bool _localIsRemovable;

    /// <summary>Removable drives, such as an SD card in this PC's card reader.</summary>
    [RelayCommand]
    private void RefreshDrives()
    {
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable && d.IsReady))
        {
            string label;
            try { label = $"{d.Name.TrimEnd('\\')} {(d.VolumeLabel.Length > 0 ? d.VolumeLabel : "Removable")} ({d.TotalSize / 1_000_000_000.0:0.#} GB)"; }
            catch (IOException) { label = d.Name; }
            Drives.Add(new DriveChoice(d.Name, label));
        }
    }

    partial void OnSelectedDriveChanged(DriveChoice? value)
    {
        if (value is null) return;
        LocalPath = value.Root;
        LoadLocal();
    }

    partial void OnLocalPathChanged(string value) => LocalIsRemovable = CardEjector.RemovableDriveOf(value) is not null;

    [RelayCommand]
    private void EjectLocal()
    {
        if (CardEjector.RemovableDriveOf(LocalPath) is not { } drive) return;
        try
        {
            CardEjector.Eject(drive);
            LocalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            SelectedDrive = null;
            LoadLocal();
            RefreshDrives();
            Status = $"{drive} ejected. You can take the card out.";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { Status = e.Message; }
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
