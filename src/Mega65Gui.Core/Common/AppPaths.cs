namespace Mega65Gui.Core.Common;

/// <summary>Per-user storage locations. Everything lives under one root so tests can redirect it.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
    }

    /// <summary>%LOCALAPPDATA%\Mega65Gui, or the folder in MEGA65GUI_HOME (used for testing with a clean profile).</summary>
    public static AppPaths Default() => new(
        Environment.GetEnvironmentVariable("MEGA65GUI_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mega65Gui"));

    public string Root { get; }
    public string CatalogueCache => Ensure("cache", "catalogue");
    public string Downloads => Ensure("cache", "downloads");
    public string Extracted => Ensure("cache", "extracted");
    public string Tools => Ensure("tools");
    public string Backups => Ensure("backups");
    public string Reports => Ensure("reports");
    /// <summary>The Core Manager's library: imported core files, artwork and the metadata index.</summary>
    public string CoreLibrary => Ensure("library", "cores");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    private string Ensure(params string[] parts)
    {
        var p = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }
}
