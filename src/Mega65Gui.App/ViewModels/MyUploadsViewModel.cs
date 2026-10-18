using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Update;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

/// <summary>What the user chose to do on the page. Nothing is pre-selected.</summary>
public enum UploadTask { None, NewFile, NewVersion, EditDetails }

public sealed record AccessOption(UploadAccess Value, string Label, string Hint);

/// <summary>
/// "My File Uploads" from the Filehost website: upload a new file, upload a new version of one of
/// yours, edit its attributes, and optionally link it to GitHub so new releases are uploaded for you.
/// Every write to the Filehost is confirmed first.
/// </summary>
public sealed partial class MyUploadsViewModel : PageViewModel
{
    private readonly AppServices services;
    private readonly IApprovalGate gate;
    private UploadAttributes _loaded = new();
    /// <summary>The release a "Fill from GitHub" downloaded, recorded on the link once it is uploaded.</summary>
    private GitHubRelease? _filledRelease;
    private bool _listsLoaded;

    public MyUploadsViewModel(AppServices services, IApprovalGate gate) : base("MEGA65 File Host Upload", "")
    {
        this.services = services;
        this.gate = gate;
        services.Session.Changed += () => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateAccount();
            if (IsSignedIn) _ = RefreshAsync();
            else { Files.Clear(); Versions.Clear(); SelectedFile = null; CurrentTask = UploadTask.None; }
        });
        _checkAtStartup = services.UploadLinks.CheckAtStartup;
        UpdateAccount();
    }

    public ObservableCollection<MyUpload> Files { get; } = [];
    public ObservableCollection<MyVersionRow> Versions { get; } = [];
    public ObservableCollection<PreviewMedia> Previews { get; } = [];
    public bool CanAddPreview => Previews.Count < FilehostUploads.MaxPreviews;
    public string PreviewInfo => $"{Previews.Count} of {FilehostUploads.MaxPreviews} previews. Pictures (jpg, png, gif), video (mp4) or audio (mp3, ogg), up to 200 MB each, or a link to a video.";
    [ObservableProperty] private string _videoLink = "";
    public ObservableCollection<UploadFileType> FileTypes { get; } = [];
    public ObservableCollection<RequirementVersion> CoreVersions { get; } = [];
    public ObservableCollection<RequirementVersion> RomVersions { get; } = [];
    /// <summary>Search tags suggested from GitHub that are not in the search tags yet.</summary>
    public ObservableCollection<string> SuggestedTags { get; } = [];
    private IReadOnlyList<string> _tagSuggestions = [];
    public bool HasSuggestions => SuggestedTags.Count > 0;

    public IReadOnlyList<AccessOption> AccessOptions { get; } =
    [
        new(UploadAccess.Public, "Public", "Everyone can download this file"),
        new(UploadAccess.DevKit, "DevKit", "You, DevKit, MegaDev and MEGA65 owners can download this file"),
        new(UploadAccess.Admin, "Admin", "Access to you and Filehost admins only"),
        new(UploadAccess.Owner, "Owner", "Only you can see this file"),
    ];

    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private string _accountText = "";
    [ObservableProperty] private MyUpload? _selectedFile;
    [ObservableProperty] private UploadTask _currentTask;
    [ObservableProperty] private bool _hasFile;

    // Which parts of the page the current task shows.
    public bool IsChoosing => CurrentTask == UploadTask.None;
    public bool IsNewFile => CurrentTask == UploadTask.NewFile;
    public bool IsNewVersion => CurrentTask == UploadTask.NewVersion;
    public bool IsEditDetails => CurrentTask == UploadTask.EditDetails;
    public bool ShowFileList => IsNewVersion || IsEditDetails;
    public bool NeedsFileChoice => ShowFileList && !HasFile;
    public bool ShowUpload => IsNewFile || (IsNewVersion && HasFile);
    public bool ShowAttributes => IsNewFile || (IsEditDetails && HasFile);
    public bool ShowFileInfo => ShowFileList && HasFile;

    public string TaskHeading => CurrentTask switch
    {
        UploadTask.NewFile => "Upload a new file",
        UploadTask.NewVersion when SelectedFile is { } f && HasFile => $"New version of \u201c{f.DisplayTitle}\u201d",
        UploadTask.NewVersion => "New version: choose the file on the left",
        UploadTask.EditDetails when SelectedFile is { } f && HasFile => $"Edit details of \u201c{f.DisplayTitle}\u201d",
        UploadTask.EditDetails => "Edit details: choose the file on the left",
        _ => "What do you want to do?",
    };

    partial void OnCurrentTaskChanged(UploadTask value) => TaskFlagsChanged();
    partial void OnHasFileChanged(bool value) => TaskFlagsChanged();

    private void TaskFlagsChanged()
    {
        foreach (var name in new[] { nameof(IsChoosing), nameof(IsNewFile), nameof(IsNewVersion), nameof(IsEditDetails), nameof(ShowFileList),
                     nameof(NeedsFileChoice), nameof(ShowUpload), nameof(ShowAttributes), nameof(ShowFileInfo), nameof(TaskHeading) })
            OnPropertyChanged(name);
    }
    [ObservableProperty] private bool _isLocked;

    // ---- File attributes, as on the website's "Set file attributes"
    [ObservableProperty] private string _fileTitle = "";
    [ObservableProperty] private UploadFileType? _selectedType;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _searchTags = "";
    [ObservableProperty] private string _shortLink = "";
    [ObservableProperty] private bool _pal = true;
    [ObservableProperty] private bool _ntsc = true;
    [ObservableProperty] private bool _osMega65 = true;
    [ObservableProperty] private bool _osC65;
    [ObservableProperty] private bool _osC64;
    [ObservableProperty] private bool _osVixen;
    [ObservableProperty] private bool _osWindows;
    [ObservableProperty] private bool _osMac;
    [ObservableProperty] private bool _osLinux;
    [ObservableProperty] private bool _osOnline;
    [ObservableProperty] private AccessOption? _access;
    [ObservableProperty] private RequirementVersion? _coreVersion;
    [ObservableProperty] private RequirementVersion? _romVersion;
    [ObservableProperty] private bool _introDisk;
    [ObservableProperty] private bool _highScore;
    [ObservableProperty] private bool _isGame;
    [ObservableProperty] private bool _showRomVersion;

    // ---- Upload
    [ObservableProperty] private string? _uploadPath;
    [ObservableProperty] private string _uploadInfo = "No file chosen.";
    [ObservableProperty] private string _versionNumber = "1";
    [ObservableProperty] private string _versionComment = "";
    [ObservableProperty] private bool _replaceLatest;
    [ObservableProperty] private double _uploadPercent;

    // ---- GitHub link
    [ObservableProperty] private string _gitHubRepo = "";
    [ObservableProperty] private string _assetPattern = "";
    [ObservableProperty] private bool _includePrereleases;
    [ObservableProperty] private string _linkInfo = "";
    [ObservableProperty] private bool _checkAtStartup;

    partial void OnSelectedFileChanged(MyUpload? value)
    {
        HasFile = false;
        // A file only matters for the tasks that work on one of your files.
        if (value is null || !ShowFileList) return;
        _ = LoadFileAsync(value.FileId);
    }

    partial void OnSearchTagsChanged(string value) => ShowSuggestions();

    private void ShowSuggestions()
    {
        var have = CurrentTags();
        SuggestedTags.Clear();
        foreach (var t in _tagSuggestions.Where(t => !have.Contains(t))) SuggestedTags.Add(t);
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private HashSet<string> CurrentTags() => new(
        SearchTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    private void SetSuggestions(IReadOnlyList<string> tags)
    {
        _tagSuggestions = tags;
        ShowSuggestions();
    }

    [RelayCommand]
    private void AddTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || CurrentTags().Contains(tag)) return;
        var joined = GitHubReleases.JoinTags(SearchTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(tag));
        if (!joined.EndsWith(tag, StringComparison.OrdinalIgnoreCase)) { Status = "The search tags are limited to 200 characters."; return; }
        SearchTags = joined;
    }

    [RelayCommand]
    private void AddAllTags()
    {
        foreach (var t in SuggestedTags.ToList()) AddTag(t);
    }

    /// <summary>Suggests search tags from the GitHub repository in the GitHub box, without downloading anything.</summary>
    [RelayCommand]
    private Task SuggestTagsAsync() => GuardAsync(async () =>
    {
        if (GitHubReleases.ParseRepo(GitHubRepo) is not { } repoName) { Status = "Enter the GitHub repository first (in the GitHub box)."; return; }
        var repo = await services.GitHub.GetRepositoryAsync(repoName);
        var release = await services.GitHub.GetLatestAsync(repoName, IncludePrereleases);
        SetSuggestions(GitHubReleases.SuggestTags(repo, release, FileTitle, null));
        Status = SuggestedTags.Count == 0 ? "No more tags to suggest." : "Click a suggested tag to add it, or Add all.";
    }, "Reading GitHub…");

    partial void OnSelectedTypeChanged(UploadFileType? value)
    {
        // The website only offers the high-score list for games.
        IsGame = value?.Category == "Game";
        if (!IsGame) HighScore = false;
    }

    partial void OnCoreVersionChanged(RequirementVersion? value)
    {
        // ...and a ROM version only for MEGA65 cores.
        ShowRomVersion = value?.IsMega65Core == true;
        if (!ShowRomVersion) RomVersion = RomVersions.FirstOrDefault();
    }

    partial void OnCheckAtStartupChanged(bool value)
    {
        services.UploadLinks.CheckAtStartup = value;
        services.UploadLinks.Save();
    }

    private void UpdateAccount()
    {
        IsSignedIn = services.Session.IsSignedIn;
        AccountText = IsSignedIn ? $"Signed in as {services.Session.UserName}" : "Sign in with your Filehost account to see and upload your files.";
    }

    public override Task OnShownAsync() => IsSignedIn && Files.Count == 0 ? RefreshAsync() : Task.CompletedTask;

    [RelayCommand]
    private void SignIn() => Views.SignInWindow.Show(services);

    private Task? _refresh;

    /// <summary>
    /// Signing in and opening the page can both ask for the list at once; they share one load so
    /// the list is not filled twice.
    /// </summary>
    [RelayCommand]
    private Task RefreshAsync() => _refresh is { IsCompleted: false } running ? running : _refresh = GuardAsync(async () =>
    {
        if (!services.Session.IsSignedIn) { Status = "Not signed in."; return; }
        await LoadListsAsync();
        var keep = SelectedFile?.FileId;
        var mine = (await services.Uploads.ListMyFilesAsync()).DistinctBy(f => f.FileId)
            .OrderBy(f => f.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
        Files.Clear();
        foreach (var f in mine) Files.Add(f);
        // Keep the file the user picked, but never pick one for them.
        var again = Files.FirstOrDefault(f => f.FileId == keep);
        if (again is not null && again != SelectedFile) SelectedFile = again;
        else if (again is null) SelectedFile = null;
        Status = Files.Count == 1 ? "You have 1 file on the Filehost." : $"You have {Files.Count} files on the Filehost.";
    }, "Loading your uploads…");

    private async Task LoadListsAsync()
    {
        if (_listsLoaded) return;
        FileTypes.Clear();
        foreach (var t in (await services.Uploads.GetFileTypesAsync()).OrderBy(t => t.Category).ThenBy(t => t.Name)) FileTypes.Add(t);
        CoreVersions.Clear();
        CoreVersions.Add(RequirementVersion.NotSelected);
        foreach (var c in await services.Uploads.GetCoreVersionsAsync()) CoreVersions.Add(c);
        RomVersions.Clear();
        RomVersions.Add(RequirementVersion.NotSelected);
        foreach (var r in await services.Uploads.GetRomVersionsAsync()) RomVersions.Add(r);
        _listsLoaded = true;
    }

    private Task LoadFileAsync(string fileId) => GuardAsync(async () =>
    {
        HasFile = false;
        Versions.Clear();
        ClearUpload();
        var attributes = await services.Uploads.GetAttributesAsync(fileId);
        if (SelectedFile?.FileId != fileId) return;
        Show(attributes);
        await LoadVersionsAsync(fileId);
        await LoadPreviewsAsync(fileId);
        ShowLink(fileId);
        HasFile = true;
        Status = IsLocked ? "This file is locked by the Filehost admins and not visible in public. Contact them via Messages on the website." : "";
    }, "Loading file…");

    private async Task LoadVersionsAsync(string fileId)
    {
        Versions.Clear();
        foreach (var v in await services.Uploads.GetVersionsAsync(fileId)) Versions.Add(v);
        VersionNumber = FilehostUploads.FormatVersion(FilehostUploads.SuggestNextVersion(Versions));
    }

    private async Task LoadPreviewsAsync(string fileId)
    {
        Previews.Clear();
        foreach (var m in await services.Uploads.GetPreviewsAsync(fileId)) Previews.Add(m);
        OnPropertyChanged(nameof(CanAddPreview));
        OnPropertyChanged(nameof(PreviewInfo));
    }

    [RelayCommand]
    private Task AddPreviewsAsync() => GuardAsync(async () =>
    {
        if (SelectedFile is not { } file || !ShowFileInfo) return;
        var room = FilehostUploads.MaxPreviews - Previews.Count;
        if (room <= 0) { Status = "This file already has 5 previews. Delete one to add another."; return; }
        var dlg = new OpenFileDialog
        {
            Title = "Pictures, video or audio to show on the file's page", Multiselect = true,
            Filter = "Previews|*.png;*.jpg;*.jpeg;*.gif;*.mp4;*.mp3;*.ogg;*.mpeg|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        var files = dlg.FileNames.ToList();
        if (files.Count > room) { Status = $"Only {room} more preview{(room == 1 ? "" : "s")} fit (5 at most). Choose fewer files."; return; }
        if (!await gate.ConfirmAsync("Add previews",
                $"Add {(files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} files")} as preview{(files.Count == 1 ? "" : "s")} of \u201c{file.DisplayTitle}\u201d on the Filehost?\n\n{string.Join("\n", files.Select(Path.GetFileName))}", default))
        { Status = "Cancelled."; return; }
        foreach (var f in files)
        {
            Status = $"Uploading {Path.GetFileName(f)}…";
            await services.Uploads.UploadPreviewAsync(file.FileId, f, new Progress<double>(p => UploadPercent = 100 * p));
        }
        UploadPercent = 0;
        await LoadPreviewsAsync(file.FileId);
        Status = files.Count == 1 ? "Preview added." : $"{files.Count} previews added.";
    }, "Uploading previews…");

    [RelayCommand]
    private Task AddVideoLinkAsync() => GuardAsync(async () =>
    {
        if (SelectedFile is not { } file || !ShowFileInfo || string.IsNullOrWhiteSpace(VideoLink)) return;
        if (!CanAddPreview) { Status = "This file already has 5 previews. Delete one to add another."; return; }
        if (!await gate.ConfirmAsync("Add a video link", $"Add {VideoLink.Trim()} as a preview of \u201c{file.DisplayTitle}\u201d?", default))
        { Status = "Cancelled."; return; }
        Status = await services.Uploads.AddPreviewLinkAsync(file.FileId, VideoLink);
        VideoLink = "";
        await LoadPreviewsAsync(file.FileId);
    }, "Adding the link…");

    [RelayCommand]
    private Task DeletePreviewAsync(PreviewMedia? media) => GuardAsync(async () =>
    {
        if (media is null || SelectedFile is not { } file) return;
        if (!await gate.ConfirmAsync("Delete preview", $"Delete this {media.Kind.ToLowerInvariant()} preview from \u201c{file.DisplayTitle}\u201d on the Filehost? This cannot be undone.", default))
        { Status = "Cancelled."; return; }
        Status = await services.Uploads.DeletePreviewAsync(media.MediaId);
        await LoadPreviewsAsync(file.FileId);
    }, "Deleting…");

    [RelayCommand]
    private void OpenPreview(PreviewMedia? media)
    {
        if (media?.LocationUri is { } uri) Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private void Show(UploadAttributes a)
    {
        _loaded = a;
        FileTitle = a.Title;
        SelectedType = FileTypes.FirstOrDefault(t => t.Id == a.TypeId);
        Description = a.Description;
        SearchTags = a.SearchTags;
        ShortLink = a.ShortLink;
        Pal = a.Pal; Ntsc = a.Ntsc;
        OsMega65 = a.OsMega65; OsC65 = a.OsC65; OsC64 = a.OsC64; OsVixen = a.OsVixen;
        OsWindows = a.OsWindows; OsMac = a.OsMac; OsLinux = a.OsLinux; OsOnline = a.OsOnline;
        Access = AccessOptions.First(o => o.Value == a.Access);
        CoreVersion = CoreVersions.FirstOrDefault(c => c.VersionId == a.CoreVersionId) ?? CoreVersions.FirstOrDefault();
        RomVersion = RomVersions.FirstOrDefault(r => r.VersionId == a.RomVersionId) ?? RomVersions.FirstOrDefault();
        IntroDisk = a.IntroDisk;
        HighScore = a.HighScore;
        IsLocked = a.Locked;
    }

    private UploadAttributes Collect() => _loaded with
    {
        Title = FileTitle,
        TypeId = SelectedType?.Id ?? "-1",
        Description = Description,
        SearchTags = SearchTags,
        ShortLink = ShortLink,
        Pal = Pal, Ntsc = Ntsc,
        OsMega65 = OsMega65, OsC65 = OsC65, OsC64 = OsC64, OsVixen = OsVixen,
        OsWindows = OsWindows, OsMac = OsMac, OsLinux = OsLinux, OsOnline = OsOnline,
        Access = Access?.Value ?? UploadAccess.Public,
        CoreVersionId = CoreVersion?.VersionId ?? "-1",
        RomVersionId = ShowRomVersion ? RomVersion?.VersionId ?? "-1" : "-1",
        IntroDisk = IntroDisk,
        HighScore = IsGame && HighScore,
    };

    [RelayCommand]
    private void StartNewVersion() => StartFileTask(UploadTask.NewVersion,
        "Choose, on the left, the file you want to add a version to.");

    [RelayCommand]
    private void StartEditDetails() => StartFileTask(UploadTask.EditDetails,
        "Choose, on the left, the file whose details you want to change.");

    private void StartFileTask(UploadTask task, string hint)
    {
        SelectedFile = null;
        Versions.Clear();
        Previews.Clear();
        ClearUpload();
        GitHubRepo = AssetPattern = LinkInfo = "";
        CurrentTask = task;
        Status = Files.Count == 0 ? "You have no files on the Filehost yet. Use Upload a new file." : hint;
    }

    [RelayCommand]
    private void ChangeTask()
    {
        SelectedFile = null;
        Versions.Clear();
        ClearUpload();
        CurrentTask = UploadTask.None;
        Status = "";
    }

    [RelayCommand]
    private void NewFile()
    {
        SelectedFile = null;
        CurrentTask = UploadTask.NewFile;
        Versions.Clear();
        ClearUpload();
        Show(new UploadAttributes());
        GitHubRepo = AssetPattern = LinkInfo = "";
        Status = "Choose the file to upload, fill in its attributes, then press Upload new file.";
    }

    private void ClearUpload()
    {
        SetSuggestions([]);
        _filledRelease = null;
        UploadPath = null;
        UploadInfo = "No file chosen.";
        VersionComment = "";
        ReplaceLatest = false;
        UploadPercent = 0;
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var dlg = new OpenFileDialog { Title = IsNewFile ? "File to upload" : "New version to upload" };
        if (dlg.ShowDialog() != true) return;
        if (FilehostUploads.CheckFile(dlg.FileName) is { } problem) { Status = problem; return; }
        UploadPath = dlg.FileName;
        var info = new FileInfo(dlg.FileName);
        UploadInfo = $"{info.Name} · {info.Length:N0} bytes";
        Status = "";
        if (!IsNewFile) return;

        // As the website does for a new file: title from the file name, platform and type from the extension.
        var ext = info.Extension.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(FileTitle)) FileTitle = Path.GetFileNameWithoutExtension(info.Name);
        var pcFile = ext is ".doc" or ".docx" or ".xls" or ".xlsx" or ".txt" or ".zip" or ".rar" or ".tar" or ".7z" or ".dmg" or ".pdf" or ".iso";
        OsMega65 = !pcFile; OsWindows = OsMac = OsLinux = pcFile;
        OsC65 = OsC64 = OsVixen = OsOnline = false;
        if (ext.Length > 0 && FileTypes.FirstOrDefault(t => t.Extension?.Contains(ext, StringComparison.OrdinalIgnoreCase) == true) is { } type)
            SelectedType = type;
    }

    [RelayCommand]
    private Task UploadNewFileAsync() => GuardAsync(async () =>
    {
        if (!IsNewFile) return;
        if (UploadPath is not { } path) { Status = "Choose a file first."; return; }
        var a = Collect();
        if (a.Problems() is { Count: > 0 } problems) { Status = string.Join(" ", problems); return; }
        if (!await gate.ConfirmAsync("Upload to the Filehost",
                $"Upload {Path.GetFileName(path)} to files.mega65.org as a new file?\n\nTitle: {a.Title}\nType: {SelectedType?.Label}\nAccess: {Access?.Label} ({Access?.Hint})", default))
        { Status = "Cancelled."; return; }

        var id = await services.Uploads.UploadNewFileAsync(path, a, UploadProgress());
        RememberLink(id);
        var uploaded = $"Uploaded {Path.GetFileName(path)} as a new file.";
        CurrentTask = UploadTask.EditDetails;
        SelectedFile = null;
        await RefreshAsync();
        SelectedFile = Files.FirstOrDefault(f => f.FileId == id);
        Status = uploaded;
    }, "Uploading…");

    [RelayCommand]
    private Task UploadVersionAsync() => GuardAsync(async () =>
    {
        if (!IsNewVersion || SelectedFile is not { } file || !HasFile) return;
        if (UploadPath is not { } path) { Status = "Choose the new version's file first."; return; }
        if (!decimal.TryParse(VersionNumber.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var version) || !FilehostUploads.IsValidVersion(version))
        { Status = "The version must be a number from 1 to 9999.9 with at most one decimal place, such as 1.2."; return; }
        var a = Collect();
        if (a.Problems() is { Count: > 0 } problems) { Status = string.Join(" ", problems); return; }
        var mode = ReplaceLatest ? VersionMode.Replace : VersionMode.New;
        var what = mode == VersionMode.Replace
            ? $"This REPLACES the latest version ({Versions.FirstOrDefault()?.Version} · {Versions.FirstOrDefault()?.FileName}), which is deleted."
            : "It is added as a new version and becomes the active one.";
        if (!await gate.ConfirmAsync("Upload a new version",
                $"Upload {Path.GetFileName(path)} as version {FilehostUploads.FormatVersion(version)} of \"{file.DisplayTitle}\"?\n\n{what}\nThe attributes shown on this page are saved with it.", default))
        { Status = "Cancelled."; return; }

        await services.Uploads.UploadNewVersionAsync(file.FileId, path, a, version, VersionComment, mode, UploadProgress());
        RememberLink(file.FileId);
        ShowLink(file.FileId);
        Status = $"Uploaded version {FilehostUploads.FormatVersion(version)}.";
        ClearUpload();
        _loaded = a;
        await LoadVersionsAsync(file.FileId);
    }, "Uploading…");

    [RelayCommand]
    private Task SaveDetailsAsync() => GuardAsync(async () =>
    {
        if (!IsEditDetails || SelectedFile is not { } file || !HasFile) return;
        var a = Collect();
        if (a.Problems() is { Count: > 0 } problems) { Status = string.Join(" ", problems); return; }
        if (!await gate.ConfirmAsync("Save file attributes", $"Save the changed attributes of \"{file.DisplayTitle}\" on the Filehost?", default))
        { Status = "Cancelled."; return; }
        Status = await services.Uploads.SaveAttributesAsync(file.FileId, a);
        _loaded = a;
        if (a.Title != file.DisplayTitle) await RefreshAsync();
    }, "Saving…");

    private Progress<double> UploadProgress() => new(p =>
    {
        UploadPercent = 100 * p;
        Status = p < 1 ? $"Uploading… {p:P0}" : "Waiting for the Filehost to store the file…";
    });

    [RelayCommand]
    private void OpenOnWebsite()
    {
        var url = SelectedFile is { } f ? FilehostClient.WebPageFor(f.FileId).ToString() : FilehostClient.BaseUri.ToString();
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ---------------- Fill from GitHub

    /// <summary>
    /// Reads the repository and its newest release and fills the page: the release file (downloaded,
    /// ready to upload), version and comment, and for a new file also title, description, search
    /// tags and type. For an existing file, attributes that are already filled in are kept.
    /// </summary>
    [RelayCommand]
    private Task FillFromGitHubAsync() => GuardAsync(async () =>
    {
        if (!ShowUpload) return;
        if (GitHubReleases.ParseRepo(GitHubRepo) is not { } repoName) { Status = "Enter a GitHub repository, such as https://github.com/owner/repo."; return; }
        if (IsNewVersion && SelectedFile is { } target)
        {
            // Guard against filling one project's release into another project's file.
            var link = services.UploadLinks.For(target.FileId);
            if (link is not null && !string.Equals(link.Repo, repoName, StringComparison.OrdinalIgnoreCase))
            { Status = $"\u201c{target.DisplayTitle}\u201d is linked to {link.Repo}, not {repoName}. Choose the right file, or remove its link first."; return; }
            if (link is null && !await gate.ConfirmAsync("Use this GitHub repository?",
                    $"Use the releases of {repoName} as new versions of \u201c{target.DisplayTitle}\u201d?\n\nIf {repoName} is a different project, press No and choose Upload a new file instead.", default))
            { Status = "Cancelled."; return; }
        }
        var repo = await services.GitHub.GetRepositoryAsync(repoName);
        var release = await services.GitHub.GetLatestAsync(repoName, IncludePrereleases);
        if (release is null) { Status = $"{repoName} has no published release{(IncludePrereleases ? "" : " (tick Include pre-releases to use one)")}."; return; }
        var asset = GitHubReleases.PickAsset(release, AssetPattern);
        if (asset is null)
        {
            Status = string.IsNullOrWhiteSpace(AssetPattern)
                ? $"Release {release.Tag} has no file the Filehost accepts ({string.Join(", ", release.Assets.Select(a => a.Name))})."
                : $"Release {release.Tag} has no file matching {AssetPattern}. Its files: {string.Join(", ", release.Assets.Select(a => a.Name))}.";
            return;
        }
        if (string.IsNullOrWhiteSpace(AssetPattern)) AssetPattern = GitHubReleases.SuggestPattern(asset, release.Tag);

        // Duplicates are checked before downloading anything.
        if (!IsNewFile && Versions.Any(v => string.Equals(v.FileName, asset.Name, StringComparison.OrdinalIgnoreCase)))
        { Status = $"{asset.Name} is already on the Filehost as a version of this file."; return; }

        var folder = Directory.CreateDirectory(Path.Combine(services.Paths.Downloads, "github")).FullName;
        var path = Path.Combine(folder, asset.Name);
        Status = $"Downloading {asset.Name} from GitHub…";
        await GitHubReleases.DownloadAsync(services.GitHubHttp, asset, path, new Progress<double>(p => UploadPercent = 100 * p), default);
        UploadPercent = 0;
        UploadPath = path;
        UploadInfo = $"{asset.Name} · {asset.Size:N0} bytes · from GitHub {release.Tag}";
        _filledRelease = release;

        var decision = new SyncDecision(new UploadLink { FileId = SelectedFile?.FileId ?? "", Repo = repoName }, release, asset, null, null);
        VersionComment = decision.Comment;
        var version = GitHubReleases.VersionFromTag(release.Tag);
        if (version is null || Versions.Any(v => v.VersionNumber == version)) version = FilehostUploads.SuggestNextVersion(Versions);
        VersionNumber = FilehostUploads.FormatVersion(version.Value);

        // Only a new file takes its details from GitHub; a new version keeps the file's details.
        if (IsNewFile)
        {
            FileTitle = repo.Name;
            Description = GitHubReleases.BuildDescription(repo, release);
            SearchTags = "";
            // GitHub releases of MEGA65 projects are MEGA65 files, even when packed as .zip.
            OsMega65 = true;
            OsC65 = OsC64 = OsVixen = OsWindows = OsMac = OsLinux = OsOnline = false;
            SelectedType = GuessType(asset.Name, repo) ?? SelectedType;
        }

        SetSuggestions(GitHubReleases.SuggestTags(repo, release, FileTitle, asset.Name));
        if (IsNewFile) AddAllTags();

        var notes = new List<string> { $"Filled from {repoName} {release.Tag}." };
        if (IsNewFile && SelectedType is null) notes.Add("Choose the type.");
        notes.Add(IsNewFile ? "Check the details, then press Upload new file." : "Check the version, then press Upload version.");
        Status = string.Join(" ", notes);
    }, "Reading GitHub…");

    /// <summary>
    /// The Filehost type for a release file: by extension as the website does, otherwise
    /// Arcade Core or Alternate Core (the website's types for cores other than the MEGA65's own)
    /// when the project calls itself a core.
    /// </summary>
    private UploadFileType? GuessType(string fileName, GitHubRepository repo)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext.Length > 0 && FileTypes.FirstOrDefault(t => t.Extension?.Contains(ext, StringComparison.OrdinalIgnoreCase) == true) is { } byExtension)
            return byExtension;
        var about = $"{repo.Name} {repo.Description} {string.Join(" ", repo.Topics)}";
        bool Says(string word) => System.Text.RegularExpressions.Regex.IsMatch(about, $@"\b{word}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!Says("core") && !Says("fpga-core")) return null;
        return Says("arcade")
            ? FileTypes.FirstOrDefault(t => t.Name == "Arcade Core")
            : FileTypes.FirstOrDefault(t => t.Name == "Alternate Core");
    }

    /// <summary>After an upload from a GitHub fill, links the file to the repository and records the release.</summary>
    private void RememberLink(string fileId)
    {
        if (_filledRelease is not { } release || GitHubReleases.ParseRepo(GitHubRepo) is not { } repo) return;
        services.UploadLinks.Set(new UploadLink
        {
            FileId = fileId, Repo = repo, AssetPattern = AssetPattern.Trim(), IncludePrereleases = IncludePrereleases,
            LastTag = release.Tag, LastUploadedAt = DateTimeOffset.Now,
        });
        _filledRelease = null;
    }

    // ---------------- GitHub link

    private void ShowLink(string fileId)
    {
        var link = services.UploadLinks.For(fileId);
        GitHubRepo = link?.Repo ?? "";
        AssetPattern = link?.AssetPattern ?? "";
        IncludePrereleases = link?.IncludePrereleases ?? false;
        LinkInfo = link is null ? "Not linked."
            : link.LastTag is { } tag ? $"Last uploaded from GitHub: {tag} ({link.LastUploadedAt?.LocalDateTime:g})." : "Linked. Nothing uploaded from GitHub yet.";
    }

    [RelayCommand]
    private void SaveLink()
    {
        if (SelectedFile is not { } file) return;
        if (GitHubReleases.ParseRepo(GitHubRepo) is not { } repo) { Status = "Enter a GitHub repository, such as https://github.com/owner/repo."; return; }
        var old = services.UploadLinks.For(file.FileId);
        services.UploadLinks.Set(new UploadLink
        {
            FileId = file.FileId, Repo = repo, AssetPattern = AssetPattern.Trim(), IncludePrereleases = IncludePrereleases,
            // Keep the record of what was uploaded unless the link now points somewhere else.
            LastTag = old?.Repo == repo ? old.LastTag : null,
            LastUploadedAt = old?.Repo == repo ? old.LastUploadedAt : null,
        });
        ShowLink(file.FileId);
        Status = $"Linked to {repo}.";
    }

    [RelayCommand]
    private void RemoveLink()
    {
        if (SelectedFile is not { } file) return;
        services.UploadLinks.Remove(file.FileId);
        ShowLink(file.FileId);
        Status = "Link removed.";
    }

    [RelayCommand]
    private Task SyncSelectedAsync()
    {
        if (SelectedFile is { } f && services.UploadLinks.For(f.FileId) is { } link) return SyncAsync([link]);
        Status = "Link this file to a GitHub repository first, then save the link.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task SyncAllAsync() => SyncAsync(services.UploadLinks.Links.ToList());

    public bool HasGitHubToken => !string.IsNullOrWhiteSpace(services.GitHub.Token);
    public string GitHubTokenInfo => HasGitHubToken
        ? "A GitHub token is saved (encrypted for your Windows account): 5,000 GitHub look-ups an hour."
        : "No GitHub token: GitHub allows 60 look-ups an hour. Answers are cached, so repeated look-ups are free.";

    /// <summary>Opens the GitHub token window (steps to create one, the current allowance, save or remove).</summary>
    [RelayCommand]
    private void SetGitHubToken()
    {
        if (!Views.GitHubTokenWindow.Show(services)) return;
        OnPropertyChanged(nameof(HasGitHubToken));
        OnPropertyChanged(nameof(GitHubTokenInfo));
        Status = HasGitHubToken ? "GitHub token saved." : "GitHub token removed.";
    }

    [RelayCommand]
    private void RemoveGitHubToken()
    {
        services.GitHubToken.Forget();
        services.GitHub.Token = null;
        OnPropertyChanged(nameof(HasGitHubToken));
        OnPropertyChanged(nameof(GitHubTokenInfo));
        Status = "GitHub token removed.";
    }

    /// <summary>Called once at startup, after the automatic sign-in.</summary>
    public Task StartupCheckAsync() =>
        services.UploadLinks.CheckAtStartup && services.Session.IsSignedIn && services.UploadLinks.Links.Count > 0
            ? SyncAsync(services.UploadLinks.Links.ToList())
            : Task.CompletedTask;

    /// <summary>
    /// For each linked upload: looks up the newest GitHub release, and if it is not on the Filehost
    /// yet, downloads its file and uploads it as a new version (after one confirmation for all).
    /// </summary>
    private Task SyncAsync(IReadOnlyList<UploadLink> links) => GuardAsync(async () =>
    {
        if (!services.Session.IsSignedIn) { Status = "Sign in to the Filehost first."; return; }
        if (links.Count == 0) { Status = "No uploads are linked to GitHub yet."; return; }
        var mine = (await services.Uploads.ListMyFilesAsync()).ToDictionary(f => f.FileId);

        var decisions = new List<SyncDecision>();
        var notes = new List<string>();
        foreach (var link in links)
        {
            if (!mine.TryGetValue(link.FileId, out var file)) { notes.Add($"{link.Repo}: the linked Filehost file is no longer one of your uploads."); continue; }
            Status = $"Checking {link.Repo}…";
            var d = SyncDecision.Decide(link, await services.GitHub.GetLatestAsync(link.Repo, link.IncludePrereleases),
                await services.Uploads.GetVersionsAsync(link.FileId));
            if (d.NeedsUpload) decisions.Add(d);
            else notes.Add($"{file.DisplayTitle}: {d.UpToDateReason}");
        }

        if (decisions.Count == 0) { Status = "Up to date. " + string.Join(" ", notes); return; }

        var list = string.Join("\n", decisions.Select(d =>
            $"• {mine[d.Link.FileId].DisplayTitle}: {d.Asset!.Name} ({d.Asset.Size:N0} bytes) from {d.Link.Repo} {d.Release!.Tag} as version {FilehostUploads.FormatVersion(d.Version!.Value)}"));
        if (!await gate.ConfirmAsync("Upload new GitHub releases",
                $"New releases were found. Upload them to the Filehost as new versions?\n\n{list}\n\nEach becomes the active version of its file.", default))
        { Status = "Cancelled."; return; }

        var folder = Directory.CreateDirectory(Path.Combine(services.Paths.Downloads, "github")).FullName;
        foreach (var d in decisions)
        {
            var title = mine[d.Link.FileId].DisplayTitle;
            var path = Path.Combine(folder, d.Asset!.Name);
            Status = $"Downloading {d.Asset.Name} from GitHub…";
            await GitHubReleases.DownloadAsync(services.GitHubHttp, d.Asset, path, new Progress<double>(p => UploadPercent = 100 * p), default);
            // The website sends the attributes with every upload, so send the current ones unchanged.
            var attributes = await services.Uploads.GetAttributesAsync(d.Link.FileId);
            Status = $"Uploading {d.Asset.Name} to \"{title}\"…";
            await services.Uploads.UploadNewVersionAsync(d.Link.FileId, path, attributes, d.Version!.Value, d.Comment, VersionMode.New, UploadProgress());
            services.UploadLinks.Set(d.Link with { LastTag = d.Release!.Tag, LastUploadedAt = DateTimeOffset.Now });
            File.Delete(path);
            notes.Insert(0, $"{title}: uploaded {d.Release.Tag} as version {FilehostUploads.FormatVersion(d.Version.Value)}.");
        }

        if (SelectedFile is { } sel) { await LoadVersionsAsync(sel.FileId); ShowLink(sel.FileId); }
        UploadPercent = 0;
        Status = string.Join(" ", notes);
    }, "Checking GitHub…");
}
