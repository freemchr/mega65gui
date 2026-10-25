using System.Text;

namespace Mega65Gui.Core.Common;

/// <summary>
/// The app details attached to a problem report, and the GitHub "new issue" link that carries
/// them. Nothing is sent: the link opens in the browser, where the user reads, edits and posts it.
/// </summary>
public static class ProblemReport
{
    /// <summary>GitHub refuses very long links; the details are cut to stay well inside that.</summary>
    public const int MaxDetailsInLink = 5000;

    /// <summary>Replaces the Windows profile folder and the user name, so the report does not name the person.</summary>
    public static string Scrub(string text, string? profileFolder, string? userName)
    {
        if (!string.IsNullOrEmpty(profileFolder)) text = text.Replace(profileFolder, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (userName is { Length: >= 3 }) text = text.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>The issue text: questions for the user to answer, then the details.</summary>
    public static string IssueBody(string details) =>
        $"""
        **What were you trying to do?**


        **What happened instead?**


        <details><summary>App details (added by MEGA65 GUI)</summary>

        ```
        {details.TrimEnd()}
        ```
        </details>
        """;

    /// <summary>A link that opens GitHub's new-issue form with the title and body filled in.</summary>
    public static string IssueUrl(string repositoryUrl, string title, string details)
    {
        if (details.Length > MaxDetailsInLink) details = details[..MaxDetailsInLink] + "\n… (cut; the full details were copied to the clipboard)";
        return $"{repositoryUrl.TrimEnd('/')}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(IssueBody(details))}";
    }

    /// <summary>"Name: value" lines with the names lined up.</summary>
    public static string Lines(IEnumerable<(string Name, string? Value)> items)
    {
        var list = items.ToList();
        var width = list.Count == 0 ? 0 : list.Max(i => i.Name.Length);
        var sb = new StringBuilder();
        foreach (var (name, value) in list) sb.Append(name.PadRight(width)).Append("  ").AppendLine(value ?? "-");
        return sb.ToString();
    }
}
