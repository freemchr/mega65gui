using Mega65Gui.Core.Common;

namespace Mega65Gui.Core.Sd;

public enum ItemOutcome { Written, Skipped, Failed }

public sealed record SyncItemResult(SyncItem Item, ItemOutcome Outcome, string? BackupPath, string? Message);

public sealed record SyncResult(SyncPlan Plan, string? BackupDirectory, IReadOnlyList<SyncItemResult> Results)
{
    public bool AllSucceeded => Results.All(r => r.Outcome != ItemOutcome.Failed);
}

/// <summary>
/// Applies only the items the user approved. Every replaced file is backed up first, each write
/// goes to a temporary file that is hash-verified before it takes the original's place, and
/// nothing is ever deleted from the card.
/// </summary>
public sealed class SdSyncExecutor(IContiguityChecker? contiguity = null)
{
    public async Task<SyncResult> ApplyAsync(SyncPlan plan, IReadOnlySet<string> approvedPaths, string backupRoot,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<SyncItemResult>();
        string? backupDir = null;

        foreach (var item in plan.Changes)
        {
            ct.ThrowIfCancellationRequested();
            if (!approvedPaths.Contains(item.RelativePath))
            {
                results.Add(new SyncItemResult(item, ItemOutcome.Skipped, null, "Not approved"));
                continue;
            }

            var src = Path.Combine(plan.PackageRoot, item.RelativePath);
            var dst = ResolveTargetPath(plan.TargetRoot, item.RelativePath);
            string? backupPath = null;
            try
            {
                if (item.Action == SyncAction.Replace && File.Exists(dst))
                {
                    backupDir ??= Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    backupPath = Path.Combine(backupDir, item.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    progress?.Report($"Backing up {item.RelativePath}");
                    File.Copy(dst, backupPath, overwrite: false);
                }

                progress?.Report($"Writing {item.RelativePath}");
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                var tmp = dst + ".m65tmp";
                File.Copy(src, tmp, overwrite: true);
                var written = await Hashing.Sha256FileAsync(tmp, ct);
                if (!string.Equals(written, item.PackageSha256, StringComparison.Ordinal))
                {
                    File.Delete(tmp);
                    results.Add(new SyncItemResult(item, ItemOutcome.Failed, backupPath, "Verification failed: written data does not match the package."));
                    continue;
                }
                File.Move(tmp, dst, overwrite: true);

                string? note = null;
                if (item.RequiresContiguity && contiguity is not null)
                {
                    var frags = contiguity.CountFragments(dst);
                    note = frags switch
                    {
                        null => "Contiguity could not be checked.",
                        1 => "Verified contiguous.",
                        _ => $"WARNING: file is in {frags} fragments; the MEGA65 cannot mount it. Reformat or defragment the card.",
                    };
                }
                results.Add(new SyncItemResult(item, ItemOutcome.Written, backupPath, note));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                results.Add(new SyncItemResult(item, ItemOutcome.Failed, backupPath, e.Message));
            }
        }

        return new SyncResult(plan, backupDir, results);
    }

    /// <summary>Reuse the existing on-card casing for a path if a case-insensitive match exists.</summary>
    internal static string ResolveTargetPath(string targetRoot, string relativePath)
    {
        var current = targetRoot;
        foreach (var part in relativePath.Split('/'))
        {
            var match = Directory.Exists(current)
                ? Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase))
                : null;
            current = match ?? Path.Combine(current, part);
        }
        return current;
    }
}
