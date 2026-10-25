namespace Mega65Gui.Core.Filehost;

/// <summary>Finds out whether a newer MEGA65 GUI has been released on GitHub.</summary>
public static class AppReleaseCheck
{
    public const string Repository = "freemchr/mega65gui";

    /// <summary>"v1.2" or "1.1.1" as a version with three parts (1.2.0, 1.1.1); null if the tag is not a version.</summary>
    public static Version? ParseTag(string? tag)
    {
        var t = (tag ?? "").Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out var v) ? Normalise(v) : null;
    }

    /// <summary>Major.minor.build, with missing parts as 0, so 1.2 and 1.2.0.0 compare equal.</summary>
    public static Version Normalise(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>The release to offer: the newest full release, if it is newer than <paramref name="current"/>.</summary>
    public static GitHubRelease? NewerThan(GitHubRelease? latest, Version current) =>
        latest is not null && ParseTag(latest.Tag) is { } v && v > Normalise(current) ? latest : null;

    /// <summary>The short version people see: "1.2" for 1.2.0, "1.1.1" otherwise.</summary>
    public static string Display(Version v)
    {
        var n = Normalise(v);
        return n.Build == 0 ? $"{n.Major}.{n.Minor}" : $"{n.Major}.{n.Minor}.{n.Build}";
    }

    /// <summary>Asks GitHub for the newest full (not pre-) release; null when there is none newer.</summary>
    public static async Task<GitHubRelease?> CheckAsync(GitHubReleases github, Version current, CancellationToken ct = default) =>
        NewerThan(await github.GetLatestAsync(Repository, includePrereleases: false, ct), current);
}
