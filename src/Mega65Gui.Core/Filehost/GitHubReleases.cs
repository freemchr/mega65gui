using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mega65Gui.Core.Filehost;

public sealed record GitHubAsset
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("browser_download_url")] public required string DownloadUrl { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
}

public sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")] public required string Tag { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("html_url")] public string? Url { get; init; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
    [JsonPropertyName("draft")] public bool Draft { get; init; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
    [JsonPropertyName("assets")] public IReadOnlyList<GitHubAsset> Assets { get; init; } = [];
}

public sealed record GitHubRepository
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("full_name")] public string? FullName { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("html_url")] public string? Url { get; init; }
    [JsonPropertyName("homepage")] public string? Homepage { get; init; }
    [JsonPropertyName("topics")] public IReadOnlyList<string> Topics { get; init; } = [];
}

/// <summary>
/// Links one of your Filehost uploads to a GitHub repository, so its newest release can be
/// uploaded as a new Filehost version.
/// </summary>
public sealed record UploadLink
{
    public required string FileId { get; init; }
    /// <summary>"owner/repo".</summary>
    public required string Repo { get; init; }
    /// <summary>Which release file to upload, e.g. "MegaST-*.zip". Empty picks the first file the Filehost accepts.</summary>
    public string AssetPattern { get; init; } = "";
    public bool IncludePrereleases { get; init; }
    /// <summary>The release tag last uploaded from this link.</summary>
    public string? LastTag { get; init; }
    public DateTimeOffset? LastUploadedAt { get; init; }
}

