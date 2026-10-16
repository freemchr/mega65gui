using Mega65Gui.Core.Common;

namespace Mega65Gui.Core.Sd;

/// <summary>
/// Synchronises release files with the SD card inside the MEGA65. The card's current copies
/// are downloaded first (this is both the comparison and the backup); only approved changes
/// are uploaded; every uploaded file is downloaded again and compared byte for byte.
/// Release SD-card files all live in the card's root, which is all this handles.
/// </summary>
public sealed class RemoteSdSync(IRemoteCard card)
{
    public string Description => card.Description;

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(CancellationToken ct) => card.ListRootAsync(ct);

    public async Task<(SyncPlan Plan, string BackupDir)> PlanAsync(string packageRoot, string backupRoot,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (Directory.EnumerateDirectories(packageRoot).Any())
            warnings.Add("The package has subfolders; only files in its top folder are synchronised with the MEGA65's card.");

        var package = Directory.EnumerateFiles(packageRoot).Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        log?.Report("Listing the MEGA65's SD card");
        var listing = await card.ListRootAsync(ct);
        var files = listing.Where(e => !e.IsDirectory).ToList();

        var existing = package.Where(p => files.Any(f => f.Matches(p.Name))).Select(p => p.Name).ToList();
        var backupDir = Path.Combine(backupRoot, $"sdcard-remote-{DateTime.Now:yyyyMMdd-HHmmss}");
        log?.Report($"Downloading {existing.Count} file(s) from the card to compare and back up");
        await card.DownloadAsync(existing, backupDir, log, ct);

        var items = new List<SyncItem>();
        foreach (var p in package)
        {
            var pkgHash = await Hashing.Sha256FileAsync(p.FullName, ct);
            var local = Path.Combine(backupDir, p.Name);
            if (!existing.Contains(p.Name))
            {
                items.Add(new SyncItem(p.Name, SyncAction.Add, p.Length, null, pkgHash, null));
                continue;
            }
            var cardHash = await Hashing.Sha256FileAsync(local, ct);
            items.Add(new SyncItem(p.Name, pkgHash == cardHash ? SyncAction.Unchanged : SyncAction.Replace,
                p.Length, new FileInfo(local).Length, pkgHash, cardHash));
        }
        foreach (var f in files.Where(f => !package.Any(p => f.Matches(p.Name))).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            items.Add(new SyncItem(f.Name, SyncAction.TargetOnly, null, f.Size, null, null));

        return (new SyncPlan(packageRoot, card.Description, items, warnings, DateTimeOffset.Now), backupDir);
    }

    public async Task<SyncResult> ApplyAsync(SyncPlan plan, IReadOnlySet<string> approved, string backupDir,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var toWrite = plan.Changes.Where(c => approved.Contains(c.RelativePath)).ToList();
        var results = plan.Changes.Where(c => !approved.Contains(c.RelativePath))
            .Select(c => new SyncItemResult(c, ItemOutcome.Skipped, null, "Not approved")).ToList();
        if (toWrite.Count == 0) return new SyncResult(plan, null, results);

        log?.Report($"Uploading {toWrite.Count} file(s) to the MEGA65's SD card");
        await card.UploadAsync(plan.PackageRoot, toWrite.Select(c => c.RelativePath).ToList(), log, ct);

        var verifyDir = Directory.CreateTempSubdirectory("m65verify").FullName;
        try
        {
            log?.Report("Reading the files back from the card to verify them");
            await card.DownloadAsync(toWrite.Select(c => c.RelativePath).ToList(), verifyDir, log, ct);
            foreach (var c in toWrite)
            {
                var back = Path.Combine(verifyDir, c.RelativePath);
                var ok = File.Exists(back) && await Hashing.Sha256FileAsync(back, ct) == c.PackageSha256;
                var backup = c.Action == SyncAction.Replace ? Path.Combine(backupDir, c.RelativePath) : null;
                results.Add(new SyncItemResult(c, ok ? ItemOutcome.Written : ItemOutcome.Failed, backup,
                    ok ? "Verified on the card." : "Read-back did not match the release."));
            }
        }
        finally
        {
            Directory.Delete(verifyDir, recursive: true);
        }
        return new SyncResult(plan, backupDir, results);
    }
}
