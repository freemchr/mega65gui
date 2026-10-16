using System.Globalization;
using System.Text.RegularExpressions;
using Mega65Gui.Core.Device;

namespace Mega65Gui.Core.Sd;

/// <summary>A file in the root of the MEGA65's SD card as listed by mega65_ftp "dir".</summary>
public sealed record RemoteEntry(string ShortName, string LongName, long Size, bool IsDirectory)
{
    public string Name => LongName.Length > 0 ? LongName : ShortName;
    public bool Matches(string fileName) =>
        string.Equals(ShortName, fileName, StringComparison.OrdinalIgnoreCase) || string.Equals(LongName, fileName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The SD card inside the MEGA65, reached over USB/JTAG or Ethernet.</summary>
public interface IRemoteCard
{
    string Description { get; }
    Task<IReadOnlyList<RemoteEntry>> ListRootAsync(CancellationToken ct);
    /// <summary>Downloads root files into <paramref name="localDir"/> under the given names.</summary>
    Task DownloadAsync(IReadOnlyList<string> names, string localDir, IProgress<string>? log, CancellationToken ct);
    /// <summary>Uploads files from <paramref name="localDir"/> to the card root, replacing existing ones.</summary>
    Task UploadAsync(string localDir, IReadOnlyList<string> names, IProgress<string>? log, CancellationToken ct);
}

/// <summary>IRemoteCard over the official mega65_ftp tool.</summary>
public sealed partial class Mega65FtpCard(Mega65Commands commands, ConnectionTarget target) : IRemoteCard
{
    public string Description => $"SD card in the MEGA65 via {target}";

    public async Task<IReadOnlyList<RemoteEntry>> ListRootAsync(CancellationToken ct)
    {
        var r = await commands.FtpAsync(target, Path.GetTempPath(), null, ct, "dir");
        var entries = ParseDir(r.StdOut + "\n" + r.StdErr);
        if (!r.Succeeded || !(r.StdOut + r.StdErr).Contains("File(s)", StringComparison.Ordinal))
            throw new IOException($"Could not list the MEGA65's SD card (mega65_ftp exit code {r.ExitCode}). Is the MEGA65 switched on and running its core?");
        return entries;
    }

    public async Task DownloadAsync(IReadOnlyList<string> names, string localDir, IProgress<string>? log, CancellationToken ct)
    {
        if (names.Count == 0) return;
        Directory.CreateDirectory(localDir);
        var r = await commands.FtpAsync(target, localDir, log, ct, names.Select(n => $"get {n}").ToArray());
        if (!r.Succeeded) throw new IOException($"Downloading from the MEGA65's SD card failed (exit code {r.ExitCode}).");
        var missing = names.Where(n => !File.Exists(Path.Combine(localDir, n))).ToList();
        if (missing.Count > 0) throw new IOException($"These files did not download: {string.Join(", ", missing)}.");
    }

    public async Task UploadAsync(string localDir, IReadOnlyList<string> names, IProgress<string>? log, CancellationToken ct)
    {
        if (names.Count == 0) return;
        var r = await commands.FtpAsync(target, localDir, log, ct, names.Select(n => $"put {n}").ToArray());
        if (!r.Succeeded) throw new IOException($"Uploading to the MEGA65's SD card failed (exit code {r.ExitCode}).");
    }

    // "     25333 | 2024-06-30 17:55:16  | AUDIOMIX.M65 | " or "<DIR> | … | CPC4ME~1 | cpc4mega65"
    // Horizontal whitespace only: an empty long-name column must not swallow the next line.
    [GeneratedRegex(@"^[ \t]*(?<size>\d+|<DIR>)[ \t]*\|[ \t]*(?<date>[^|\n]*)\|[ \t]*(?<short>[^|\n]*?)[ \t]*\|[ \t]*(?<long>[^|\n]*?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex DirLine();

    internal static IReadOnlyList<RemoteEntry> ParseDir(string output) =>
        DirLine().Matches(output.Replace("\r", ""))
            .Select(m => new RemoteEntry(
                m.Groups["short"].Value,
                m.Groups["long"].Value,
                m.Groups["size"].Value == "<DIR>" ? 0 : long.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture),
                m.Groups["size"].Value == "<DIR>"))
            .Where(e => e.ShortName.Length > 0)
            .ToList();
}
