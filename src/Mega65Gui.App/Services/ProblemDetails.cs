using Mega65Gui.App.ViewModels;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;

namespace Mega65Gui.App.Services;

/// <summary>
/// Collects what a problem report needs to explain itself: version, page, connection, COM ports,
/// tools and the page's last message. No passwords, tokens, account names or file contents.
/// </summary>
public static class ProblemDetails
{
    public static string Collect(AppServices services, PageViewModel page)
    {
        var items = new List<(string, string?)>
        {
            ("MEGA65 GUI", AppReleaseCheck.Display(AppServices.AppVersion)),
            ("Windows", Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? ", 64-bit" : "")),
            ("Page", page.Title),
            ("Page message", string.IsNullOrWhiteSpace(page.Status) ? null : page.Status),
            ("Connection", services.Connection?.ToString() ?? (services.LostPort is { } lost ? $"disconnected ({lost} has gone)" : "none")),
        };
        if (services.Connection is not null)
        {
            var d = services.Device;
            items.Add(("MEGA65", d.NoResponse ? "not answering" : $"model {d.Model}, core {d.CoreBuild ?? "not read"}, ROM {d.RomVersion ?? "not read"}"));
        }
        var ports = SerialPorts.List();
        items.Add(("COM ports", ports.Count == 0 ? "none" : string.Join("; ", ports.Select(p => p.Label))));
        items.Add(("Device tools", services.Tools.Current is { } t ? $"{t.Version ?? "installed"} ({t.Source})" : "not installed"));
        items.Add(("Filehost", services.Session.IsSignedIn ? "signed in" : "not signed in"));

        if (page is UpdateViewModel u)
        {
            items.Add(("SD card", u.CardInDevice ? "in the MEGA65" : $"in a PC card reader ({(string.IsNullOrWhiteSpace(u.SdCardPath) ? "no folder chosen" : u.SdCardPath)})"));
            items.Add(("Model", u.SelectedModel?.Label));
            items.Add(("Include ROM", u.IncludeRom ? "yes" : "no"));
        }

        var lastError = Path.Combine(services.Paths.Reports, "last-error.txt");
        if (File.Exists(lastError) && File.GetLastWriteTime(lastError) > DateTime.Now.AddDays(-7))
        {
            // The first lines say what failed and where; the full stack trace would make the report long.
            var head = File.ReadLines(lastError).Take(4).Select(l => l.Trim());
            items.Add(("Last error", string.Join(" | ", head)));
        }

        return ProblemReport.Scrub(ProblemReport.Lines(items),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);
    }
}
