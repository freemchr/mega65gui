using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
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

public sealed record LinkedFile(string FileId, string Label, bool IsPublic);

public sealed partial class CatalogueViewModel : PageViewModel
{
    private readonly AppServices services;
    private bool _loaded;

    public CatalogueViewModel(AppServices services) : base("Filehost", "\uE774")
    {
        this.services = services;
        services.Session.Changed += () => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateAccount();
            // Owner-only files become visible (or hidden) when the account changes.
            if (ViewingFileId is { } id && _loaded) _ = LoadDetailAsync(id);
        });
        UpdateAccount();
    }

    public ObservableCollection<LinkedFile> LinkedFiles { get; } = [];

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _accountText = "";
    [ObservableProperty] private string? _viewingFileId;

    private void UpdateAccount()
    {
        IsSignedIn = services.Session.IsSignedIn;
        AccountText = IsSignedIn ? $"Signed in as {services.Session.UserName}" : "Not signed in";
    }

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
    partial void OnSelectedFileChanged(FilehostFile? value)
    {
        if (value is not null && !_restoringSelection) _ = LoadDetailAsync(value.FileId);
    }

    /// <summary>How old the list may be before opening the page fetches it again.</summary>
    private static readonly TimeSpan AutoRefreshAfter = TimeSpan.FromMinutes(5);

    public override async Task OnShownAsync()
    {
        // Show the cached list at once, then fetch the latest in the background if it is stale.
        if (!_loaded) await RefreshAsync(false);
        var fetched = services.Catalogue.Current?.FetchedAt ?? DateTimeOffset.MinValue;
        if (DateTimeOffset.Now - fetched > AutoRefreshAfter) await RefreshAsync(true);
    }

    [RelayCommand]
    private Task RefreshAsync(bool force) => GuardAsync(async () =>
    {
        var snap = await services.Catalogue.LoadAsync(force);
        _loaded = true;
        var keep = Category;
        Categories.Clear();
        Categories.Add("All");
        foreach (var c in services.Catalogue.Categories) Categories.Add(c);
        // Clearing the list deselects the ComboBox, so restore the selection afterwards.
        Category = keep is not null && Categories.Contains(keep) ? keep : "All";
        OnPropertyChanged(nameof(Category));
        CacheInfo = snap.StaleReason is not null
            ? $"Offline – showing the copy from {snap.FetchedAt.LocalDateTime:g} ({snap.StaleReason})"
            : $"{snap.Files.Count} files · updated {snap.FetchedAt.LocalDateTime:t}";
        ApplyFilter();
        Status = "";
    }, force ? "Checking the Filehost for new files…" : "Loading catalogue…");

    private bool _restoringSelection;

    private void ApplyFilter()
    {
        var selectedId = SelectedFile?.FileId;
        Results.Clear();
        var q = new CatalogueQuery(SearchText, Category == "All" ? null : Category, null, OfficialOnly);
        foreach (var f in services.Catalogue.Search(q)) Results.Add(f);

        // Keep the highlighted row after a refresh without reloading its details.
        if (selectedId is not null && Results.FirstOrDefault(f => f.FileId == selectedId) is { } again)
        {
            _restoringSelection = true;
            SelectedFile = again;
            _restoringSelection = false;
        }
    }

    [RelayCommand]
    private Task OpenLinkedAsync(LinkedFile? link) => link is null ? Task.CompletedTask : LoadDetailAsync(link.FileId);

    private async Task LoadDetailAsync(string fileId)
    {
        ViewingFileId = fileId;
        Detail = null;
        Versions.Clear();
        LinkedFiles.Clear();
        await GuardAsync(async () =>
        {
            try
            {
                Detail = await services.Catalogue.GetDetailAsync(fileId);
            }
            catch (UnauthorizedAccessException e)
            {
                Status = e.Message;
                return;
            }
            foreach (var id in FilehostLinks.Extract(Detail?.Description).Where(id => id != fileId))
            {
                var pub = services.Catalogue.Current?.Files.FirstOrDefault(f => f.FileId == id);
                LinkedFiles.Add(new LinkedFile(id, pub?.Title ?? "Owner-only file (sign in to check access)", pub is not null));
            }
            if (Detail is not { IsAvailable: true })
            {
                Status = services.Session.IsSignedIn
                    ? "Your Filehost account does not have access to this file. Owner-only files need the ownership code redeemed on the Filehost website."
                    : "This file is not public. Sign in with your Filehost account to check access.";
                return;
            }
            foreach (var v in await services.Catalogue.GetVersionsAsync(fileId))
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
        if ((ViewingFileId ?? SelectedFile?.FileId) is { } id)
            Process.Start(new ProcessStartInfo(FilehostClient.WebPageFor(id).ToString()) { UseShellExecute = true });
    }
}
