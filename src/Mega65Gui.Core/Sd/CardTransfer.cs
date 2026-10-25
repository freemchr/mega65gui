using System.Globalization;
using System.Text.RegularExpressions;
using Mega65Gui.Core.Cores;

namespace Mega65Gui.Core.Sd;

/// <summary>What to do when a file of the same name is already there.</summary>
public enum OverwriteChoice { Replace, Skip, ReplaceAll, SkipAll, Cancel }

public sealed record TransferProgress(int FilesDone, int FileCount, long BytesDone, long Bytes, string Current)
{
    public double Fraction => Bytes > 0 ? (double)BytesDone / Bytes : FileCount > 0 ? (double)FilesDone / FileCount : 0;
}

/// <summary>Skipped: the user kept the file that was there. Failed: the copy went wrong (see Problems).</summary>
public sealed record TransferResult(int Copied, int Skipped, int FoldersMade, bool Stopped, IReadOnlyList<string> Problems, int Failed = 0);

/// <summary>
/// Copies files and whole folders between the PC and the SD card inside the MEGA65, asking
/// before replacing anything. The whole job is listed first so progress can be shown. Stopping
/// takes effect between files: a file that is being copied is always finished, so the
/// mega65_ftp session is never left half way through a command.
/// </summary>
public sealed class CardTransfer(ICardTree card)
{
    /// <summary>Upper-case the names written to the card (files and folders).</summary>
    public bool UpperCaseNames { get; init; }
    /// <summary>Asked for each clash until the user picks "all"; no handler means replace.</summary>
    public Func<string, Task<OverwriteChoice>>? AskOverwrite { get; init; }
    public IProgress<TransferProgress>? Progress { get; init; }

    private bool? _all;

    private sealed record Item(string Dir, string Name, string LocalPath, long Size, RemoteEntry? Remote);

    private enum Outcome { Copied, Skipped, Failed }

    // ---------------- PC → card ----------------

    /// <summary>Copies PC files and folders into the card folder <paramref name="dest"/> ("" is the root).</summary>
    public async Task<TransferResult> UploadAsync(IReadOnlyList<string> localPaths, string dest, CancellationToken stop = default)
    {
        var folders = new List<(string Parent, string Name)>();
        var files = new List<Item>();
        foreach (var p in localPaths)
        {
            if (Directory.Exists(p)) PlanFolder(new DirectoryInfo(p), dest, folders, files);
            else if (File.Exists(p)) files.Add(new Item(dest, CardName(Path.GetFileName(p)), p, new FileInfo(p).Length, null));
        }

        var problems = new List<string>();
        var listings = new Dictionary<string, IReadOnlyList<RemoteEntry>?>(StringComparer.OrdinalIgnoreCase);
        async Task<IReadOnlyList<RemoteEntry>?> List(string dir)
        {
            if (!listings.TryGetValue(dir, out var l)) listings[dir] = l = await card.ListAsync(dir, CancellationToken.None);
            return l;
        }

        var made = 0;
        foreach (var (parent, name) in folders)
        {
            if (stop.IsCancellationRequested) return new TransferResult(0, 0, made, true, problems);
            var existing = (await List(parent))?.FirstOrDefault(e => e.Matches(name));
            if (existing is { IsDirectory: true }) continue;
            if (existing is not null) { problems.Add($"{Join(parent, name)}: a file of that name is in the way of the folder."); continue; }
            try { await card.MakeDirectoryAsync(parent, name, CancellationToken.None); }
            catch (IOException e) { problems.Add($"{Join(parent, name)}: the folder could not be made ({e.Message})."); continue; }
            listings.Remove(parent);
            listings[Join(parent, name)] = [];
            made++;
        }

        return await CopyAsync(files, made, problems, stop, async f =>
        {
            var listing = await List(f.Dir);
            if (listing is null) { problems.Add($"{Join(f.Dir, f.Name)}: its folder could not be made on the card."); return Outcome.Failed; }
            if (listing.Any(e => e.IsDirectory && e.Matches(f.Name))) { problems.Add($"{Join(f.Dir, f.Name)}: a folder of that name is already on the card."); return Outcome.Failed; }
            if (listing.Any(e => !e.IsDirectory && e.Matches(f.Name)) && !await ReplaceAsync("/" + Join(f.Dir, f.Name))) return Outcome.Skipped;
            return await Do(() => card.UploadAsync(f.LocalPath, f.Dir, f.Name, CancellationToken.None), f, problems);
        });
    }

