using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mega65Gui.Core.Filehost;

/// <summary>A row of readfilelist.php: one of the signed-in user's own uploads.</summary>
public sealed record MyUpload
{
    [JsonPropertyName("id")] public required string FileId { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }

    public string DisplayTitle => FilehostUploads.UnescapeHtml(Title);
}

/// <summary>A row of readfiletypes.php, e.g. id 58 = "Core" in category "Firmware".</summary>
public sealed record UploadFileType
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("extension")] public string? Extension { get; init; }
    [JsonPropertyName("filecategory")] public string? Category { get; init; }

    public string Label => string.IsNullOrEmpty(Category) ? Name : $"{Category} · {Name}";
}

/// <summary>A row of readversionlist.php (the owner's view, which includes inactive versions).</summary>
public sealed record MyVersionRow
{
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("versionid")] public required string VersionId { get; init; }
    [JsonPropertyName("filename")] public string? FileName { get; init; }
    [JsonPropertyName("size")] public string? Size { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("uploaded")] public string? Uploaded { get; init; }
    [JsonPropertyName("active")] public string? Active { get; init; }
    [JsonPropertyName("location")] public string? Location { get; init; }

    public bool IsActive => Active == "Y";
    public long? SizeBytes => FilehostParsing.ParseLong(Size);
    public decimal? VersionNumber =>
        decimal.TryParse(Version, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>A row of readcoreversionsupload.php or readromversions.php: a core or ROM a file can say it needs.</summary>
public sealed record RequirementVersion
{
    [JsonPropertyName("versionid")] public required string VersionId { get; init; }
    [JsonPropertyName("versiontext")] public required string VersionText { get; init; }
    /// <summary>The core's name for core versions ("MEGA65 Development"), the state for ROMs (" Stable").</summary>
    [JsonPropertyName("core")] public string? Core { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }

    public static readonly RequirementVersion NotSelected = new() { VersionId = "-1", VersionText = "Not selected" };

    /// <summary>The website only offers a ROM version when the chosen core is a MEGA65 core.</summary>
    public bool IsMega65Core => VersionText.StartsWith("MEGA65", StringComparison.Ordinal);
    public string Label => State is { Length: > 0 } s ? $"{VersionText} ({s.Trim()})" : VersionText;
}

/// <summary>A preview (screenshot, video or audio) shown on a file's page: readmedias.php.</summary>
public sealed record PreviewMedia
{
    [JsonPropertyName("mediaid")] public required string MediaId { get; init; }
    [JsonPropertyName("location")] public string? Location { get; init; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; init; }
    /// <summary>I image, V video (an upload or a link), A audio.</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }

    public Uri? ThumbnailUri => FilehostClient.ResolveDownloadUri(string.IsNullOrEmpty(Thumbnail) ? null : Thumbnail);
    public Uri? LocationUri => Location is { Length: > 0 } l && Uri.TryCreate(l, UriKind.Absolute, out var abs) ? abs : FilehostClient.ResolveDownloadUri(Location);
    public string Kind => Type switch { "I" => "Picture", "V" => "Video", "A" => "Audio", _ => "Preview" };
}

/// <summary>Who may download a file. The website offers exactly one of these per file.</summary>
public enum UploadAccess { Public, DevKit, Admin, Owner }

public enum VersionMode
{
    /// <summary>Appends the upload as a new version and makes it active.</summary>
    New,
    /// <summary>Deletes the latest version and puts the upload in its place.</summary>
    Replace,
}

/// <summary>
/// The attributes the upload form sends with every upload and every "Update". Values are kept
/// as the user sees them; <see cref="FilehostUploads"/> applies the website's escaping.
/// </summary>
public sealed record UploadAttributes
{
    public string Title { get; init; } = "";
    public string TypeId { get; init; } = "-1";
    public string Description { get; init; } = "";
    public string SearchTags { get; init; } = "";
    public string ShortLink { get; init; } = "";
    public bool Pal { get; init; } = true;
    public bool Ntsc { get; init; } = true;
    public bool OsMega65 { get; init; } = true;
    public bool OsC65 { get; init; }
    public bool OsC64 { get; init; }
    public bool OsVixen { get; init; }
    public bool OsWindows { get; init; }
    public bool OsMac { get; init; }
    public bool OsLinux { get; init; }
    public bool OsOnline { get; init; }
    public bool IntroDisk { get; init; }
    public bool HighScore { get; init; }
    public UploadAccess Access { get; init; } = UploadAccess.Public;
    public string CoreVersionId { get; init; } = "-1";
    public string RomVersionId { get; init; } = "-1";
    public bool Locked { get; init; }

    public bool AnyPlatform => OsMega65 || OsC65 || OsC64 || OsVixen || OsWindows || OsMac || OsLinux || OsOnline;

    /// <summary>The same checks the website runs before it saves.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(Title)) p.Add("Enter a title.");
        if (TypeId == "-1") p.Add("Choose a type.");
        if (!AnyPlatform) p.Add("Tick at least one platform.");
        if (ShortLink.Trim().Length > 0 && !ShortLink.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            p.Add("The short link may only contain letters, digits and '-'.");
        if (BoldProblem(Description) is { } bold) p.Add(bold);
        return p;
    }

    /// <summary>
    /// Why the description's bold markers would break the page, or null. The website turns #bs#
    /// into &lt;b&gt; and #be# into &lt;/b&gt; as they are, so a #bs# without its #be# makes
    /// the whole rest of the description bold.
    /// </summary>
    public static string? BoldProblem(string description)
    {
        var open = -1;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(description, "#bs#|#be#",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var starts = m.Value.Equals("#bs#", StringComparison.OrdinalIgnoreCase);
            if (starts && open >= 0) return $"Description: bold is started again before it is ended, near \"{Near(description, open)}\". Add #be# where the bold text ends.";
            if (!starts && open < 0) return $"Description: #be# without a #bs# before it, near \"{Near(description, m.Index)}\".";
            open = starts ? m.Index : -1;
        }
        return open >= 0 ? $"Description: bold is never ended after \"{Near(description, open)}\", so the rest would be bold. Add #be# where the bold text ends." : null;

        static string Near(string text, int index)
        {
            var piece = text.Substring(index, Math.Min(50, text.Length - index)).ReplaceLineEndings(" ");
            return index + 50 < text.Length ? piece + "…" : piece;
        }
    }
}

public sealed class FilehostUploadException(string message, string? state = null) : Exception(message)
{
    public string? State { get; } = state;
}

/// <summary>
/// The signed-in user's "My File Uploads" on the Filehost, driven through the same endpoints as
/// the website's upload page (html/fileupload.php):
/// <list type="bullet">
/// <item>readfilelist.php, readfile.php, readversionlist.php, readfiletypes.php to read;</item>
/// <item>savefileupload.php (multipart, file field "fileupload") for a new file or a new version;</item>
/// <item>savefileupdate.php to save changed attributes without uploading.</item>
/// </list>
/// The HttpClient must share the cookie container of the signed-in <see cref="FilehostSession"/>.
/// The server checks that the file belongs to the session's account.
/// </summary>
public sealed class FilehostUploads(HttpClient http, FilehostSession session)
{
    /// <summary>The website refuses files over 200 MB.</summary>
    public const long MaxFileSize = 200_000_000;

    /// <summary>The extensions the website's upload form accepts ("" is a file without one).</summary>
    public static readonly IReadOnlySet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "", ".doc", ".docx", ".xls", ".xlsx", ".txt", ".zip", ".rar", ".tar", ".dmg", ".7z", ".pdf", ".iso", ".m65", ".d81",
        ".d64", ".prg", ".asm", ".bas", ".lst", ".c65", ".cor", ".rom", ".bin", ".mcs", ".bit", ".hic", ".sid", ".mod",
        ".osx", ".exe", ".rdf", ".bdf", ".cfg", ".tcr", ".chr",
    };

    /// <summary>The website allows five previews per file.</summary>
    public const int MaxPreviews = 5;

    public static readonly IReadOnlySet<string> AllowedPreviewExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".giff", ".mp4", ".ogg", ".mp3", ".mpeg",
    };

    /// <summary>readmedias.php answers {"state": "OK", "0": {...}, "1": {...}}.</summary>
    public async Task<IReadOnlyList<PreviewMedia>> GetPreviewsAsync(string fileId, CancellationToken ct = default) =>
        NumberedRows<PreviewMedia>(await SendAsync(FilehostClient.AjaxPost("readmedias.php", [new("objectid", fileId), new("origin", "F")]), ct));

    /// <summary>Uploads a picture, video or audio file as a preview of one of your files.</summary>
    public async Task<string> UploadPreviewAsync(string fileId, string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FilehostUploadException("The file does not exist.");
        if (!AllowedPreviewExtensions.Contains(info.Extension))
            throw new FilehostUploadException("Previews can be jpg, jpeg, png, gif, mp4, ogg, mp3 or mpeg files.");
        if (info.Length > MaxFileSize) throw new FilehostUploadException("The Filehost accepts files up to 200 MB.");

        await using var stream = File.OpenRead(path);
        var file = new ProgressStreamContent(stream, progress);
        file.Headers.ContentType = new MediaTypeHeaderValue(info.Extension.ToLowerInvariant() switch
        {
            ".png" => "image/png", ".gif" or ".giff" => "image/gif", ".jpg" or ".jpeg" => "image/jpeg",
            ".mp4" => "video/mp4", ".mp3" or ".mpeg" => "audio/mpeg", ".ogg" => "audio/ogg", _ => "application/octet-stream",
        });
        using var form = new MultipartFormDataContent
        {
            { file, "mediaupload", info.Name },
            { new StringContent(fileId), "objectid" },
            { new StringContent("F"), "origin" },
        };
        using var req = FilehostClient.AjaxPost("savemediaupload.php", []);
        req.Content = form;
        return Expect(await SendAsync(req, ct), "OK").Message ?? "Preview added.";
    }

    /// <summary>Adds a link to a video (for example on YouTube) as a preview.</summary>
    public async Task<string> AddPreviewLinkAsync(string fileId, string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FilehostUploadException("Enter the full address of the video, starting with https://.");
        var json = await SendAsync(FilehostClient.AjaxPost("savemedialink.php",
            [new("objectid", fileId), new("origin", "F"), new("link", uri.ToString()), new("type", "V")]), ct);
        return Expect(json, "OK").Message ?? "Video link added.";
    }

    public async Task<string> DeletePreviewAsync(string mediaId, CancellationToken ct = default)
    {
        var json = await SendAsync(FilehostClient.AjaxPost("savedeletemedia.php", [new("publicid", mediaId), new("origin", "F")]), ct);
        return Expect(json, "OK").Message ?? "Preview deleted.";
    }

    public async Task<IReadOnlyList<MyUpload>> ListMyFilesAsync(CancellationToken ct = default) =>
        Rows<MyUpload>(await SendAsync(FilehostClient.AjaxGet("readfilelist.php"), ct));

    public async Task<IReadOnlyList<UploadFileType>> GetFileTypesAsync(CancellationToken ct = default) =>
        Rows<UploadFileType>(await SendAsync(FilehostClient.AjaxGet("readfiletypes.php"), ct));

    public async Task<IReadOnlyList<RequirementVersion>> GetCoreVersionsAsync(CancellationToken ct = default) =>
        Rows<RequirementVersion>(await SendAsync(FilehostClient.AjaxPost("readcoreversionsupload.php", []), ct));

    public async Task<IReadOnlyList<RequirementVersion>> GetRomVersionsAsync(CancellationToken ct = default) =>
        Rows<RequirementVersion>(await SendAsync(FilehostClient.AjaxPost("readromversions.php", []), ct));

    public async Task<UploadAttributes> GetAttributesAsync(string fileId, CancellationToken ct = default) =>
        ParseAttributes(await SendAsync(FilehostClient.AjaxPost("readfile.php", [new("fileid", fileId)]), ct));

    public async Task<IReadOnlyList<MyVersionRow>> GetVersionsAsync(string fileId, CancellationToken ct = default) =>
        Rows<MyVersionRow>(await SendAsync(FilehostClient.AjaxPost("readversionlist.php", [new("fileid", fileId)]), ct));

    /// <summary>Uploads a brand-new file. Returns the new file's ID.</summary>
    public Task<string> UploadNewFileAsync(string path, UploadAttributes attributes, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (attributes.Problems() is { Count: > 0 } problems) throw new FilehostUploadException(string.Join(" ", problems));
        return UploadAsync(path, "-1", attributes, "Initial version", "1", "init", progress, ct);
    }

    /// <summary>
    /// Uploads a new version of one of your files. The website sends the file's attributes with
    /// every upload, so pass the current ones (from <see cref="GetAttributesAsync"/>), or they
    /// would be overwritten.
    /// </summary>
    public Task<string> UploadNewVersionAsync(string fileId, string path, UploadAttributes attributes, decimal version, string comment,
        VersionMode mode, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (fileId is "" or "-1") throw new ArgumentException("Choose one of your files.", nameof(fileId));
        if (!IsValidVersion(version)) throw new FilehostUploadException("The version must be between 1 and 9999.9, with at most one decimal place.");
        return UploadAsync(path, fileId, attributes, comment, FormatVersion(version), mode == VersionMode.New ? "new" : "replace", progress, ct);
    }

    /// <summary>Saves changed attributes (title, description, …) without uploading a file.</summary>
    public async Task<string> SaveAttributesAsync(string fileId, UploadAttributes attributes, CancellationToken ct = default)
    {
        if (attributes.Problems() is { Count: > 0 } problems) throw new FilehostUploadException(string.Join(" ", problems));
        var json = await SendAsync(FilehostClient.AjaxPost("savefileupdate.php", [new("fileid", fileId), .. AttributeFields(attributes)]), ct);
        return Expect(json, "OK").Message ?? "Saved.";
    }

    /// <summary>The website's version box: 1 to 9999.9 in steps of 0.1.</summary>
    public static bool IsValidVersion(decimal v) => v is >= 1 and <= 9999.9m && decimal.Round(v, 1) == v;

    /// <summary>Formatted as the browser sends the number: "2", "1.1".</summary>
    public static string FormatVersion(decimal v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The next version after the highest one so far, one step of 0.1 up.</summary>
    public static decimal SuggestNextVersion(IEnumerable<MyVersionRow> versions) =>
        versions.Select(v => v.VersionNumber).Where(v => v is not null).Max() is { } max ? Math.Min(max + 0.1m, 9999.9m) : 1m;

    /// <summary>Why the website would refuse this file, or null if it is acceptable.</summary>
    public static string? CheckFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return "The file does not exist.";
        if (!AllowedExtensions.Contains(info.Extension))
            return $"The Filehost does not accept {info.Extension} files. Allowed: {string.Join(", ", AllowedExtensions.Where(e => e != "").Order())}.";
        if (info.Length > MaxFileSize) return "The Filehost accepts files up to 200 MB.";
        if (info.Length == 0) return "The file is empty.";
        return null;
    }

    private async Task<string> UploadAsync(string path, string fileId, UploadAttributes attributes, string comment, string version,
        string mode, IProgress<double>? progress, CancellationToken ct)
    {
        if (CheckFile(path) is { } problem) throw new FilehostUploadException(problem);

        await using var stream = File.OpenRead(path);
        var file = new ProgressStreamContent(stream, progress);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var form = new MultipartFormDataContent { { file, "fileupload", Path.GetFileName(path) } };
        foreach (var (k, v) in UploadFields(fileId, attributes, comment, version, mode)) form.Add(new StringContent(v), k);

        using var req = FilehostClient.AjaxPost("savefileupload.php", []);
        req.Content = form;
        var json = await SendAsync(req, ct);
        var (_, message, newId) = Expect(json, "OK");
        return fileId == "-1" ? newId ?? throw new FilehostUploadException("The Filehost did not return the new file's ID.") : fileId;
    }

    internal static IEnumerable<KeyValuePair<string, string>> UploadFields(string fileId, UploadAttributes a, string comment, string version, string mode)
    {
        yield return new("fileid", fileId);
        foreach (var f in AttributeFields(a)) yield return f;
        yield return new("comment", EscapeBackslash(comment.Trim()));
        yield return new("version", version);
        yield return new("mode", mode);
    }

    /// <summary>The attribute fields in the website's names, escaped as the website escapes them.</summary>
    internal static IEnumerable<KeyValuePair<string, string>> AttributeFields(UploadAttributes a)
    {
        static string YN(bool b) => b ? "Y" : "N";
        yield return new("title", EscapeHtml(EscapeBackslash(a.Title.Trim())));
        yield return new("type", a.TypeId);
        yield return new("description", EscapeBackslash(a.Description.Trim()));
        yield return new("searchtags", EscapeHtml(EscapeBackslash(a.SearchTags.Trim())));
        yield return new("shortlink", a.ShortLink.Trim());
        yield return new("ntsc", YN(a.Ntsc));
        yield return new("pal", YN(a.Pal));
        yield return new("osmega65", YN(a.OsMega65));
        yield return new("osc65", YN(a.OsC65));
        yield return new("osc64", YN(a.OsC64));
        yield return new("osvixen", YN(a.OsVixen));
        yield return new("oswindows", YN(a.OsWindows));
        yield return new("osmac", YN(a.OsMac));
        yield return new("oslinux", YN(a.OsLinux));
        yield return new("osonline", YN(a.OsOnline));
        yield return new("introdisk", YN(a.IntroDisk));
        yield return new("highscore", YN(a.HighScore));
        yield return new("grouppublic", YN(a.Access == UploadAccess.Public));
        // The website's "DevKit" button posts as groupmega65.
        yield return new("groupmega65", YN(a.Access == UploadAccess.DevKit));
        yield return new("groupadmin", YN(a.Access == UploadAccess.Admin));
        yield return new("groupowner", YN(a.Access == UploadAccess.Owner));
        yield return new("coreversionid", a.CoreVersionId);
        yield return new("romversionid", a.RomVersionId);
    }

    /// <summary>readfile.php answers {"0": {...}, "state": "OK"}.</summary>
    public static UploadAttributes ParseAttributes(string json)
    {
        var (state, message, _) = ReadState(json);
        if (state != "OK") throw new FilehostUploadException(message ?? "The Filehost did not return the file. Is it one of yours?", state);
        var node = JsonNode.Parse(json) switch
        {
            JsonArray { Count: > 0 } a => a[0],
            JsonObject o => o["0"],
            _ => null,
        } ?? throw new FilehostUploadException("The Filehost returned an unexpected answer.");

        string S(string key) => node[key]?.GetValue<string>() ?? "";
        bool Y(string key) => S(key) == "Y";
        return new UploadAttributes
        {
            // The title and tags come back HTML-escaped; the website unescapes them into the form.
            Title = UnescapeHtml(S("title")),
            TypeId = S("filetypeid") is { Length: > 0 } t ? t : "-1",
            Description = S("description"),
            SearchTags = UnescapeHtml(S("searchtags")),
            ShortLink = S("shortlink"),
            Ntsc = Y("ntsc"), Pal = Y("pal"),
            OsMega65 = Y("osmega65"), OsC65 = Y("osc65"), OsC64 = Y("osc64"), OsVixen = Y("osvixen"),
            OsWindows = Y("oswindows"), OsMac = Y("osmac"), OsLinux = Y("oslinux"), OsOnline = Y("osonline"),
            IntroDisk = Y("introdisk"), HighScore = Y("highscore"),
            Access = S("groupname") switch
            {
                "devkit" => UploadAccess.DevKit,
                "admin" => UploadAccess.Admin,
                "owner" => UploadAccess.Owner,
                _ => UploadAccess.Public,
            },
            CoreVersionId = S("coreversionid") is { Length: > 0 } c ? c : "-1",
            RomVersionId = S("romversionid") is { Length: > 0 } r ? r : "-1",
            Locked = Y("locked"),
        };
    }

    // The website's escapeBackslash/escapeHTML/unescapeHTML helpers.
    internal static string EscapeBackslash(string s) => s.Replace("\\", "\\\\");
    internal static string EscapeHtml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    internal static string UnescapeHtml(string s) => s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">");

    private async Task<string> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        {
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var (state, _, _) = ReadState(json);
            if (FilehostSession.IsExpiredState(state))
            {
                session.MarkExpired();
                throw new FilehostUploadException("Your Filehost session has ended. Please sign in again.", state);
            }
            return json;
        }
    }

    private static (string? State, string? Message, string? FileId) Expect(string json, string ok)
    {
        var r = ReadState(json);
        if (r.State == ok) return r;
        throw new FilehostUploadException(r.Message is { Length: > 0 } m ? m : $"The Filehost refused the request ({r.State ?? "no answer"}).", r.State);
    }

    /// <summary>Some lists come back as an object with numbered keys next to "state".</summary>
    internal static IReadOnlyList<T> NumberedRows<T>(string json)
    {
        var (state, message, _) = ReadState(json);
        if (state is "EMPTY") return [];
        if (state != "OK") throw new FilehostUploadException(message ?? $"The Filehost answered {state ?? "nothing"}.", state);
        if (JsonNode.Parse(json) is not JsonObject o) return [];
        return o.Where(kv => int.TryParse(kv.Key, out _)).OrderBy(kv => int.Parse(kv.Key))
            .Select(kv => kv.Value.Deserialize<T>()).OfType<T>().ToList();
    }

    /// <summary>Lists come back as arrays; errors and expired sessions as an object with a state.</summary>
    private static IReadOnlyList<T> Rows<T>(string json)
    {
        var (state, message, _) = ReadState(json);
        if (state is not null && state != "OK" && !json.TrimStart().StartsWith('['))
            throw new FilehostUploadException(message ?? $"The Filehost answered {state}.", state);
        try
        {
            return JsonNode.Parse(json) is JsonArray a ? a.Deserialize<List<T>>() ?? [] : [];
        }
        catch (JsonException)
        {
            throw new FilehostUploadException("The Filehost returned an unexpected answer.");
        }
    }

    private static (string? State, string? Message, string? FileId) ReadState(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return (null, null, null);
            return (Str(o["state"]), Str(o["message"]), Str(o["fileid"]));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        static string? Str(JsonNode? n) => n is JsonValue v ? v.ToString() : null;
    }

    /// <summary>Streams the file and reports the fraction sent.</summary>
    private sealed class ProgressStreamContent(Stream stream, IProgress<double>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long sent = 0, total = stream.Length;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                sent += n;
                progress?.Report(total == 0 ? 1 : (double)sent / total);
            }
        }

        protected override Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(target, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = stream.Length;
            return true;
        }
    }
}
