using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mega65Gui.Core.Filehost;

/// <summary>
/// Read-only client for the public, unauthenticated Filehost endpoints. These are the site's
/// own undocumented endpoints (POST, form-encoded, JSON returned as text/html), so every
/// response is parsed defensively.
///
/// Access rule: version lists are only requested for file IDs that appear in the public
/// catalogue. Owner-restricted files (for example closed-ROM packages) are never queried and
/// are handed off to the website instead.
/// </summary>
public sealed class FilehostClient(HttpClient http)
{
    public static readonly Uri BaseUri = new("https://files.mega65.org/");
    public static readonly Uri PhpBase = new(BaseUri, "php/");

    public static Uri WebPageFor(string fileIdOrLinkId) => new(BaseUri, $"?id={Uri.EscapeDataString(fileIdOrLinkId)}");

    public async Task<string> GetCatalogueJsonAsync(CancellationToken ct = default) =>
        await PostAsync("readfilespublic.php", [], ct);

    public async Task<FilehostFileDetail?> GetFileDetailAsync(string fileId, CancellationToken ct = default)
    {
        var json = await PostAsync("readfiledetail.php", [new("fileid", fileId)], ct);
        return ParseFileDetail(json);
    }

    public async Task<IReadOnlyList<FilehostVersionRow>> GetVersionsAsync(string publicFileId, CancellationToken ct = default)
    {
        var json = await PostAsync("readversionlistpublic.php", [new("fileid", publicFileId)], ct);
        return ParseVersionList(json);
    }

    private async Task<string> PostAsync(string script, KeyValuePair<string, string>[] form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await http.PostAsync(new Uri(PhpBase, script), content, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public static IReadOnlyList<FilehostFile> ParseCatalogue(string json) =>
        JsonSerializer.Deserialize<List<FilehostFile>>(json) ?? [];

    public static IReadOnlyList<FilehostVersionRow> ParseVersionList(string json)
    {
        // Some Filehost endpoints answer errors with an object rather than an array.
        var node = JsonNode.Parse(json);
        return node is JsonArray ? node.Deserialize<List<FilehostVersionRow>>() ?? [] : [];
    }

    public static FilehostFileDetail? ParseFileDetail(string json)
    {
        var node = JsonNode.Parse(json);
        var first = node is JsonArray { Count: > 0 } arr ? arr[0] : node;
        return first?.Deserialize<FilehostFileDetail>();
    }

    /// <summary>`location` is relative to /php/ (e.g. "../files/m/name_XXXX.7z"); files are served statically.</summary>
    public static Uri? ResolveDownloadUri(string? location) =>
        string.IsNullOrWhiteSpace(location) ? null : new Uri(PhpBase, location);

    public static Uri? ResolveDownloadUri(FilehostVersionRow row) => ResolveDownloadUri(row.Location);
}
