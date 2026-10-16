using Mega65Gui.Core.Common;

namespace Mega65Gui.Core.Sd;

/// <summary>
/// Compares an extracted release package with the contents of an SD card (or any folder) by
/// size and SHA-256. Paths are matched case-insensitively because MEGA65 cards are FAT32.
/// Planning is read-only.
/// </summary>
public sealed class SdSyncPlanner
{
    private const long Fat32MaxFileSize = uint.MaxValue; // 4 GiB - 1

    public async Task<SyncPlan> PlanAsync(string packageRoot, string targetRoot,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(packageRoot)) throw new DirectoryNotFoundException(packageRoot);
        if (!Directory.Exists(targetRoot)) throw new DirectoryNotFoundException(targetRoot);

        var package = Enumerate(packageRoot);
        var target = Enumerate(targetRoot);
        var items = new List<SyncItem>();
        var warnings = new List<string>();

        foreach (var (rel, pkgFile) in package.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Comparing {rel}");
            var pkgHash = await Hashing.Sha256FileAsync(pkgFile.FullName, ct);

            if (pkgFile.Length > Fat32MaxFileSize)
                warnings.Add($"{rel} is larger than FAT32 allows and cannot be copied to the card.");

            if (!target.TryGetValue(rel, out var tgtFile))
            {
                items.Add(new SyncItem(rel, SyncAction.Add, pkgFile.Length, null, pkgHash, null));
                continue;
            }

            if (tgtFile.Length != pkgFile.Length)
            {
                items.Add(new SyncItem(rel, SyncAction.Replace, pkgFile.Length, tgtFile.Length, pkgHash, null));
                continue;
            }

            var tgtHash = await Hashing.Sha256FileAsync(tgtFile.FullName, ct);
            var action = string.Equals(pkgHash, tgtHash, StringComparison.Ordinal) ? SyncAction.Unchanged : SyncAction.Replace;
            items.Add(new SyncItem(rel, action, pkgFile.Length, tgtFile.Length, pkgHash, tgtHash));
        }

        foreach (var (rel, tgtFile) in target.Where(t => !package.ContainsKey(t.Key)).OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
            items.Add(new SyncItem(rel, SyncAction.TargetOnly, null, tgtFile.Length, null, null));

        var plan = new SyncPlan(packageRoot, targetRoot, items, warnings, DateTimeOffset.Now);

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetRoot));
            if (root is not null)
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < plan.BytesToWrite)
                    warnings.Add($"Not enough free space: {plan.BytesToWrite:N0} bytes needed, {drive.AvailableFreeSpace:N0} available.");
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not check free space: {e.Message}");
        }

        if (plan.Changes.Any(c => c.RequiresContiguity))
            warnings.Add("Disk images (.d81) must be stored contiguously on the card. They are checked after copying where Windows allows it.");

        return plan;
    }

    internal static Dictionary<string, FileInfo> Enumerate(string root)
    {
        var result = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var path in Directory.EnumerateFiles(root, "*", opts))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            result[rel] = new FileInfo(path);
        }
        return result;
    }
}
