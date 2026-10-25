using Mega65Gui.Core.Common;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Packages;
using Mega65Gui.Core.Sd;

namespace Mega65Gui.Core.Cores;

/// <summary>A folder in a core's package that belongs on the SD card, such as Vixen-32's "vixen-32".</summary>
public sealed record SdFolder(string Name, string LocalPath, int FileCount, long Bytes);

/// <summary>
/// Finds the SD-card folders in a core package. Many cores read their own files from the card
/// (Vixen-32 needs /VIXEN-32/BOOT.BIN); the package ships them as a folder to copy to the card's
/// root unchanged. A candidate is a folder at the package's top level (inside a single wrapping
/// folder, if there is one) that holds files but no core, archive or source code. The user sees
/// and approves the exact file list before anything is copied.
/// </summary>
public static class CorePackageFiles
{
    private static readonly string[] NotForCard = ["source", "src", "doc", "docs", "documentation", "__macosx"];

    public static IReadOnlyList<SdFolder> FindSdFolders(string extracted)
    {
        var top = extracted;
        // Unwrap "Vixen-32_Beta2_v0.76/…" when the archive has one folder and no files at its top.
        while (Directory.GetFiles(top).Length == 0 && Directory.GetDirectories(top) is [var only]) top = only;
        return Directory.GetDirectories(top)
            .Where(d => !NotForCard.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            .Select(d => (Dir: d, Files: Directory.GetFiles(d, "*", SearchOption.AllDirectories)))
            .Where(x => x.Files.Length > 0 && !x.Files.Any(f =>
                Path.GetExtension(f).ToLowerInvariant() is ".cor" or ".bit" or ".vhd" or ".vhdl" or ".sv" || PackageExtractor.IsArchive(f)))
            .Select(x => new SdFolder(Path.GetFileName(x.Dir), x.Dir, x.Files.Length, x.Files.Sum(f => new FileInfo(f).Length)))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>The SD card inside the MEGA65, reached by folder path ("" is the root, "vixen-32/img" a sub-folder).</summary>
public interface ICardTree
{
    string Description { get; }
    /// <summary>The folder's entries, or null when the folder does not exist.</summary>
    Task<IReadOnlyList<RemoteEntry>?> ListAsync(string dir, CancellationToken ct);
    Task MakeDirectoryAsync(string parent, string name, CancellationToken ct);
    Task UploadAsync(string localPath, string dir, string name, CancellationToken ct);
    Task DownloadAsync(string dir, RemoteEntry entry, string localPath, CancellationToken ct);
}

/// <summary>
/// Copies a folder tree to the card inside the MEGA65, keeping its layout. Files of the same name
/// already on the card are downloaded first (comparison and backup); only approved files are
/// uploaded; every upload is read back and compared by SHA-256. Nothing is deleted.
/// </summary>
public sealed class CardTreeSync(ICardTree card)
{
    public async Task<(SyncPlan Plan, string BackupDir)> PlanAsync(string stageRoot, string backupRoot, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var backupDir = Path.Combine(backupRoot, $"sdcard-remote-{DateTime.Now:yyyyMMdd-HHmmss}");
        var listings = new Dictionary<string, IReadOnlyList<RemoteEntry>?>(StringComparer.OrdinalIgnoreCase);
        var items = new List<SyncItem>();
        foreach (var file in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(stageRoot, file).Replace('\\', '/');
            var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
            if (!listings.TryGetValue(dir, out var listing))
            {
                log?.Report($"Listing /{dir} on the card");
                listings[dir] = listing = await ListIfParentExists(dir, listings, ct);
            }
            var hash = await Hashing.Sha256FileAsync(file, ct);
            var length = new FileInfo(file).Length;
            var existing = listing?.FirstOrDefault(e => !e.IsDirectory && e.Matches(Path.GetFileName(file)));
            if (existing is null) { items.Add(new SyncItem(rel, SyncAction.Add, length, null, hash, null)); continue; }

            var backup = Path.Combine(backupDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            log?.Report($"Downloading the card's {rel} to compare and back up");
            await card.DownloadAsync(dir, existing, backup, ct);
            var cardHash = File.Exists(backup) ? await Hashing.Sha256FileAsync(backup, ct) : null;
            items.Add(new SyncItem(rel, cardHash == hash ? SyncAction.Unchanged : SyncAction.Replace, length, existing.Size, hash, cardHash));
        }
        return (new SyncPlan(stageRoot, card.Description, items, [], DateTimeOffset.Now), backupDir);
    }

    /// <summary>A folder whose parent is missing cannot exist, so the card is not asked about it.</summary>
    private async Task<IReadOnlyList<RemoteEntry>?> ListIfParentExists(string dir, Dictionary<string, IReadOnlyList<RemoteEntry>?> known, CancellationToken ct)
    {
        var parent = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : dir.Length > 0 ? "" : null;
        if (parent is not null)
        {
            if (!known.TryGetValue(parent, out var up)) known[parent] = up = await ListIfParentExists(parent, known, ct);
            var name = dir[(dir.LastIndexOf('/') + 1)..];
            if (up is null || !up.Any(e => e.IsDirectory && e.Matches(name))) return null;
        }
        return await card.ListAsync(dir, ct);
    }

    public async Task<SyncResult> ApplyAsync(SyncPlan plan, IReadOnlySet<string> approved, string backupDir, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var results = plan.Changes.Where(c => !approved.Contains(c.RelativePath))
            .Select(c => new SyncItemResult(c, ItemOutcome.Skipped, null, "Not approved")).ToList();
        var toWrite = plan.Changes.Where(c => approved.Contains(c.RelativePath)).ToList();
        var made = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        var verifyDir = Directory.CreateTempSubdirectory("m65verify").FullName;
        try
        {
            foreach (var item in toWrite)
            {
                ct.ThrowIfCancellationRequested();
                var dir = Path.GetDirectoryName(item.RelativePath)?.Replace('\\', '/') ?? "";
                var name = Path.GetFileName(item.RelativePath);
                try
                {
                    await EnsureFolderAsync(dir, made, log, ct);
                    log?.Report($"Uploading {item.RelativePath} ({item.PackageSize:N0} bytes)");
                    await card.UploadAsync(Path.Combine(plan.PackageRoot, item.RelativePath), dir, name, ct);
                    var listing = await card.ListAsync(dir, ct);
                    var entry = listing?.FirstOrDefault(e => !e.IsDirectory && e.Matches(name)) ?? throw new IOException("The file is not on the card after uploading.");
                    var back = Path.Combine(verifyDir, Guid.NewGuid().ToString("N"));
                    await card.DownloadAsync(dir, entry, back, ct);
                    var ok = File.Exists(back) && await Hashing.Sha256FileAsync(back, ct) == item.PackageSha256;
                    results.Add(new SyncItemResult(item, ok ? ItemOutcome.Written : ItemOutcome.Failed,
                        item.Action == SyncAction.Replace ? Path.Combine(backupDir, item.RelativePath) : null,
                        ok ? "Verified on the card." : "Read-back did not match the package."));
                }
                catch (IOException e) { results.Add(new SyncItemResult(item, ItemOutcome.Failed, null, e.Message)); }
            }
        }
        finally { Directory.Delete(verifyDir, recursive: true); }
        return new SyncResult(plan, backupDir, results);
    }

    private async Task EnsureFolderAsync(string dir, HashSet<string> made, IProgress<string>? log, CancellationToken ct)
    {
        if (made.Contains(dir)) return;
        var parent = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
        await EnsureFolderAsync(parent, made, log, ct);
        var name = dir[(dir.LastIndexOf('/') + 1)..];
        var listing = await card.ListAsync(parent, ct);
        if (listing is null || !listing.Any(e => e.IsDirectory && e.Matches(name)))
        {
            log?.Report($"Creating folder /{dir} on the card");
            await card.MakeDirectoryAsync(parent, name, ct);
        }
        made.Add(dir);
    }
}

/// <summary>ICardTree over an interactive mega65_ftp session. Folders are entered by their 8.3 names, as the SD Card Browser does.</summary>
public sealed class FtpCardTree(FtpSession session, string description) : ICardTree
{
    public string Description => description;

    // The folder reached last, and the prompt's path there: many files in one folder need only one walk.
    private (string Dir, string Prompt)? _at;

    private async Task<bool> GoToAsync(string dir, CancellationToken ct)
    {
        if (_at is { } at && string.Equals(at.Dir, dir, StringComparison.OrdinalIgnoreCase) && at.Prompt == session.CurrentDirectory) return true;
        _at = null;
        for (var i = 0; i < 32 && session.CurrentDirectory is not ("/" or ""); i++)
            Check(await session.ChangeDirectoryAsync("..", ct), "cd ..");
        foreach (var part in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = (await session.ListAsync(ct)).FirstOrDefault(e => e.IsDirectory && e.Matches(part));
            if (entry is null) return false;
            Check(await session.ChangeDirectoryAsync(entry.ShortName, ct), $"cd {part}");
        }
        _at = (dir, session.CurrentDirectory);
        return true;
    }

    /// <summary>Goes to a folder (the SD Card Browser's own after a copy); false when it is not on the card.</summary>
    public Task<bool> OpenAsync(string dir, CancellationToken ct = default) => GoToAsync(dir, ct);

    /// <summary>Tells the tree the session is already in <paramref name="dir"/>, so copying there needs no walk from the root.</summary>
    public void AlreadyIn(string dir) => _at = (dir, session.CurrentDirectory);

    public async Task<IReadOnlyList<RemoteEntry>?> ListAsync(string dir, CancellationToken ct) =>
        await GoToAsync(dir, ct) ? await session.ListAsync(ct) : null;

    public async Task MakeDirectoryAsync(string parent, string name, CancellationToken ct)
    {
        if (!await GoToAsync(parent, ct)) throw new IOException($"The folder /{parent} is not on the card.");
        Check(await session.MakeDirectoryAsync(name, ct), $"mkdir {name}");
    }

    public async Task UploadAsync(string localPath, string dir, string name, CancellationToken ct)
    {
        if (!await GoToAsync(dir, ct)) throw new IOException($"The folder /{dir} is not on the card.");
        Check(await session.UploadAsync(localPath, name, ct), $"put {name}");
    }

    public async Task DownloadAsync(string dir, RemoteEntry entry, string localPath, CancellationToken ct)
    {
        if (!await GoToAsync(dir, ct)) throw new IOException($"The folder /{dir} is not on the card.");
        Check(await session.DownloadAsync(entry.ShortName, localPath, ct), $"get {entry.Name}");
    }

    private static void Check(FtpReply r, string what)
    {
        if (r.Failed) throw new IOException($"mega65_ftp {what} failed: {string.Join(" ", r.Errors)}");
    }
}
