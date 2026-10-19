using System.Text.RegularExpressions;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.Core.Cores;

/// <summary>A downloadable FPGA core on the Filehost.</summary>
public sealed record FilehostCore(FilehostFile File, string Kind, IReadOnlyList<HardwareModel> NamedModels, string? VersionHint)
{
    public string Title => File.Title;
    public bool IsOfficial => string.Equals(File.Author?.Trim(), "MEGA65", StringComparison.OrdinalIgnoreCase);
}

public sealed record FilehostCoreQuery(string? Text = null, string? Kind = null, HardwareModel? Model = null, bool OfficialOnly = false);

/// <summary>
/// Picks the FPGA cores out of the Filehost catalogue. The Filehost files cores under category
/// "Firmware" with type "Alternate Core", "Arcade Core" or (official MEGA65 cores) "Release
/// Package". Boards named in a title or file name are only hints: the board a core is really
/// built for is read from its .cor header after download.
/// </summary>
public static partial class FilehostCores
{
    public static readonly string[] Kinds = ["Alternate Core", "Arcade Core", "Release Package"];

    public static bool IsCore(FilehostFile f) =>
        string.Equals(f.Category, "Firmware", StringComparison.OrdinalIgnoreCase)
        && Kinds.Contains(f.Type?.Trim(), StringComparer.OrdinalIgnoreCase);

    public static FilehostCore Describe(FilehostFile f) =>
        new(f, Kinds.FirstOrDefault(k => string.Equals(k, f.Type?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? f.Type ?? "",
            NamedModels(f), VersionHint(f.Title) ?? VersionHint(f.FileName));

    private static IReadOnlyList<HardwareModel> NamedModels(FilehostFile f)
    {
        var named = CoreTargets.ModelsNamedIn(f.Title, f.FileName, f.Search).ToList();
        // Release package titles name their target in brackets, e.g. "(mega65r6)".
        if (OfficialReleases.TargetFromTitle(f.Title) is { } t && CoreTargets.ModelOf(t) is { } m && !named.Contains(m)) named.Add(m);
        return named.Order().ToList();
    }

    // "V5.2", "v1.0", "_0.5.1_", "1.95"
    [GeneratedRegex(@"(?<![0-9.])v?(?<v>\d+(?:\.\d+){1,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex VersionToken();

    public static string? VersionHint(string? text) =>
        text is not null && VersionToken().Match(text) is { Success: true } m ? m.Groups["v"].Value : null;

    public static IEnumerable<FilehostCore> Search(IEnumerable<FilehostFile> catalogue, FilehostCoreQuery q)
    {
        var cores = catalogue.Where(IsCore).Select(Describe);
        if (q.OfficialOnly) cores = cores.Where(c => c.IsOfficial);
        if (!string.IsNullOrWhiteSpace(q.Kind)) cores = cores.Where(c => string.Equals(c.Kind, q.Kind, StringComparison.OrdinalIgnoreCase));
        if (q.Model is { } model) cores = cores.Where(c => c.NamedModels.Contains(model));
        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var terms = q.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            cores = cores.Where(c => terms.All(t =>
                c.Title.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (c.File.Author?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.File.FileName?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.VersionHint?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.File.Search?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)));
        }
        return cores.OrderByDescending(c => c.File.Updated);
    }

    /// <summary>
    /// True when the library holds an older version of this Filehost file: the catalogue's
    /// current version differs from every version in the library. Whether the new version suits a
    /// given MEGA65 is only known once it is downloaded and its header read.
    /// </summary>
    public static bool HasNewerVersion(FilehostFile file, IEnumerable<CoreLibraryEntry> library)
    {
        var fromFile = library.Where(e => e.Origin.FilehostFileId == file.FileId).ToList();
        return fromFile.Count > 0 && file.VersionId is { Length: > 0 } current && fromFile.All(e => e.Origin.FilehostVersionId != current);
    }

    public static bool IsInLibrary(FilehostFile file, IEnumerable<CoreLibraryEntry> library) =>
        library.Any(e => e.Origin.FilehostFileId == file.FileId && e.Origin.FilehostVersionId == file.VersionId);
}