/// <summary>Reads releases through the public GitHub REST API (no token; 60 requests an hour).</summary>
public sealed partial class GitHubReleases(HttpClient http)
{
    private static readonly Uri Api = new("https://api.github.com/");

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?:github\.com/)?(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9._-]+?)(?:\.git)?(?:/.*)?$")]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"(?<!\d)(?<major>\d{1,4})(?:\.(?<minor>\d+))?")]
    private static partial Regex VersionPattern();

    /// <summary>"owner/repo" from a GitHub URL, "owner/repo" or a release link; null if it is not one.</summary>
    public static string? ParseRepo(string? text)
    {
        var m = RepoPattern().Match((text ?? "").Trim());
        return m.Success ? $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}" : null;
    }

    public async Task<GitHubRepository> GetRepositoryAsync(string repo, CancellationToken ct = default)
    {
        using var resp = await SendAsync($"repos/{repo}", repo, ct);
        return JsonSerializer.Deserialize<GitHubRepository>(await resp.Content.ReadAsStringAsync(ct))
            ?? throw new FilehostUploadException("GitHub returned an unexpected answer.");
    }

    private async Task<HttpResponseMessage> SendAsync(string path, string repo, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Api, path));
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { resp.Dispose(); throw new FilehostUploadException($"GitHub has no repository {repo} (or it is private)."); }
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden) { resp.Dispose(); throw new FilehostUploadException("GitHub's hourly limit for unauthenticated requests was reached. Try again later."); }
        resp.EnsureSuccessStatusCode();
        return resp;
    }

    /// <summary>The newest published release, newest first; pre-releases only when asked for.</summary>
    public async Task<GitHubRelease?> GetLatestAsync(string repo, bool includePrereleases, CancellationToken ct = default)
    {
        using var resp = await SendAsync($"repos/{repo}/releases?per_page=20", repo, ct);
        var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(await resp.Content.ReadAsStringAsync(ct)) ?? [];
        return releases.Where(r => !r.Draft && (includePrereleases || !r.Prerelease))
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue).FirstOrDefault();
    }

    /// <summary>The release file to upload: the first one matching the pattern (with * and ?) that the Filehost accepts.</summary>
    public static GitHubAsset? PickAsset(GitHubRelease release, string? pattern)
    {
        var accepted = release.Assets.Where(a => FilehostUploads.AllowedExtensions.Contains(Path.GetExtension(a.Name)) && a.Size <= FilehostUploads.MaxFileSize);
        if (string.IsNullOrWhiteSpace(pattern)) return accepted.FirstOrDefault();
        var glob = new Regex("^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
        return accepted.FirstOrDefault(a => glob.IsMatch(a.Name));
    }

    /// <summary>
    /// The Filehost version number for a tag: "v1.2" gives 1.2, "MegaST-2" gives 2. Tags with more
    /// parts than the Filehost's nnnn.n (such as 1.2.3) give null, and the caller suggests one instead.
    /// </summary>
    public static decimal? VersionFromTag(string tag)
    {
        var matches = VersionPattern().Matches(tag);
        if (matches.Count != 1 || Regex.IsMatch(tag, @"\d+\.\d+\.\d+")) return null;
        var m = matches[0];
        var text = m.Groups["minor"].Success ? $"{m.Groups["major"].Value}.{m.Groups["minor"].Value}" : m.Groups["major"].Value;
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && FilehostUploads.IsValidVersion(v) ? v : null;
    }

    /// <summary>
    /// A pattern that finds the same release file in later releases: the tag's version in the
    /// file name becomes *, so "MegaST-v1.2.zip" of tag v1.2 gives "MegaST-v*.zip".
    /// </summary>
    public static string SuggestPattern(GitHubAsset asset, string tag)
    {
        var version = Regex.Match(tag, @"\d+(?:[._-]\d+)*").Value;
        if (version.Length > 0 && asset.Name.Contains(version, StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(asset.Name, Regex.Escape(version), "*", RegexOptions.IgnoreCase);
        return "*" + Path.GetExtension(asset.Name);
    }

    /// <summary>
    /// A Filehost description from the repository and release: plain text, where the Filehost's
    /// #bs#…#be# marks bold. Markdown headings and **bold** become bold, links become "text (url)".
    /// </summary>
    public static string BuildDescription(GitHubRepository repo, GitHubRelease? release)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(repo.Description)) parts.Add(repo.Description.Trim());
        if (release is not null && !string.IsNullOrWhiteSpace(release.Body))
        {
            var heading = string.IsNullOrWhiteSpace(release.Name) ? release.Tag : release.Name.Trim();
            var notes = MarkdownToFilehost(release.Body);
            parts.Add(notes.Contains($"#bs#{heading}#be#", StringComparison.OrdinalIgnoreCase) ? notes : $"#bs#{heading}#be#\n{notes}");
        }
        var links = new List<string>();
        if (repo.Url is { } url) links.Add($"Source: {url}");
        if (!string.IsNullOrWhiteSpace(repo.Homepage) && repo.Homepage != repo.Url) links.Add($"Website: {repo.Homepage.Trim()}");
        if (links.Count > 0) parts.Add(string.Join("\n", links));
        var text = string.Join("\n\n", parts);
        return text.Length <= 65000 ? text : text[..65000];
    }

    public static string MarkdownToFilehost(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n");
        // Badges are images inside links: drop them, and plain images too.
        text = Regex.Replace(text, @"\[!\[[^\]]*\]\([^)]*\)\]\([^)]*\)", "");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
        // HTML in release notes (a centred logo, say): the Filehost would show the tags as text.
        text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
        // An explicit <br> is a hard line break: kept as a marker so line joining leaves it alone.
        text = Regex.Replace(text, @"\s*<br\s*/?>\s*", "\u0001", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?(?:b|strong)>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<a\s[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", "[$2]($1)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"</?[A-Za-z][^>]*>", "");
        text = System.Net.WebUtility.HtmlDecode(text);

        // The Filehost shows line breaks as they are, so hard-wrapped paragraph lines are joined.
        // Inline markup is converted afterwards, because bold text may wrap across lines.
        var output = new List<string>();
        var joinable = false;
        var lines = text.Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            var line = Unquote(lines[n]).TrimEnd();
            var callout = Regex.Match(line, @"^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$", RegexOptions.IgnoreCase);
            var heading = Regex.Match(line, @"^\s{0,3}#{1,6}\s+(.*?)\s*#*$");
            if (callout.Success)
            {
                var word = callout.Groups[1].Value;
                output.Add($"**{char.ToUpperInvariant(word[0])}{word[1..].ToLowerInvariant()}:**");
                joinable = true;
            }
            else if (heading.Success)
            {
                output.Add($"**{heading.Groups[1].Value}**");
                joinable = false;
            }
            else if (IsTableRow(line))
            {
                // A table becomes one line per row, cells separated by " | ", the header row in bold.
                if (Regex.IsMatch(line, @"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")) continue;
                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim());
                var row = string.Join(" | ", cells);
                var isHeader = n + 1 < lines.Length && Regex.IsMatch(Unquote(lines[n + 1]), @"^\s*\|?\s*:?-{3,}");
                output.Add(isHeader ? $"**{row}**" : row);
                joinable = false;
            }
            else if (line.Trim().Length == 0)
            {
                if (output.Count > 0 && output[^1].Length > 0) output.Add("");
                joinable = false;
            }
            else if (Regex.IsMatch(line, @"^\s*(?:[*+-]|\d+[.)])\s+"))
            {
                // "* x" and "+ x" read as "- x"; numbered items stay as they are.
                output.Add(Regex.Replace(line, @"^(\s*)[*+]\s+", "$1- "));
                joinable = true;
            }
            else if (joinable && output.Count > 0)
                output[^1] += " " + line.Trim();
            else
            {
                output.Add(line.Trim());
                joinable = true;
            }
        }

        text = string.Join("\n", output).Trim().Replace('\u0001', '\n');
        text = Regex.Replace(text, @"\*\*(.+?)\*\*|__(.+?)__", m => $"#bs#{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}#be#");
        text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)\s]+)\)", m => m.Groups[1].Value == m.Groups[2].Value ? m.Groups[2].Value : $"{m.Groups[1].Value} ({m.Groups[2].Value})");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        return text;

        static string Unquote(string line) => Regex.Replace(line, @"^(\s*>\s?)+", "");
        static bool IsTableRow(string line) => line.TrimStart().StartsWith('|') && line.Count(c => c == '|') >= 2;
    }

    /// <summary>Search tags from the repository topics, within the Filehost's 200 characters.</summary>
    public static string BuildSearchTags(GitHubRepository repo) => JoinTags(repo.Topics.Where(t => t.Length >= 3));

    /// <summary>Joins tags as the Filehost's own files do ("atari, atari st, core"), within 200 characters.</summary>
    public static string JoinTags(IEnumerable<string> tags)
    {
        var kept = new List<string>();
        foreach (var t in tags)
        {
            if (string.Join(", ", kept.Append(t)).Length > 200) break;
            kept.Add(t);
        }
        return string.Join(", ", kept);
    }

    // Terms worth searching for on a MEGA65 file host, matched as whole words in the repository
    // and release text. Each maps to the tag that is suggested.
    private static readonly (string Pattern, string Tag)[] KnownTerms =
    [
        (@"zx\s*spectrum|spectrum", "spectrum"), (@"zx\s*next|spectrum\s*next", "zx next"), (@"sinclair", "sinclair"),
        (@"nextzxos", "nextzxos"), (@"z80n?", "z80"), (@"6502", "6502"), (@"45gs02", "45gs02"), (@"68000|68k", "68000"),
        (@"atari\s*st", "atari st"), (@"atari", "atari"), (@"amiga", "amiga"), (@"c64|commodore\s*64", "c64"), (@"vic-?20", "vic20"),
        (@"commodore", "commodore"), (@"msx", "msx"), (@"amstrad|cpc", "amstrad"), (@"bbc\s*micro", "bbc micro"),
        (@"nes|famicom", "nes"), (@"game\s*boy", "gameboy"), (@"arcade", "arcade"), (@"apple\s*ii", "apple ii"),
        (@"mister", "mister"), (@"mister2mega65", "mister2mega65"), (@"fpga", "fpga"), (@"vhdl", "vhdl"), (@"verilog", "verilog"),
        (@"core", "core"), (@"emulat(?:or|ion)", "emulator"), (@"emutos", "emutos"), (@"tos", "tos"),
        (@"joystick", "joystick"), (@"floppy", "floppy"),
        (@"game", "game"), (@"demo", "demo"), (@"basic", "basic"), (@"music|sid", "music"), (@"tool|utility", "tool"),
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "into", "this", "that", "your", "you", "are", "was", "has", "have", "not",
        "its", "can", "all", "any", "but", "one", "two", "new", "use", "used", "using", "via", "port", "version", "release",
        "mega", "mega65", "files", "file",
    };

    /// <summary>
    /// Search tags to suggest for a GitHub project: its topics, known terms found in its name,
    /// description and release notes (most mentioned first), and the remaining description words.
    /// Words already in the title or file name are left out, because the Filehost searches those.
    /// Each tag has at least 3 characters, as the website asks.
    /// </summary>
    public static IReadOnlyList<string> SuggestTags(GitHubRepository repo, GitHubRelease? release, string? title, string? fileName)
    {
        var searched = new HashSet<string>(Words($"{title} {fileName} {SplitCamel(title ?? "")}"), StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();
        void Add(string tag)
        {
            tag = tag.Trim().ToLowerInvariant();
            if (tag.Length < 3 || StopWords.Contains(tag) || tags.Contains(tag)) return;
            // A tag made only of words the Filehost already searches adds nothing.
            if (Words(tag).All(searched.Contains)) return;
            tags.Add(tag);
        }

        foreach (var t in repo.Topics) Add(t.Replace('-', ' '));

        var about = $"{repo.Name} {SplitCamel(repo.Name)} {repo.Description}";
        var notes = $"{release?.Name} {release?.Body}";
        int Count(string text, string pattern) => Regex.Matches(text, $@"\b(?:{pattern})\b", RegexOptions.IgnoreCase).Count;
        // A term in the name or description counts at once; one only in the release notes needs
        // two mentions, so a passing remark (a copyright line, say) does not become a tag.
        foreach (var (_, tag) in KnownTerms
                     .Select(k => (InAbout: Count(about, k.Pattern), InNotes: Count(notes, k.Pattern), k.Tag))
                     .Where(k => k.InAbout > 0 || k.InNotes >= 2)
                     .OrderByDescending(k => k.InAbout).ThenByDescending(k => k.InNotes)
                     .Select(k => (0, k.Tag)))
            Add(tag);

        foreach (var w in Words(repo.Description ?? "")) Add(w);
        return tags.Take(12).ToList();

        static IEnumerable<string> Words(string s) => Regex.Matches(s, @"[A-Za-z0-9][A-Za-z0-9+#.-]*[A-Za-z0-9+#]|[A-Za-z0-9]")
            .Select(m => m.Value.ToLowerInvariant());
    }

    /// <summary>"MegaZXNext" gives "Mega ZX Next".</summary>
    public static string SplitCamel(string s) => Regex.Replace(s, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");

    public static async Task DownloadAsync(HttpClient http, GitHubAsset asset, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? asset.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using (var dst = File.Create(path))
        {
            var buffer = new byte[81920];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                got += n;
                if (total > 0) progress?.Report((double)got / total);
            }
        }
        if (asset.Size > 0 && new FileInfo(path).Length != asset.Size)
            throw new FilehostUploadException($"The download of {asset.Name} was incomplete.");
    }
}

