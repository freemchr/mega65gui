namespace Mega65Gui.Core.Sd;

public enum SyncAction
{
    /// <summary>File exists in the package but not on the card.</summary>
    Add,
    /// <summary>File exists in both but content differs; the card copy is backed up first.</summary>
    Replace,
    /// <summary>Identical content (size + SHA-256).</summary>
    Unchanged,
    /// <summary>File exists only on the card. Never touched; reported for information.</summary>
    TargetOnly,
}

public sealed record SyncItem(
    string RelativePath,
    SyncAction Action,
    long? PackageSize,
    long? TargetSize,
    string? PackageSha256,
    string? TargetSha256)
{
    public bool IsChange => Action is SyncAction.Add or SyncAction.Replace;
    public bool RequiresContiguity => RelativePath.EndsWith(".d81", StringComparison.OrdinalIgnoreCase)
                                      || RelativePath.EndsWith(".d64", StringComparison.OrdinalIgnoreCase)
                                      || RelativePath.EndsWith(".d65", StringComparison.OrdinalIgnoreCase);
}

public sealed record SyncPlan(
    string PackageRoot,
    string TargetRoot,
    IReadOnlyList<SyncItem> Items,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CreatedAt)
{
    public IEnumerable<SyncItem> Changes => Items.Where(i => i.IsChange);
    public long BytesToWrite => Changes.Sum(i => i.PackageSize ?? 0);
}
