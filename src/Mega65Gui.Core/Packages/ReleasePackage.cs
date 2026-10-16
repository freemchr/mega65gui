namespace Mega65Gui.Core.Packages;

/// <summary>
/// Layout of an extracted official core release package. The cores (.cor/.bit/.mcs) are staged
/// for flashing; the SD-card files are synchronised with the card. The folder name holding the
/// SD files has varied between releases, so it is found by looking for known marker files.
/// </summary>
public sealed record ReleasePackage(string Root, IReadOnlyList<string> CoreFiles, string? SdCardRoot, IReadOnlyList<string> Documents)
{
    // Files that ship in the root of a MEGA65 SD card as part of the release.
    private static readonly string[] SdMarkers = ["FREEZER.M65", "AUDIOMIX.M65", "MEGAINFO.M65", "ONBOARD.M65", "MAKEDISK.M65"];

    public static ReleasePackage Inspect(string root)
    {
        var all = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        var cores = all.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".cor" or ".bit" or ".mcs").ToList();
        var docs = all.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".md" or ".txt" or ".pdf").ToList();

        string? sdRoot = all
            .Where(f => SdMarkers.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        return new ReleasePackage(root, cores, sdRoot, docs);
    }
}