    private void PlanFolder(DirectoryInfo dir, string parent, List<(string, string)> folders, List<Item> files)
    {
        var name = CardName(dir.Name);
        folders.Add((parent, name));
        var here = Join(parent, name);
        foreach (var f in dir.EnumerateFiles().Where(Visible).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            files.Add(new Item(here, CardName(f.Name), f.FullName, f.Length, null));
        foreach (var d in dir.EnumerateDirectories().Where(Visible).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            PlanFolder(d, here, folders, files);
    }

    // Explorer's own files (desktop.ini, Thumbs.db) and anything hidden stay on the PC.
    private static bool Visible(FileSystemInfo i) => (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

    private string CardName(string name) => UpperCaseNames ? name.ToUpperInvariant() : name;

    // ---------------- card → PC ----------------

    /// <summary>Copies card files and folders from the card folder <paramref name="source"/> into a PC folder.</summary>
    public async Task<TransferResult> DownloadAsync(IReadOnlyList<RemoteEntry> entries, string source, string localDir, CancellationToken stop = default)
    {
        var problems = new List<string>();
        var files = new List<Item>();
        var folders = new List<string>();
        foreach (var e in entries)
        {
            if (stop.IsCancellationRequested) return new TransferResult(0, 0, 0, true, problems);
            if (e.IsDirectory) await PlanRemoteFolderAsync(source, e, Path.Combine(localDir, LocalName(e.Name)), folders, files, problems, stop);
            else files.Add(new Item(source, e.Name, Path.Combine(localDir, LocalName(e.Name)), e.Size, e));
        }
        var made = 0;
        foreach (var f in folders.Where(f => !Directory.Exists(f)))
        {
            try { Directory.CreateDirectory(f); made++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add($"{f}: {e.Message}"); }
        }

        return await CopyAsync(files, made, problems, stop, async f =>
        {
            if (File.Exists(f.LocalPath) && !await ReplaceAsync(f.LocalPath)) return Outcome.Skipped;
            return await Do(() => card.DownloadAsync(f.Dir, f.Remote!, f.LocalPath, CancellationToken.None), f, problems);
        });
    }

    private async Task PlanRemoteFolderAsync(string parent, RemoteEntry folder, string localPath, List<string> folders, List<Item> files,
        List<string> problems, CancellationToken stop)
    {
        // Folders are entered by their 8.3 name, which the card always has.
        var dir = Join(parent, folder.ShortName);
        folders.Add(localPath);
        var listing = await card.ListAsync(dir, CancellationToken.None);
        if (listing is null) { problems.Add($"/{dir}: could not open the folder."); return; }
        foreach (var e in listing.Where(e => !e.IsDirectory).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            files.Add(new Item(dir, e.Name, Path.Combine(localPath, LocalName(e.Name)), e.Size, e));
        foreach (var e in listing.Where(e => e.IsDirectory).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (stop.IsCancellationRequested) return;
            await PlanRemoteFolderAsync(dir, e, Path.Combine(localPath, LocalName(e.Name)), folders, files, problems, stop);
        }
    }

    /// <summary>A card name made safe for Windows.</summary>
    public static string LocalName(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : c)).TrimEnd('.', ' ');
        return safe.Length == 0 ? "_" : safe;
    }

    // ---------------- shared ----------------

    private async Task<TransferResult> CopyAsync(List<Item> files, int made, List<string> problems, CancellationToken stop, Func<Item, Task<Outcome>> copy)
    {
        long total = files.Sum(f => f.Size), done = 0;
        int copied = 0, skipped = 0, failed = 0, n = 0;
        foreach (var f in files)
        {
            if (stop.IsCancellationRequested) return new TransferResult(copied, skipped, made, true, problems, failed);
            Progress?.Report(new TransferProgress(n, files.Count, done, total, f.Name));
            var outcome = await copy(f);
            if (_cancelled) return new TransferResult(copied, skipped, made, true, problems, failed);
            switch (outcome)
            {
                case Outcome.Copied: copied++; break;
                case Outcome.Skipped: skipped++; break;
                default: failed++; break;
            }
            n++;
            done += f.Size;
        }
        Progress?.Report(new TransferProgress(n, files.Count, done, total, ""));
        return new TransferResult(copied, skipped, made, false, problems, failed);
    }

    private bool _cancelled;

    /// <summary>True to replace, false to skip; a "Cancel" answer stops the whole transfer.</summary>
    private async Task<bool> ReplaceAsync(string what)
    {
        if (_all is { } all) return all;
        var choice = AskOverwrite is null ? OverwriteChoice.Replace : await AskOverwrite(what);
        switch (choice)
        {
            case OverwriteChoice.ReplaceAll: _all = true; return true;
            case OverwriteChoice.SkipAll: _all = false; return false;
            case OverwriteChoice.Cancel: _cancelled = true; return false;
            default: return choice == OverwriteChoice.Replace;
        }
    }

    // An IOException is one file going wrong; anything else (mega65_ftp not answering) ends the whole copy.
    private static async Task<Outcome> Do(Func<Task> op, Item f, List<string> problems)
    {
        try { await op(); return Outcome.Copied; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add($"{f.Name}: {e.Message}"); return Outcome.Failed; }
    }

    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;
}

/// <summary>Checks file names before they are copied to the MEGA65's card.</summary>
public static class CardNames
{
    /// <summary>Plain-language warnings for names and files that may cause trouble on the MEGA65; empty when all is well.</summary>
    public static IReadOnlyList<string> Warnings(IEnumerable<(string Name, long Size, bool IsDirectory)> items)
    {
        var w = new List<string>();
        foreach (var (name, size, isDir) in items)
        {
            if (name.Contains('"')) w.Add($"{name}: names with quotes cannot be copied.");
            else if (name.StartsWith('$')) w.Add($"{name}: BASIC reads LOAD \"$…\" as a directory listing, so this file cannot be loaded by name.");
            else if (name.IndexOfAny([',', ':', '=', '*', '?']) >= 0) w.Add($"{name}: commas, colons, equals signs, * and ? cannot be typed in a BASIC LOAD or MOUNT command.");
            if (name.Any(c => c > '~')) w.Add($"{name}: has characters the MEGA65 cannot show, such as accented letters.");
            if (isDir) continue;
            if (size == 0) w.Add($"{name}: is empty (0 bytes).");
            if (Path.GetExtension(name).Equals(".d81", StringComparison.OrdinalIgnoreCase))
            {
                if (size != DiskImages.D81Image.ImageSize) w.Add($"{name}: is {size:N0} bytes, not 819,200, so it is probably not a working D81 disk image.");
                var stem = Path.GetFileNameWithoutExtension(name);
                if (stem.Length > 8 || stem.Contains(' ')) w.Add($"{name}: disk images are easiest to mount with a short name (8 characters or fewer, no spaces), such as GAME.D81.");
            }
        }
        return w;
    }
}

/// <summary>The answer to mega65_ftp's "df": "sdcard totals: 1234 out of 7600 MB free (…)".</summary>
public static partial class CardSpace
{
    [GeneratedRegex(@"(?<free>\d+) out of (?<total>\d+) MB free")]
    private static partial Regex Totals();

    public static (long FreeMb, long TotalMb)? Parse(string output) =>
        Totals().Match(output) is { Success: true } m
            ? (long.Parse(m.Groups["free"].Value, CultureInfo.InvariantCulture), long.Parse(m.Groups["total"].Value, CultureInfo.InvariantCulture))
            : null;
}
