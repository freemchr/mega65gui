using Mega65Gui.Core.Common;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Packages;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Update;

namespace Mega65Gui.Core.Cores;

public sealed record StageResult(bool Staged, string Message, SyncResult? Sync, string CardFileName);

/// <summary>
/// Copies a library core to the root of the SD card for MEGAFLASH, through the same planner,
/// approval window, backup and verification as Update My MEGA65. Flash is never written: the
/// install itself is done on the MEGA65 with MEGAFLASH.
/// </summary>
public sealed class CoreStaging(CoreLibrary library, IApprovalGate gate, AppPaths paths, IContiguityChecker? contiguity = null)
{
    /// <summary>
    /// The name the core gets on the card. mega65_ftp's put command splits on spaces, so spaces
    /// become underscores; the name otherwise stays as imported.
    /// </summary>
    public static string CardFileName(CoreLibraryEntry e) => e.FileName.Replace(' ', '_');

    /// <summary>Stages to a card in this PC's card reader (or any folder holding a copy of the card).</summary>
    public async Task<StageResult> StageToFolderAsync(CoreLibraryEntry entry, string cardRoot, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(cardRoot)) throw new DirectoryNotFoundException($"The SD card folder {cardRoot} does not exist.");
        var name = CardFileName(entry);
        var stage = await PrepareAsync(entry, name, ct);
        try
        {
            var full = await new SdSyncPlanner().PlanAsync(stage, cardRoot, null, ct);
            var item = full.Items.First(i => string.Equals(i.RelativePath, name, StringComparison.OrdinalIgnoreCase));
            if (item.Action == SyncAction.Unchanged) return new(true, $"{name} is already on the card (identical copy).", null, name);
            var plan = full with { Items = [item] };
            var approved = await gate.ApproveChangesAsync($"Copy {name} to the SD card", plan, ct);
            if (approved is null || !approved.Contains(name)) return new(false, "Not copied: you cancelled.", null, name);
            log?.Report($"Copying {name} to {cardRoot}");
            var result = await new SdSyncExecutor(contiguity).ApplyAsync(plan, approved, paths.Backups, log, ct);
            var written = Path.Combine(cardRoot, name);
            var verified = result.AllSucceeded && File.Exists(written) && await Hashing.Sha256FileAsync(written, ct) == entry.Id;
            return new(verified, verified
                ? $"{name} was copied to the card and verified.{(item.Action == SyncAction.Replace ? " The card's previous copy was backed up." : "")}"
                : $"{name} could not be verified on the card: {result.Results.FirstOrDefault()?.Message ?? "the copy does not match"}.", result, name);
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    /// <summary>
    /// Stages to the card inside the MEGA65 over USB/JTAG or Ethernet. A file of the same name is
    /// downloaded first (comparison and backup), and the upload is read back and compared.
    /// </summary>
    public async Task<StageResult> StageToDeviceAsync(CoreLibraryEntry entry, IRemoteCard card, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var name = CardFileName(entry);
        var stage = await PrepareAsync(entry, name, ct);
        try
        {
            var remote = new RemoteSdSync(card);
            var (full, backupDir) = await remote.PlanAsync(stage, paths.Backups, log, ct);
            var item = full.Items.First(i => string.Equals(i.RelativePath, name, StringComparison.OrdinalIgnoreCase));
            if (item.Action == SyncAction.Unchanged) return new(true, $"{name} is already on the MEGA65's card (identical copy).", null, name);
            var plan = full with { Items = [item], Warnings = [] };
            var approved = await gate.ApproveChangesAsync($"Upload {name} to the MEGA65's SD card", plan, ct);
            if (approved is null || !approved.Contains(name)) return new(false, "Not uploaded: you cancelled.", null, name);
            var result = await remote.ApplyAsync(plan, approved, backupDir, log, ct);
            var ok = result.AllSucceeded && result.Results.Any(r => r.Outcome == ItemOutcome.Written);
            return new(ok, ok ? $"{name} was uploaded to the MEGA65's card and read back to verify it."
                              : $"{name} did not verify on the card: {result.Results.FirstOrDefault()?.Message ?? "unknown error"}.", result, name);
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    // ================= The core's own SD-card files =================

    /// <summary>The package a library core came from, when it is still on this PC.</summary>
    public static string? PackageOf(CoreLibraryEntry e) =>
        e.Origin.SourcePath is { } p && PackageExtractor.IsArchive(p) && File.Exists(p) ? p : null;

    /// <summary>
    /// Unpacks the core's package and returns a folder holding only its SD-card folders, laid out
    /// as they must be on the card (for Vixen-32: vixen-32/BOOT.BIN, vixen-32/img/…).
    /// </summary>
    public async Task<(string Stage, IReadOnlyList<SdFolder> Folders)> PrepareSdFilesAsync(CoreLibraryEntry entry, CancellationToken ct = default)
    {
        var package = PackageOf(entry) ?? throw new InvalidOperationException(
            "This core was not added from a package, or its package is no longer on this PC, so there are no SD-card files to copy.");
        var stage = Path.Combine(paths.Extracted, $"sdfiles-{entry.Id[..16]}-{Guid.NewGuid():N}");
        try
        {
            var unpacked = await PackageExtractor.ExtractAsync(package, Path.Combine(stage, "package"), ct);
            var folders = CorePackageFiles.FindSdFolders(unpacked);
            var card = Directory.CreateDirectory(Path.Combine(stage, "card")).FullName;
            foreach (var f in folders) CopyTree(f.LocalPath, Path.Combine(card, f.Name));
            return (stage, folders);
        }
        catch
        {
            Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Copies the core's SD-card folders to a card in a PC reader, after approval of the exact file list.</summary>
    public async Task<StageResult> CopySdFilesToFolderAsync(CoreLibraryEntry entry, string cardRoot, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(cardRoot)) throw new DirectoryNotFoundException($"The SD card folder {cardRoot} does not exist.");
        var (stage, folders) = await PrepareSdFilesAsync(entry, ct);
        try
        {
            if (folders.Count == 0) return new(false, NoFolders, null, "");
            var root = Path.Combine(stage, "card");
            var full = await new SdSyncPlanner().PlanAsync(root, cardRoot, null, ct);
            var plan = full with { Items = full.Items.Where(i => i.Action != SyncAction.TargetOnly).ToList() };
            var names = string.Join(", ", folders.Select(f => f.Name));
            if (!plan.Changes.Any()) return new(true, $"The card already has identical copies of {names}.", null, names);
            var approved = await gate.ApproveChangesAsync($"Copy {names} to the root of the SD card", plan, ct);
            if (approved is null) return new(false, "Not copied: you cancelled.", null, names);
            var result = await new SdSyncExecutor(contiguity).ApplyAsync(plan, approved, paths.Backups, log, ct);
            var check = await new SdSyncPlanner().PlanAsync(root, cardRoot, null, ct);
            var wrong = check.Changes.Count(c => approved.Contains(c.RelativePath));
            return new(result.AllSucceeded && wrong == 0, Summary(result, names, wrong), result, names);
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    /// <summary>Copies the core's SD-card folders to the card inside the MEGA65, creating folders and reading every upload back.</summary>
    public async Task<StageResult> CopySdFilesToDeviceAsync(CoreLibraryEntry entry, ICardTree card, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var (stage, folders) = await PrepareSdFilesAsync(entry, ct);
        try
        {
            if (folders.Count == 0) return new(false, NoFolders, null, "");
            var sync = new CardTreeSync(card);
            var (plan, backupDir) = await sync.PlanAsync(Path.Combine(stage, "card"), paths.Backups, log, ct);
            var names = string.Join(", ", folders.Select(f => f.Name));
            if (!plan.Changes.Any()) return new(true, $"The MEGA65's card already has identical copies of {names}.", null, names);
            var approved = await gate.ApproveChangesAsync($"Upload {names} to the root of the MEGA65's SD card", plan, ct);
            if (approved is null) return new(false, "Not uploaded: you cancelled.", null, names);
            var result = await sync.ApplyAsync(plan, approved, backupDir, log, ct);
            return new(result.AllSucceeded, Summary(result, names, 0), result, names);
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    private const string NoFolders = "The package has no folder for the SD card. Check the core's README: some cores need nothing on the card.";

    private static string Summary(SyncResult r, string names, int stillDifferent)
    {
        var written = r.Results.Count(x => x.Outcome == ItemOutcome.Written);
        var failed = r.Results.Where(x => x.Outcome == ItemOutcome.Failed).ToList();
        var backups = r.Results.Count(x => x.BackupPath is not null);
        return failed.Count == 0 && stillDifferent == 0
            ? $"{names}: {written} file(s) copied and verified{(backups > 0 ? $", {backups} replaced file(s) backed up to {r.BackupDirectory}" : "")}."
            : $"{names}: {written} copied, {failed.Count + stillDifferent} failed: {string.Join("; ", failed.Take(3).Select(f => $"{f.Item.RelativePath} ({f.Message})"))}";
    }

    /// <summary>A folder holding only the core, under the name it will have on the card.</summary>
    private async Task<string> PrepareAsync(CoreLibraryEntry entry, string name, CancellationToken ct)
    {
        if (!await library.VerifyAsync(entry, ct)) throw new IOException($"The library copy of {entry.FileName} is missing or has changed since it was imported.");
        var stage = Path.Combine(paths.Extracted, $"stage-{entry.Id[..16]}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        File.Copy(library.PathOf(entry), Path.Combine(stage, name));
        return stage;
    }

    /// <summary>On-device MEGAFLASH steps. Slot 0 (the factory core) is never suggested.</summary>
    public static string FlashInstructions(HardwareModel model, string cardFileName)
    {
        var slots = HardwareModels.FlashSlots(model);
        var range = slots is { } n ? $"1 to {n - 1}" : "1 or higher";
        return $"""
            Install {cardFileName} with MEGAFLASH on the MEGA65{(model == HardwareModel.Unknown ? "" : $" {model}")}:
              1. If the card is in a PC card reader, eject it safely and put it back in the MEGA65.
              2. Switch the MEGA65 off. Hold NO SCROLL and switch it on to open MEGAFLASH.
              3. MEGAFLASH lists the core in each slot. Choose a slot from {range}. Never choose slot 0: it holds the factory core the MEGA65 falls back to.
                 Slot 1 normally holds the MEGA65 core; choosing it replaces that core.
              4. Select {cardFileName}, check the name and version MEGAFLASH shows, and confirm. Do not switch off while it writes.
              5. Power-cycle. Start the new slot from MEGAFLASH, or with Boot slot in the Core Manager.
            {(HardwareModels.SupportsPcFlashing(model)
                ? "This board could also be flashed from the PC with mega65_ftp and Vivado. That path is not verified on hardware, so it is not offered here."
                : "The official tools cannot flash this model from a PC, so this step is done on the MEGA65.")}
            """;
    }
}
