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
    private bool _triedAutoSignIn;

    public CatalogueViewModel(AppServices services) : base("Filehost", "\uE774")
    {
        this.services = services;
        services.Session.Changed += () => System.Windows.Application.Current.Dispatcher.Invoke(UpdateAccount);
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

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (Views.SignInWindow.Show(services) && ViewingFileId is { } id) await LoadDetailAsync(id);
    }

    [RelayCommand]
    private Task SignOutAsync() => GuardAsync(async () =>
    {
        await services.Session.SignOutAsync();
        if (services.Credentials.Load() is { } saved) services.Credentials.Save(saved.UserName, null);
        if (ViewingFileId is { } id && !services.Catalogue.IsPublic(id)) { Detail = null; Versions.Clear(); }
        Status = "Signed out. Any saved password has been removed.";
    });

    /// <summary>Signs in silently with a password the user chose to remember.</summary>
    private async Task TryAutoSignInAsync()
    {
        if (_triedAutoSignIn || services.Session.IsSignedIn) return;
        _triedAutoSignIn = true;
        if (services.Credentials.Load() is not { Password: { } password } saved) return;
        try
        {
            var r = await services.Session.SignInAsync(saved.UserName, password);
            if (r.Result == SignInResult.InvalidCredentials)
                Status = "The saved Filehost password was rejected. Please sign in again.";
        }
        catch (HttpRequestException) { /* offline: stay signed out */ }
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
        if (value is not null) _ = LoadDetailAsync(value.FileId);
    }

    public override async Task OnShownAsync()
    {
        if (!_loaded) await RefreshAsync(false);
        await TryAutoSignInAsync();
    }

    [RelayCommand]
    private Task RefreshAsync(bool force) => GuardAsync(async () =>
    {
        var snap = await services.Catalogue.LoadAsync(force);
        _loaded = true;
        Categories.Clear();
        Categories.Add("All");
        foreach (var c in services.Catalogue.Categories) Categories.Add(c);
        OnPropertyChanged(nameof(Category)); // re-select after the list was rebuilt
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
