using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Sd;

namespace Mega65Gui.Core.Update;

public enum StepStatus { Pending, Running, Done, Skipped, NeedsUser, Failed }

public sealed record StepRecord(int Number, string Title, StepStatus Status, string Detail);

public sealed record UpdateOptions(
    string SdCardPath,
    ConnectionTarget? Connection = null,
    HardwareModel? ModelOverride = null,
    bool IncludeRom = false);

/// <summary>The release selected for this machine and how it compares with what is installed.</summary>
public sealed record ReleaseCandidate(FilehostFile File, FilehostVersionRow Version, ReleaseInfo Info);

public enum CoreComparison { UpToDate, UpdateAvailable, InstalledUnknown }

public sealed record UpdateReport
{
    public DateTimeOffset Started { get; init; }
    public DateTimeOffset? Finished { get; set; }
    public List<StepRecord> Steps { get; } = [];
    public DeviceState? Device { get; set; }
    public HardwareModel Model { get; set; }
    public ReleaseCandidate? Release { get; set; }
    public CoreComparison? Core { get; set; }
    public string? DownloadSha256 { get; set; }
    public SyncResult? SdSync { get; set; }
    public SyncResult? CoreStaging { get; set; }
    public string? GuidedInstructions { get; set; }
    public string? Outcome { get; set; }
}

/// <summary>Implemented by the UI. Nothing is written to the card unless one of these returns approval.</summary>
public interface IApprovalGate
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken ct);

    /// <summary>Returns the relative paths the user approved, or null to cancel.</summary>
    Task<IReadOnlySet<string>?> ApproveChangesAsync(string title, SyncPlan plan, CancellationToken ct);
}
