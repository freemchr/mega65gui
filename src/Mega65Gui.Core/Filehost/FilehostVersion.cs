using System.Globalization;
using System.Text.Json.Serialization;

namespace Mega65Gui.Core.Filehost;

/// <summary>
/// One row of https://files.mega65.org/php/readversionlistpublic.php. Every field is
/// returned as a string; the typed accessors below parse it.
/// </summary>
public sealed record FilehostVersionRow
{
    [JsonPropertyName("fileid")] public required string FileId { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("versionid")] public required string VersionId { get; init; }
    [JsonPropertyName("filename")] public required string FileName { get; init; }
    [JsonPropertyName("size")] public string? Size { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
    [JsonPropertyName("published")] public string? Published { get; init; }
    [JsonPropertyName("active")] public string? Active { get; init; }
    [JsonPropertyName("location")] public string? Location { get; init; }

    public bool IsActive => string.Equals(Active, "Y", StringComparison.OrdinalIgnoreCase);

    public long? SizeBytes => long.TryParse(Size, NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null;

    public DateOnly? PublishedDate =>
        DateOnly.TryParseExact(Published, ["d MMM yyyy", "dd MMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;

    public decimal? VersionNumber =>
        decimal.TryParse(Version, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) ? v : null;
}
