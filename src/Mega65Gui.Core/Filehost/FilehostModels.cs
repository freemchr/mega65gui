using System.Globalization;
using System.Text.Json.Serialization;

namespace Mega65Gui.Core.Filehost;

/// <summary>Row of readfilespublic.php: one per public file, latest version only.</summary>
public sealed record FilehostFile
{
    [JsonPropertyName("fileid")] public required string FileId { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("os")] public string? Os { get; init; }
    [JsonPropertyName("rating")] public string? Rating { get; init; }
    [JsonPropertyName("downloads")] public string? Downloads { get; init; }
    [JsonPropertyName("search")] public string? Search { get; init; }
    [JsonPropertyName("published")] public string? Published { get; init; }
    [JsonPropertyName("sortdate")] public string? SortDate { get; init; }
    [JsonPropertyName("versionid")] public string? VersionId { get; init; }
    [JsonPropertyName("filename")] public string? FileName { get; init; }
    [JsonPropertyName("size")] public string? Size { get; init; }
    [JsonPropertyName("location")] public string? Location { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }

    public IReadOnlyList<string> Platforms =>
        (Os ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int DownloadCount => int.TryParse(Downloads?.Trim(), out var d) ? d : 0;
    public long? SizeBytes => FilehostParsing.ParseLong(Size);
    public decimal? RatingValue => FilehostParsing.ParseRating(Rating);

    public DateTime? Updated =>
        DateTime.TryParseExact(SortDate, "yyyy/MM/dd/HH/mm/ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public IReadOnlyList<string> Tags =>
        (Search ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Row of readfiledetail.php. The server returns [{"state":"EMPTY"}] for unknown or restricted files.</summary>
public sealed record FilehostFileDetail
{
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("fileid")] public string? FileId { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("linkid")] public string? LinkId { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("os")] public string? Os { get; init; }
    [JsonPropertyName("rating")] public string? Rating { get; init; }
    [JsonPropertyName("ratingcount")] public string? RatingCount { get; init; }
    [JsonPropertyName("downloads")] public string? Downloads { get; init; }
    [JsonPropertyName("pal")] public string? Pal { get; init; }
    [JsonPropertyName("ntsc")] public string? Ntsc { get; init; }
    [JsonPropertyName("versionid")] public string? VersionId { get; init; }
    [JsonPropertyName("filename")] public string? FileName { get; init; }
    [JsonPropertyName("size")] public string? Size { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("location")] public string? Location { get; init; }
    [JsonPropertyName("coretext")] public string? CoreText { get; init; }
    [JsonPropertyName("romtext")] public string? RomText { get; init; }
    [JsonPropertyName("published")] public string? Published { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }

    public bool IsAvailable => string.Equals(State, "OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>Descriptions are plain text but contain HTML entities such as &amp;quot;.</summary>
    public string PlainDescription => System.Net.WebUtility.HtmlDecode(Description ?? "");
}

internal static class FilehostParsing
{
    public static long? ParseLong(string? s) =>
        long.TryParse(s?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Ratings arrive as "5.0", "5.0 (2)", "-", "" or null.</summary>
    public static decimal? ParseRating(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var head = s.Trim().Split(' ')[0];
        return decimal.TryParse(head, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var r) ? r : null;
    }
}
