using System.Text.RegularExpressions;

namespace Mega65Gui.Core.Filehost;

/// <summary>
/// Release metadata parsed from an official core release package. The Filehost "version"
/// field is only a per-file revision counter, so the MEGA65 release number has to come from
/// the package file name (or, failing that, the version comment).
/// </summary>
public sealed partial record ReleaseInfo(string Target, Version Release, string? CommitHash, bool IncludesRom)
{
    // mega65r3-release-0.97-aaf4542-norom.7z, mega65r6-release-0.97.2-03b24c6-norom.7z
    [GeneratedRegex(@"^(?<target>mega65r\d+|nexys4ddr-widget|nexys4)-release-(?<ver>\d+(?:\.\d+){1,3})-(?<hash>[0-9a-f]{7,})(?<suffix>-[a-z0-9]+)?\.", RegexOptions.IgnoreCase)]
    private static partial Regex ModernName();

    // MEGA65-R3-Rel_0_9-norom.7z
    [GeneratedRegex(@"^MEGA65-(?<target>R\d+)-Rel_(?<ver>\d+(?:_\d+)+)(?<norom>-norom)?\.", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyName();

    [GeneratedRegex(@"Release\s+(?<ver>\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex CommentVersion();

    public static ReleaseInfo? TryParse(string fileName, string? comment = null)
    {
        var m = ModernName().Match(fileName);
        if (m.Success)
            return new ReleaseInfo(m.Groups["target"].Value.ToLowerInvariant(), Version.Parse(m.Groups["ver"].Value),
                m.Groups["hash"].Value.ToLowerInvariant(),
                !string.Equals(m.Groups["suffix"].Value, "-norom", StringComparison.OrdinalIgnoreCase));

        m = LegacyName().Match(fileName);
        if (m.Success)
            return new ReleaseInfo("mega65" + m.Groups["target"].Value.ToLowerInvariant(),
                Version.Parse(m.Groups["ver"].Value.Replace('_', '.')), null, !m.Groups["norom"].Success);

        if (comment is not null && CommentVersion().Match(comment) is { Success: true } c)
            return new ReleaseInfo("unknown", Version.Parse(c.Groups["ver"].Value), null, !fileName.Contains("norom", StringComparison.OrdinalIgnoreCase));

        return null;
    }
}

public static class OfficialReleases
{
    /// <summary>
    /// An official stable core release package: published by the MEGA65 account as
    /// Firmware / Release Package and not a development build.
    /// </summary>
    public static bool IsStableCorePackage(FilehostFile f) =>
        string.Equals(f.Author?.Trim(), "MEGA65", StringComparison.OrdinalIgnoreCase)
        && string.Equals(f.Category, "Firmware", StringComparison.OrdinalIgnoreCase)
        && string.Equals(f.Type, "Release Package", StringComparison.OrdinalIgnoreCase)
        && f.Title.Contains("Release Package", StringComparison.OrdinalIgnoreCase)
        && !f.Title.Contains("Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>Target board named in the title, e.g. "MEGA65 Core Release Package (mega65r6) no ROM" gives "mega65r6".</summary>
    public static string? TargetFromTitle(string title)
    {
        var open = title.IndexOf('(');
        var close = open >= 0 ? title.IndexOf(')', open) : -1;
        return open >= 0 && close > open ? title[(open + 1)..close].Trim().ToLowerInvariant() : null;
    }
}
