namespace Mega65Gui.Core.Common;

/// <summary>Per-user storage locations. Everything lives under one root so tests can redirect it.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
    }

    public static AppPaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mega65Gui"));

    public string Root { get; }
    public string CatalogueCache => Ensure("cache", "catalogue");
    public string Downloads => Ensure("cache", "downloads");
    public string Extracted => Ensure("cache", "extracted");
    public string Tools => Ensure("tools");
    public string Backups => Ensure("backups");
    public string Reports => Ensure("reports");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    private string Ensure(params string[] parts)
    {
        var p = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }
}
