using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Packages;
using Mega65Gui.Core.Sd;

namespace Mega65Gui.Core.Update;

/// <summary>
/// "Update My MEGA65". Each of the twelve steps is recorded in the report. Nothing on the SD
/// card changes without explicit approval, nothing is ever deleted, and FPGA flash is never
/// written automatically: the official tools cannot flash R4–R6 from a PC, so core
/// installation is always guided through the on-device MEGAFLASH utility.
/// </summary>
public sealed class UpdateEngine(
    CatalogueService catalogue,
    DownloadManager downloads,
    IDeviceProbe probe,
    IApprovalGate gate,
    AppPaths paths,
    IContiguityChecker? contiguity = null,
    Func<ConnectionTarget, IRemoteCard>? remoteCards = null)
{
    public static readonly string[] StepTitles =
    [
        "Identify connection and device capabilities",
        "Determine hardware and software versions",
        "Discover compatible Filehost releases",
        "Present change summary",
        "Download and validate release package",
        "Compare release package with SD card",
        "Back up affected files",
        "Apply approved SD-card changes",
        "Stage FPGA core",
        "Install core (verified operation or guided)",
        "Verify completed operations",
        "Save operation report",
    ];

    public event Action<StepRecord>? StepChanged;

    private UpdateReport _report = null!;

    public async Task<UpdateReport> RunAsync(UpdateOptions options, IProgress<string>? log = null, CancellationToken ct = default)
    {
        _report = new UpdateReport { Started = DateTimeOffset.Now };
        try
        {
            await RunStepsAsync(options, log, ct);
        }
        catch (OperationCanceledException)
        {
            _report.Outcome ??= "Cancelled by user.";
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            var current = _report.Steps.LastOrDefault(s => s.Status == StepStatus.Running);
            if (current is not null) Set(current.Number, StepStatus.Failed, e.Message);
            _report.Outcome = $"Stopped: {e.Message}";
        }
        finally
        {
            _report.Finished = DateTimeOffset.Now;
            // Mark the step done with its path before writing, so the saved report records it as done.
            Set(12, StepStatus.Done, ReportStem(_report) + ".md");
            try
            {
                await SaveReportAsync(_report, CancellationToken.None);
            }
            catch (Exception e)
            {
                // The update result is still returned to the caller even if the report cannot be written.
                Set(12, StepStatus.Failed, $"Report could not be saved: {e.Message}");
            }
        }
        return _report;
    }

    private async Task RunStepsAsync(UpdateOptions o, IProgress<string>? log, CancellationToken ct)
    {
        // 1. Connection and capabilities
        Set(1, StepStatus.Running, "");
        if (o.CardInDevice && (o.Connection is null || remoteCards is null))
        {
            Set(1, StepStatus.NeedsUser, "Updating the card inside the MEGA65 needs a USB/JTAG or Ethernet connection. Connect on the Device page first.");
            _report.Outcome = "Connection required.";
            return;
        }
        DeviceState device = DeviceState.Unknown;
        if (o.Connection is { } conn)
        {
            try { device = await probe.ProbeAsync(conn, ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
            {
                log?.Report($"Device probe failed: {e.Message}");
            }
            Set(1, StepStatus.Done, $"{conn}. Remote read-back {(conn.Kind == ConnectionKind.Serial ? "available" : "not available over Ethernet")}.");
        }
        else Set(1, StepStatus.Done, "No device connection. Working from the SD card only.");
        _report.Device = device;

        // 2. Versions
        Set(2, StepStatus.Running, "");
        var model = device.Model != HardwareModel.Unknown ? device.Model : o.ModelOverride ?? HardwareModel.Unknown;
        _report.Model = model;
        if (model == HardwareModel.Unknown)
        {
            Set(2, StepStatus.NeedsUser, "Hardware model unknown. Connect over USB/JTAG or choose the model manually.");
            _report.Outcome = "Model required.";
            return;
        }
        var source = device.Model != HardwareModel.Unknown ? "read from device" : "selected by user";
        Set(2, StepStatus.Done, $"Model {model} ({source}); core {device.CoreBuild ?? "unknown"}; ROM {device.RomVersion ?? "unknown"}.");

        // 3. Releases
        Set(3, StepStatus.Running, "");
        var candidate = await FindReleaseAsync(model, ct);
        string? romNote = null;
        if (o.IncludeRom && candidate is not null)
        {
            (var withRom, romNote) = await FindRomReleaseAsync(candidate, ct);
            if (withRom is not null) candidate = withRom;
        }
        if (candidate is null)
        {
            Set(3, StepStatus.Failed, $"No public stable release package targets {model}.");
            _report.Outcome = "No compatible release.";
            return;
        }
        _report.Release = candidate;
        Set(3, StepStatus.Done, $"{candidate.File.Title}: release {candidate.Info.Release} ({candidate.Version.FileName}, {candidate.Version.Published})."
            + (romNote is null ? "" : $"\n{romNote}"));

        // 4. Summary
        Set(4, StepStatus.Running, "");
        _report.Core = CompareCore(device, candidate.Info);
        var summary = new StringBuilder()
            .AppendLine($"Machine: MEGA65 {model} ({source})")
            .AppendLine($"Installed core: {device.CoreBuild ?? "unknown (not read from device)"}")
            .AppendLine($"Available: release {candidate.Info.Release}, commit {candidate.Info.CommitHash ?? "n/a"}")
            .AppendLine(_report.Core switch
            {
                CoreComparison.UpToDate => "The running core already matches this release.",
                CoreComparison.UpdateAvailable =>
                    "The running core is different from this release. Note: this is the core running right now. "
                    + "If the MEGA65 started from the factory core (slot 0) and slot 1 already holds this release, there is nothing to flash.",
                _ => "The installed core could not be read, so the SD card will be compared file by file.",
            })
            .AppendLine()
            .AppendLine(o.CardInDevice
                ? "The card inside the MEGA65 is read with a helper program loaded into the MEGA65's memory: anything unsaved in memory will be lost, and the MEGA65 resets after each card operation. Save any work on the MEGA65 first."
                : "")
            .AppendLine("Next: download the package and compare it with your SD card. Nothing is written yet.")
            .ToString();
        Set(4, StepStatus.Done, summary);
        if (!await gate.ConfirmAsync("Update My MEGA65", summary, ct)) { _report.Outcome = "Cancelled at summary."; return; }

        // 5. Download and validate
        Set(5, StepStatus.Running, candidate.Version.FileName);
        var manifest = await downloads.DownloadAsync(candidate.Version, null, ct);
        _report.DownloadSha256 = manifest.Sha256;
        var extracted = await PackageExtractor.ExtractAsync(downloads.PathOf(manifest), Path.Combine(paths.Extracted, candidate.Version.VersionId), ct);
        var package = ReleasePackage.Inspect(extracted);
        var coreFile = SelectCore(package, model);
        if (package.SdCardRoot is null) throw new InvalidDataException("The release package has no recognisable SD-card files.");
        var romCheck = !candidate.Info.IncludesRom ? ""
            : package.HasRom ? " Includes MEGA65.ROM."
            : " WARNING: this package was expected to include MEGA65.ROM but does not.";
        Set(5, StepStatus.Done, $"{manifest.Size:N0} bytes, sha256 {manifest.Sha256}. Core: {(coreFile is null ? "none for this model" : Path.GetFileName(coreFile))}.{romCheck}");

        // The card is either in a PC card reader (a folder) or inside the MEGA65 (reached with mega65_ftp).
        RemoteSdSync? remote = null;
        if (o.CardInDevice)
        {
            if (o.Connection is null || remoteCards is null)
                throw new InvalidOperationException("Connect to the MEGA65 first to update the card inside it.");
            remote = new RemoteSdSync(remoteCards(o.Connection));
        }

        // 6. Compare with card
        Set(6, StepStatus.Running, remote is null ? "" : $"{remote.Description}. The MEGA65 resets after each card operation.");
        SyncPlan plan;
        string? remoteBackupDir = null;
        if (remote is null) plan = await new SdSyncPlanner().PlanAsync(package.SdCardRoot, o.SdCardPath, log, ct);
        else (plan, remoteBackupDir) = await remote.PlanAsync(package.SdCardRoot, paths.Backups, log, ct);
        var adds = plan.Items.Count(i => i.Action == SyncAction.Add);
        var reps = plan.Items.Count(i => i.Action == SyncAction.Replace);
        Set(6, StepStatus.Done, $"{adds} to add, {reps} to replace, {plan.Items.Count(i => i.Action == SyncAction.Unchanged)} unchanged, {plan.Items.Count(i => i.Action == SyncAction.TargetOnly)} of your own files left alone.");

        // 7 + 8. Backup and apply
        if (plan.Changes.Any())
        {
            var approved = await gate.ApproveChangesAsync("Approve SD-card changes", plan, ct);
            if (approved is null) { _report.Outcome = "Cancelled before SD changes."; Set(7, StepStatus.Skipped, "Cancelled"); Set(8, StepStatus.Skipped, "Cancelled"); return; }
            Set(7, StepStatus.Running, "");
            Set(8, StepStatus.Running, "");
            var result = remote is null
                ? await new SdSyncExecutor(contiguity).ApplyAsync(plan, approved, paths.Backups, log, ct)
                : await remote.ApplyAsync(plan, approved, remoteBackupDir!, log, ct);
            _report.SdSync = result;
            var backedUp = result.Results.Count(r => r.BackupPath is not null);
            Set(7, StepStatus.Done, backedUp == 0 ? "No existing files needed backing up." : $"{backedUp} file(s) backed up to {result.BackupDirectory}.");
            var written = result.Results.Count(r => r.Outcome == ItemOutcome.Written);
            var failed = result.Results.Where(r => r.Outcome == ItemOutcome.Failed).ToList();
            Set(8, failed.Count == 0 ? StepStatus.Done : StepStatus.Failed,
                $"{written} written, {result.Results.Count(r => r.Outcome == ItemOutcome.Skipped)} skipped, {failed.Count} failed."
                + string.Concat(failed.Select(f => $"\n  {f.Item.RelativePath}: {f.Message}")));
        }
        else
        {
            Set(7, StepStatus.Skipped, "Nothing to back up.");
            Set(8, StepStatus.Skipped, "The SD card already matches the release.");
        }

        // 9. Stage core
        string? stagedCore = null;
        if (coreFile is null)
            Set(9, StepStatus.Skipped, "This package has no core file for the model.");
        else if (_report.Core == CoreComparison.UpToDate)
            Set(9, StepStatus.Skipped, "The running core already matches this release.");
        else
        {
            Set(9, StepStatus.Running, "");
            var coreDir = Path.GetDirectoryName(coreFile)!;
            var coreRel = Path.GetFileName(coreFile);
            stagedCore = coreRel;
            if (remote is null)
            {
                var corePlan = await new SdSyncPlanner().PlanAsync(coreDir, o.SdCardPath, null, ct);
                var coreItem = corePlan.Items.First(i => string.Equals(i.RelativePath, coreRel, StringComparison.OrdinalIgnoreCase));
                corePlan = corePlan with { Items = [coreItem], Warnings = [] };
                if (coreItem.Action == SyncAction.Unchanged)
                    Set(9, StepStatus.Done, $"{coreRel} is already on the card.");
                else if (await gate.ConfirmAsync("Stage core", $"Copy {coreRel} to the root of the SD card so MEGAFLASH can install it?", ct))
                {
                    var r = await new SdSyncExecutor().ApplyAsync(corePlan, new HashSet<string> { coreRel }, paths.Backups, log, ct);
                    _report.CoreStaging = r;
                    Set(9, r.AllSucceeded ? StepStatus.Done : StepStatus.Failed, r.AllSucceeded ? $"{coreRel} copied and verified." : r.Results[0].Message ?? "Failed");
                }
                else { stagedCore = null; Set(9, StepStatus.Skipped, "Declined by user."); }
            }
            else
            {
                // Cores are 8 MB; over USB/JTAG that is minutes, so an identical name and size counts as present
                // (release core names include the commit hash).
                var size = new FileInfo(coreFile).Length;
                var listing = await remote.ListAsync(ct);
                if (listing.Any(e => !e.IsDirectory && e.Matches(coreRel) && e.Size == size))
                    Set(9, StepStatus.Done, $"{coreRel} is already on the card (same name and size).");
                else if (await gate.ConfirmAsync("Stage core",
                    $"Upload {coreRel} ({size / 1048576.0:0.0} MB) to the MEGA65's SD card so MEGAFLASH can install it? Over USB/JTAG this takes several minutes.", ct))
                {
                    var item = new SyncItem(coreRel, SyncAction.Add, size, null, await Hashing.Sha256FileAsync(coreFile, ct), null);
                    var corePlan = new SyncPlan(coreDir, remote.Description, [item], [], DateTimeOffset.Now);
                    var r = await remote.ApplyAsync(corePlan, new HashSet<string> { coreRel }, paths.Backups, log, ct);
                    _report.CoreStaging = r;
                    Set(9, r.AllSucceeded ? StepStatus.Done : StepStatus.Failed, r.AllSucceeded ? $"{coreRel} uploaded and verified." : r.Results[0].Message ?? "Failed");
                }
                else { stagedCore = null; Set(9, StepStatus.Skipped, "Declined by user."); }
            }
        }

        // 10. Core installation: guided only
        if (stagedCore is null)
            Set(10, StepStatus.Skipped, "No core staged.");
        else
        {
            _report.GuidedInstructions = GuidedFlashInstructions(model, stagedCore)
                + (_report.Core == CoreComparison.InstalledUnknown
                    ? $"\nThe running core could not be read. If MEGAINFO on your MEGA65 already shows release {candidate.Info.Release} ({candidate.Info.CommitHash}), there is nothing to flash."
                    : "");
            Set(10, StepStatus.NeedsUser, _report.GuidedInstructions);
        }

        // 11. Verify
        Set(11, StepStatus.Running, "");
        if (remote is not null)
        {
            // Every uploaded file was already read back from the card and compared in step 8.
            var bad = _report.SdSync?.Results.Where(r => r.Outcome == ItemOutcome.Failed).Select(r => r.Item.RelativePath).ToList() ?? [];
            var uploaded = (_report.SdSync?.Results.Count(r => r.Outcome == ItemOutcome.Written) ?? 0)
                           + (_report.CoreStaging?.Results.Count(r => r.Outcome == ItemOutcome.Written) ?? 0);
            Set(11, bad.Count == 0 ? StepStatus.Done : StepStatus.Failed, bad.Count == 0
                ? uploaded == 0
                    ? "Nothing needed uploading; the card's files were compared byte for byte with the release."
                    : $"All {uploaded} uploaded file(s) were read back from the card and match the release."
                : $"{bad.Count} file(s) did not match on read-back: {string.Join(", ", bad)}");
        }
        else
        {
            var verify = await new SdSyncPlanner().PlanAsync(package.SdCardRoot, o.SdCardPath, null, ct);
            var approvedPaths = _report.SdSync?.Results.Where(r => r.Outcome == ItemOutcome.Written).Select(r => r.Item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var stillDifferent = verify.Changes.Where(c => approvedPaths.Contains(c.RelativePath)).ToList();
            var declined = verify.Changes.Count() - stillDifferent.Count;
            Set(11, stillDifferent.Count == 0 ? StepStatus.Done : StepStatus.Failed,
                stillDifferent.Count == 0
                    ? $"All written files match the release on re-read.{(declined > 0 ? $" {declined} change(s) you declined remain." : "")}"
                    : $"{stillDifferent.Count} file(s) differ after writing: {string.Join(", ", stillDifferent.Select(s => s.RelativePath))}");

        }

        _report.Outcome = _report.Steps.Any(s => s.Status == StepStatus.Failed) ? "Completed with errors."
            : stagedCore is not null ? "SD card updated. Finish the core install on the MEGA65."
            : "Completed.";
    }

    internal async Task<ReleaseCandidate?> FindReleaseAsync(HardwareModel model, CancellationToken ct)
    {
        var target = HardwareModels.ReleaseTarget(model);
        if (target is null) return null;
        if (catalogue.Current is null) await catalogue.LoadAsync(ct: ct);

        // There can be more than one official package per board (e.g. "no ROM" and, for signed-in
        // owners, "with ROM"); consider them all and take the newest version without the ROM.
        var files = catalogue.Search(new CatalogueQuery(OfficialOnly: true))
            .Where(OfficialReleases.IsStableCorePackage)
            .Where(f => OfficialReleases.TargetFromTitle(f.Title) == target)
            .ToList();

        ReleaseCandidate? best = null;
        foreach (var file in files)
        {
            var versions = await catalogue.GetVersionsAsync(file.FileId, ct: ct);
            var candidate = versions
                .Where(v => v.IsActive)
                .Select(v => (v, info: ReleaseInfo.TryParse(v.FileName, v.Comment)))
                .Where(x => x.info is not null && !x.info.IncludesRom)
                .OrderByDescending(x => x.info!.Release)
                .Select(x => new ReleaseCandidate(file, x.v, x.info!))
                .FirstOrDefault();
            if (candidate is not null && (best is null || candidate.Info.Release > best.Info.Release)) best = candidate;
        }
        return best;
    }

    /// <summary>
    /// The monitor's "build GIT:" line and the package file names both embed the commit hash,
    /// so the core counts as current when the running build string contains the release hash.
    /// </summary>
    /// <summary>
    /// The no-ROM package description links to the owner-only package that includes the closed
    /// ROM. That package is only used when the signed-in account can see it (checked by the server).
    /// </summary>
    internal async Task<(ReleaseCandidate? Candidate, string Note)> FindRomReleaseAsync(ReleaseCandidate publicRelease, CancellationToken ct)
    {
        if (catalogue.Session is not { IsSignedIn: true })
            return (null, "Not signed in to the Filehost, so the package without the closed ROM is used.");

        var detail = await catalogue.GetDetailAsync(publicRelease.File.FileId, ct);
        foreach (var id in FilehostLinks.Extract(detail?.Description).Where(id => !catalogue.IsPublic(id)))
        {
            IReadOnlyList<FilehostVersionRow> versions;
            FilehostFileDetail? romDetail;
            try
            {
                romDetail = await catalogue.GetDetailAsync(id, ct);
                if (romDetail is not { IsAvailable: true }) continue;
                versions = await catalogue.GetVersionsAsync(id, ct: ct);
            }
            catch (UnauthorizedAccessException) { continue; }

            var best = versions
                .Where(v => v.IsActive)
                .Select(v => (v, info: ReleaseInfo.TryParse(v.FileName, v.Comment)))
                .Where(x => x.info is { IncludesRom: true } && x.info.Release >= publicRelease.Info.Release)
                .OrderByDescending(x => x.info!.Release)
                .FirstOrDefault();
            if (best.info is null) continue;

            var file = new FilehostFile { FileId = id, Title = romDetail.Title ?? "Release package with ROM", Author = romDetail.Author,
                Category = romDetail.Category, Type = romDetail.Type };
            return (new ReleaseCandidate(file, best.v, best.info), "Using the package that includes the closed ROM (your Filehost account has access).");
        }
        return (null, "Your Filehost account cannot see a package with the closed ROM (redeem your ownership code on the Filehost), so the package without the ROM is used.");
    }

    internal static CoreComparison CompareCore(DeviceState device, ReleaseInfo release)
    {
        if (string.IsNullOrWhiteSpace(device.CoreBuild) || release.CommitHash is null) return CoreComparison.InstalledUnknown;
        return device.CoreBuild.Contains(release.CommitHash, StringComparison.OrdinalIgnoreCase)
            ? CoreComparison.UpToDate : CoreComparison.UpdateAvailable;
    }

    internal static string? SelectCore(ReleasePackage package, HardwareModel model)
    {
        var prefix = HardwareModels.CorePrefix(model);
        if (prefix is null) return null;
        return package.CoreFiles
            .Where(f => Path.GetExtension(f).Equals(".cor", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(f => Path.GetFileName(f).StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)
                              || Path.GetFileName(f).StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)
                              || Path.GetFileName(f).StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase));
    }

    internal static string GuidedFlashInstructions(HardwareModel model, string coreFile) =>
        $"""
        Install the core on your MEGA65 {model} with MEGAFLASH:
          1. Eject the SD card safely and put it back into the MEGA65's internal slot.
          2. Switch the MEGA65 off. Hold NO SCROLL and switch it on to open MEGAFLASH.
          3. MEGAFLASH lists the core in each slot. If slot 1 already shows {coreFile}, there is nothing to flash: just start slot 1.
          4. Otherwise choose slot 1 (never slot 0, the factory core), then select {coreFile}.
          5. Confirm the flash and wait. Do not switch off while it runs.
          6. Power-cycle, then run Update My MEGA65 again over USB/JTAG to confirm the new core is the one running.
        {(HardwareModels.SupportsPcFlashing(model)
            ? "Your board could also be flashed from the PC with mega65_ftp and Vivado. That path has not been verified on hardware, so it is not offered here."
            : "The official tools cannot flash this model from a PC, so this step must be done on the machine.")}
        See the Filehost article "How to flash a core (Release 0.96 and later)".
        """;

    private void Set(int n, StepStatus status, string detail)
    {
        var rec = new StepRecord(n, StepTitles[n - 1], status, detail);
        var i = _report.Steps.FindIndex(s => s.Number == n);
        if (i >= 0) _report.Steps[i] = rec; else _report.Steps.Add(rec);
        StepChanged?.Invoke(rec);
    }

    private string ReportStem(UpdateReport report) => Path.Combine(paths.Reports, $"update-{report.Started:yyyyMMdd-HHmmss}");

    private async Task<string> SaveReportAsync(UpdateReport report, CancellationToken ct)
    {
        var stem = ReportStem(report);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
        await File.WriteAllTextAsync(stem + ".json", json, ct);
        await File.WriteAllTextAsync(stem + ".md", ToMarkdown(report), ct);
        return stem + ".md";
    }

    public static string ToMarkdown(UpdateReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Update My MEGA65 report").AppendLine();
        sb.AppendLine($"- Started: {r.Started:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Finished: {r.Finished:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Model: {r.Model}");
        if (r.Release is { } rel) sb.AppendLine($"- Release: {rel.Info.Release} ({rel.Version.FileName}, sha256 {r.DownloadSha256 ?? "not downloaded"})");
        sb.AppendLine($"- Outcome: {r.Outcome}").AppendLine();
        sb.AppendLine("| # | Step | Status | Detail |").AppendLine("|---|---|---|---|");
        foreach (var s in r.Steps.OrderBy(s => s.Number))
            sb.AppendLine($"| {s.Number} | {s.Title} | {s.Status} | {s.Detail.Replace("\r", "").Replace("\n", "<br>").Replace("|", "\\|")} |");
        if (r.SdSync is { } sync)
        {
            sb.AppendLine().AppendLine("## SD-card changes").AppendLine();
            foreach (var x in sync.Results)
                sb.AppendLine($"- {x.Outcome}: {x.Item.RelativePath} ({x.Item.Action}){(x.BackupPath is null ? "" : $", backup: {x.BackupPath}")}{(x.Message is null ? "" : $", {x.Message}")}");
        }
        return sb.ToString();
    }
}