/// <summary>The GitHub links, kept in upload-links.json next to the other settings.</summary>
public sealed class UploadLinkStore(string file)
{
    private sealed record Stored(List<UploadLink> Links, bool CheckAtStartup);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<UploadLink> Links { get; private set; } = [];
    public bool CheckAtStartup { get; set; }

    public void Load()
    {
        try
        {
            if (!File.Exists(file)) return;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file));
            Links = s?.Links ?? [];
            CheckAtStartup = s?.CheckAtStartup ?? false;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Links = [];
        }
    }

    public UploadLink? For(string fileId) => Links.FirstOrDefault(l => l.FileId == fileId);

    public void Set(UploadLink link)
    {
        Links.RemoveAll(l => l.FileId == link.FileId);
        Links.Add(link);
        Save();
    }

    public void Remove(string fileId)
    {
        Links.RemoveAll(l => l.FileId == fileId);
        Save();
    }

    public void Save() => File.WriteAllText(file, JsonSerializer.Serialize(new Stored(Links, CheckAtStartup), Options));
}

/// <summary>What a sync would do for one linked upload.</summary>
public sealed record SyncDecision(UploadLink Link, GitHubRelease? Release, GitHubAsset? Asset, decimal? Version, string? UpToDateReason)
{
    public bool NeedsUpload => UpToDateReason is null;

