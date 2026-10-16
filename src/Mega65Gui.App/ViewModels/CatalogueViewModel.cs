using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.App.ViewModels;

public sealed partial class VersionRow(FilehostVersionRow row) : ObservableObject
{
    public FilehostVersionRow Row { get; } = row;
    public string Release => ReleaseInfo.TryParse(Row.FileName, Row.Comment)?.Release.ToString() ?? "";
    [ObservableProperty] private string? _cachedPath;
}

public sealed partial class CatalogueViewModel(AppServices services) : PageViewModel("Filehost", "")
{
    private bool _loaded;

    public ObservableCollection<FilehostFile> Results { get; } = [];
    public ObservableCollection<string> Categories { get; } = [];
    public ObservableCollection<VersionRow> Versions { get; } = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _category = "All";
    [ObservableProperty] private bool _officialOnly;
    [ObservableProperty] private FilehostFile? _selectedFile;
    [ObservableProperty] private FilehostFileDetail? _detail;
    [ObservableProperty] private VersionRow? _selectedVersion;
    [ObservableProperty] private string _cacheInfo = "";
    [ObservableProperty] private double _downloadPercent;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnCategoryChanged(string value) => ApplyFilter();
    partial void OnOfficialOnlyChanged(bool value) => ApplyFilter();
    partial void OnSelectedFileChanged(FilehostFile? value) => _ = LoadDetailAsync(value);

    public override async Task OnShownAsync()
    {
        if (!_loaded) await RefreshAsync(false);
    }

    [RelayCommand]
    private Task RefreshAsync(bool force) => GuardAsync(async () =>
    {
        var snap = await services.Catalogue.LoadAsync(force);
        _loaded = true;
        Categories.Clear();
        Categories.Add("All");
        foreach (var c in services.Catalogue.Categories) Categories.Add(c);
        CacheInfo = snap.StaleReason is not null
            ? $"Offline – showing the copy from {snap.FetchedAt.LocalDateTime:g} ({snap.StaleReason})"
            : $"{snap.Files.Count} files · {(snap.FromCache ? "cached" : "fetched")} {snap.FetchedAt.LocalDateTime:g}";
        ApplyFilter();
        Status = "";
    }, "Loading catalogue…");

    private void ApplyFilter()
    {
        Results.Clear();
        var q = new CatalogueQuery(SearchText, Category == "All" ? null : Category, null, OfficialOnly);
        foreach (var f in services.Catalogue.Search(q)) Results.Add(f);
    }

    private async Task LoadDetailAsync(FilehostFile? file)
    {
        Detail = null;
        Versions.Clear();
        if (file is null) return;
        await GuardAsync(async () =>
        {
            Detail = await services.Catalogue.GetDetailAsync(file.FileId);
            foreach (var v in await services.Catalogue.GetVersionsAsync(file.FileId))
            {
                var row = new VersionRow(v);
                if (services.Downloads.TryGetCached(v.VersionId) is { } m) row.CachedPath = services.Downloads.PathOf(m);
                Versions.Add(row);
            }
            SelectedVersion = Versions.FirstOrDefault(v => v.Row.IsActive) ?? Versions.FirstOrDefault();
            Status = "";
        }, "Loading details…");
    }

    [RelayCommand]
    private Task DownloadAsync() => GuardAsync(async () =>
    {
        if (SelectedVersion is not { } v) return;
        var progress = new Progress<DownloadProgress>(p => DownloadPercent = p.Total is > 0 ? 100.0 * p.Received / p.Total.Value : 0);
        var m = await services.Downloads.DownloadAsync(v.Row, progress);
        v.CachedPath = services.Downloads.PathOf(m);
        Status = $"Downloaded {m.FileName} ({m.Size:N0} bytes), SHA-256 {m.Sha256}";
    }, "Downloading…");

    [RelayCommand]
    private void ShowInFolder()
    {
        if (SelectedVersion?.CachedPath is { } p && File.Exists(p))
            Process.Start("explorer.exe", $"/select,\"{p}\"");
    }

    [RelayCommand]
    private void OpenOnWebsite()
    {
        if (SelectedFile is { } f)
            Process.Start(new ProcessStartInfo(FilehostClient.WebPageFor(f.FileId).ToString()) { UseShellExecute = true });
    }
}
