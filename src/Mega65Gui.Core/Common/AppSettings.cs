using System.Text.Json;

namespace Mega65Gui.Core.Common;

/// <summary>App-wide preferences, kept in settings.json.</summary>
public sealed class AppSettings(string file)
{
    private sealed record Stored(bool CheckForNewVersions = true);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Look for a newer MEGA65 GUI release on GitHub when the app starts.</summary>
    public bool CheckForNewVersions { get; set; } = true;

    public void Load()
    {
        try
        {
            if (!File.Exists(file)) return;
            var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file));
            CheckForNewVersions = s?.CheckForNewVersions ?? true;
        }
        catch (Exception e) when (e is JsonException or IOException) { }
    }

    public void Save() => File.WriteAllText(file, JsonSerializer.Serialize(new Stored(CheckForNewVersions), Options));
}