    /// <summary>Compares the newest release with what is already on the Filehost.</summary>
    public static SyncDecision Decide(UploadLink link, GitHubRelease? release, IReadOnlyList<MyVersionRow> versions)
    {
        if (release is null) return new(link, null, null, null, "No published release on GitHub.");
        var asset = GitHubReleases.PickAsset(release, link.AssetPattern);
        if (asset is null)
            return new(link, release, null, null, string.IsNullOrWhiteSpace(link.AssetPattern)
                ? $"Release {release.Tag} has no file the Filehost accepts."
                : $"Release {release.Tag} has no file matching {link.AssetPattern}.");
        if (link.LastTag == release.Tag) return new(link, release, asset, null, $"Release {release.Tag} is already uploaded.");
        if (versions.Any(v => string.Equals(v.FileName, asset.Name, StringComparison.OrdinalIgnoreCase)))
            return new(link, release, asset, null, $"{asset.Name} is already on the Filehost.");

        var version = GitHubReleases.VersionFromTag(release.Tag);
        if (version is null || versions.Any(v => v.VersionNumber == version)) version = FilehostUploads.SuggestNextVersion(versions);
        return new(link, release, asset, version, null);
    }

    /// <summary>The Filehost version comment: the release name and link, within the website's 1000 characters.</summary>
    public string Comment
    {
        get
        {
            if (Release is null) return "";
            var name = string.IsNullOrWhiteSpace(Release.Name) ? Release.Tag : Release.Name.Trim();
            var text = Release.Url is { } url ? $"{name} ({url})" : name;
            return text.Length <= 1000 ? text : text[..1000];
        }
    }
}
