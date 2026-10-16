using System.Text.Json;
using System.Text.RegularExpressions;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Packages;

namespace Mega65Gui.Core.Tools;

public enum ToolChannel { Release, Development }

/// <summary>Where the official mega65-tools Windows builds are published on the Filehost.</summary>
public static class OfficialToolPackages
{
    public const string WindowsRelease = "06c55815-7826-4ad6-be0e-b8dc5e721b6d";
    public const string WindowsDevelopment = "658322fd-e586-4b4f-a991-89470b269b4a";

    public static string FileIdFor(ToolChannel channel) =>
        channel == ToolChannel.Release ? WindowsRelease : WindowsDevelopment;
}

public sealed record ToolInstallation(string Folder, IReadOnlyDictionary<string, string> Executables, string? Version,
    string Source, DateTimeOffset InstalledAt)
{
    public string? PathOf(string tool) => Executables.TryGetValue(tool, out var p) ? p : null;
    public bool Has(string tool) => Executables.ContainsKey(tool);
}

/// <summary>
/// Installs and locates the official MEGA65 command-line tools (GPL-3.0). They are run as
/// separate processes and never linked into this application.
/// </summary>
public sealed partial class ToolManager(AppPaths paths, IProcessRunner runner)
{
    public static readonly string[] KnownTools = ["m65", "mega65_ftp", "etherload", "romdiff", "bit2core", "bit2mcs"];

    private string StateFile => Path.Combine(paths.Tools, "installed.json");

    public ToolInstallation? Current { get; private set; }

    public ToolInstallation? Load()
    {
        if (!File.Exists(StateFile)) return null;
        var t = JsonSerializer.Deserialize<ToolInstallation>(File.ReadAllText(StateFile));
        Current = t is not null && t.Executables.Values.All(File.Exists) ? t : null;
        return Current;
    }

    /// <summary>Uses an existing folder of tools (for example one the user already has).</summary>
    public async Task<ToolInstallation> UseFolderAsync(string folder, CancellationToken ct = default)
    {
        var exes = Locate(folder);
        if (!exes.ContainsKey("m65") && !exes.ContainsKey("mega65_ftp"))
            throw new FileNotFoundException("No m65 or mega65_ftp executable was found in that folder.");
        var install = new ToolInstallation(folder, exes, await ReadVersionAsync(exes, ct), $"Folder: {folder}", DateTimeOffset.Now);
        Save(install);
        return install;
    }

    public async Task<ToolInstallation> InstallFromFilehostAsync(CatalogueService catalogue, DownloadManager downloads,
        ToolChannel channel, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (catalogue.Current is null) await catalogue.LoadAsync(ct: ct);
        var fileId = OfficialToolPackages.FileIdFor(channel);
        var versions = await catalogue.GetVersionsAsync(fileId, ct: ct);
        var latest = versions.Where(v => v.IsActive).OrderByDescending(v => v.VersionNumber).FirstOrDefault()
                     ?? throw new InvalidOperationException("The Filehost lists no active tools package.");

        progress?.Report($"Downloading {latest.FileName}");
        var manifest = await downloads.DownloadAsync(latest, null, ct);
        var folder = Path.Combine(paths.Tools, latest.VersionId);
        progress?.Report("Extracting tools");
        await PackageExtractor.ExtractAsync(downloads.PathOf(manifest), folder, ct);

        var exes = Locate(folder);
        if (exes.Count == 0) throw new InvalidDataException("The tools package contained no recognised executables.");
        var install = new ToolInstallation(folder, exes, await ReadVersionAsync(exes, ct),
            $"Filehost {channel}: {latest.FileName} (sha256 {manifest.Sha256})", DateTimeOffset.Now);
        Save(install);
        return install;
    }

    internal static Dictionary<string, string> Locate(string folder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var ext = Path.GetExtension(file);
            if (!KnownTools.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (OperatingSystem.IsWindows() ? !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) : ext.Length > 0) continue;
            result.TryAdd(name.ToLowerInvariant(), file);
        }
        return result;
    }

    // The tools print e.g. "Version: 20261005.14-develo-44c63d2" in their help text.
    [GeneratedRegex(@"Version:\s*(?<v>\S+)")]
    private static partial Regex VersionLine();

    internal static string? ParseVersion(string text) => VersionLine().Match(text) is { Success: true } m ? m.Groups["v"].Value : null;

    private async Task<string?> ReadVersionAsync(IReadOnlyDictionary<string, string> exes, CancellationToken ct)
    {
        var exe = exes.GetValueOrDefault("m65") ?? exes.GetValueOrDefault("mega65_ftp");
        if (exe is null) return null;
        try
        {
            var r = await runner.RunAsync(exe, ["-h"], TimeSpan.FromSeconds(10), null, ct);
            return ParseVersion(r.StdOut + r.StdErr);
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private void Save(ToolInstallation install)
    {
        File.WriteAllText(StateFile, JsonSerializer.Serialize(install));
        Current = install;
    }
}
