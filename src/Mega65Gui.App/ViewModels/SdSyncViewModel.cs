using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.App.Views;
using Mega65Gui.Core.Packages;
using Mega65Gui.Core.Sd;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

public sealed partial class SdSyncViewModel(AppServices services) : PageViewModel("SD Card Sync", "")
{
    private SyncPlan? _plan;

    public ObservableCollection<SyncItem> Items { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] private string _packagePath = "";
    [ObservableProperty] private string _sdCardPath = "";
    [ObservableProperty] private string _summary = "Choose a release package (.7z/.zip or a folder) and your SD card, then compare.";
    [ObservableProperty] private bool _hideUnchanged = true;
    [ObservableProperty] private bool _canApply;

    partial void OnHideUnchangedChanged(bool value) => ShowPlan();

    [RelayCommand]
    private void BrowsePackageFile()
    {
        var dlg = new OpenFileDialog { Filter = "Release packages (*.7z;*.zip)|*.7z;*.zip", InitialDirectory = services.Paths.Downloads };
        if (dlg.ShowDialog() == true) PackagePath = dlg.FileName;
    }

    [RelayCommand]
    private void BrowsePackageFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Folder holding the files to copy to the SD card" };
        if (dlg.ShowDialog() == true) PackagePath = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseSdCard()
    {
        var dlg = new OpenFolderDialog { Title = "Select your MEGA65 SD card" };
        if (dlg.ShowDialog() == true) SdCardPath = dlg.FolderName;
    }

    [RelayCommand]
    private Task CompareAsync() => GuardAsync(async () =>
    {
        var source = await ResolveSourceAsync();
        var progress = new Progress<string>(s => Status = s);
        _plan = await Task.Run(() => new SdSyncPlanner().PlanAsync(source, SdCardPath, progress));
        ShowPlan();
        Status = "";
    }, "Comparing…");

    private async Task<string> ResolveSourceAsync()
    {
        if (Directory.Exists(PackagePath)) return PackagePath;
        if (!File.Exists(PackagePath)) throw new FileNotFoundException("Choose a release package or folder.");
        var dest = Path.Combine(services.Paths.Extracted, "manual-" + Path.GetFileNameWithoutExtension(PackagePath));
        await PackageExtractor.ExtractAsync(PackagePath, dest);
        var pkg = ReleasePackage.Inspect(dest);
        return pkg.SdCardRoot ?? dest;
    }

    private void ShowPlan()
    {
        Items.Clear();
        Warnings.Clear();
        if (_plan is null) return;
        foreach (var i in _plan.Items.Where(i => !HideUnchanged || i.Action != SyncAction.Unchanged)) Items.Add(i);
        foreach (var w in _plan.Warnings) Warnings.Add(w);
        var n = _plan.Items.GroupBy(i => i.Action).ToDictionary(g => g.Key, g => g.Count());
        Summary = $"{n.GetValueOrDefault(SyncAction.Add)} to add · {n.GetValueOrDefault(SyncAction.Replace)} to replace · " +
                  $"{n.GetValueOrDefault(SyncAction.Unchanged)} unchanged · {n.GetValueOrDefault(SyncAction.TargetOnly)} only on card (left alone)";
        CanApply = _plan.Changes.Any();
    }

    [RelayCommand]
    private Task ApplyAsync() => GuardAsync(async () =>
    {
        if (_plan is null) return;
        var approved = ChangeApprovalWindow.Ask("Approve SD-card changes", _plan);
        if (approved is null || approved.Count == 0) { Status = "Nothing applied."; return; }
        var progress = new Progress<string>(s => Status = s);
        var result = await Task.Run(() => new SdSyncExecutor(services.Contiguity).ApplyAsync(_plan, approved, services.Paths.Backups, progress));
        var failed = result.Results.Where(r => r.Outcome == ItemOutcome.Failed).ToList();
        var notes = result.Results.Where(r => r.Message?.StartsWith("WARNING") == true).Select(r => $"{r.Item.RelativePath}: {r.Message}");
        Status = failed.Count == 0
            ? $"Applied {result.Results.Count(r => r.Outcome == ItemOutcome.Written)} change(s)." + (result.BackupDirectory is null ? "" : $" Backups: {result.BackupDirectory}")
            : $"{failed.Count} change(s) failed: {string.Join("; ", failed.Select(f => $"{f.Item.RelativePath} – {f.Message}"))}";
        foreach (var n in notes) Warnings.Add(n);
        await CompareAsync();
    }, "Applying…");
}
