using Mega65Gui.Core.Device;

namespace Mega65Gui.Core.Cores;

/// <summary>
/// How a loader puts a core into the FPGA. Only <see cref="PcJtag"/> exists today; the others
/// are named so the Core Manager's checks and wording do not have to change if an external
/// loader (such as a Pico-based JTAG loader) or an on-device loader is ever proven to work.
/// See docs/CORE-LOADING-FEASIBILITY.md.
/// </summary>
public enum CoreLoaderKind
{
    /// <summary>This PC drives the FPGA's JTAG port through the USB/JTAG adapter (m65 -q).</summary>
    PcJtag,
    /// <summary>Separate hardware attached to the JTAG header loads the core. Not implemented.</summary>
    ExternalLoader,
    /// <summary>The MEGA65 loads the core itself. Not implemented; not known to be possible.</summary>
    OnDevice,
}

public sealed record CoreLoaderInfo(string Name, CoreLoaderKind Kind, string Description, bool LostAtPowerOff);

public sealed record CoreLoadResult(bool Succeeded, string Message, string Output);

/// <summary>Configures the FPGA with a core without writing flash.</summary>
public interface ICoreLoader
{
    CoreLoaderInfo Info { get; }

    /// <summary>Why the loader cannot be used right now, or null when it can.</summary>
    string? Unavailable(ConnectionTarget? connection);

    /// <summary>Why the loader cannot load this kind of file, or null when it can.</summary>
    string? Refuses(CoreFileKind kind);

    Task<CoreLoadResult> LoadAsync(string path, ConnectionTarget connection, IProgress<string>? log, CancellationToken ct);
}

/// <summary>
/// Loads a .bit through the USB/JTAG adapter with m65's built-in JTAG loader ("m65 -q"), the
/// same mechanism as the Device page's Bitstream button. The FPGA runs it until power-off.
/// </summary>
public sealed class JtagCoreLoader(Mega65Commands? commands) : ICoreLoader
{
    public CoreLoaderInfo Info { get; } = new("USB/JTAG from this PC", CoreLoaderKind.PcJtag,
        "m65 -q sends the bitstream over the USB/JTAG adapter straight into the FPGA. Flash is not touched.", LostAtPowerOff: true);

    public string? Unavailable(ConnectionTarget? connection) =>
        commands is null ? "Install the device tools first: loading a core uses m65 from the official tools."
        : connection is null ? "Connect to the MEGA65 over USB/JTAG first (Device page)."
        : connection.Kind != ConnectionKind.Serial ? "Loading a core needs the USB/JTAG connection; Ethernet cannot reach the FPGA's JTAG port."
        : null;

    /// <summary>m65 -q takes a .bit; a .cor is loaded through the .bit it contains (see CoreFile.ExtractBitstream).</summary>
    public string? Refuses(CoreFileKind kind) => null;

    public async Task<CoreLoadResult> LoadAsync(string path, ConnectionTarget connection, IProgress<string>? log, CancellationToken ct)
    {
        if (Unavailable(connection) is { } why) throw new InvalidOperationException(why);
        if (!path.EndsWith(".cor", StringComparison.OrdinalIgnoreCase)) return await RunM65Async(path, path, connection, log, ct);

        // m65 has no .cor support, so the .bit inside the core is written to a temporary file.
        var bit = CoreFile.ExtractBitstream(await File.ReadAllBytesAsync(path, ct));
        var temp = Path.Combine(Path.GetTempPath(), $"mega65gui-{Guid.NewGuid():N}.bit");
        await File.WriteAllBytesAsync(temp, bit, ct);
        try
        {
            log?.Report($"Extracted the {bit.Length:N0}-byte .bit from {Path.GetFileName(path)}.");
            return await RunM65Async(temp, path, connection, log, ct);
        }
        finally { File.Delete(temp); }
    }

    private async Task<CoreLoadResult> RunM65Async(string bitPath, string path, ConnectionTarget connection, IProgress<string>? log, CancellationToken ct)
    {
        var r = await commands!.LoadBitstreamAsync(connection, bitPath, ct, log);
        var output = (r.StdOut + r.StdErr).Trim();
        if (r.Succeeded) return new(true, $"{Path.GetFileName(path)} is running. It stays in the FPGA until the MEGA65 is switched off; flash was not changed.", output);
        var reason = ToolFailureHints.Explain(output)
                     ?? (r.ExitCode == -1 && output.Contains("Timed out", StringComparison.Ordinal)
                         ? "m65 did not finish in time. Check the USB cable and that the MEGA65 is switched on, then try again."
                         : $"m65 exited with code {r.ExitCode}. The MEGA65 should still be running its previous core; see the log for m65's output.");
        return new(false, reason, output);
    }
}

/// <summary>Whether an operation may go ahead, and what the user must be told or asked first.</summary>
public sealed record OperationCheck(string? Blocker, CompatibilityVerdict? Verdict, IReadOnlyList<string> Warnings)
{
    public bool Allowed => Blocker is null;
    /// <summary>True when the hardware match is not confirmed, so the user must accept that explicitly.</summary>
    public bool NeedsExplicitConfirmation => Allowed && Verdict is { Result: CompatibilityResult.Unverified };
}

/// <summary>Pure checks for the Core Manager's three operations, kept apart from the UI so they can be tested.</summary>
public static class CoreOperations
{
    /// <summary>Run a library core now through a loader (temporary, nothing written to flash).</summary>
    public static OperationCheck CheckRun(CoreLibraryEntry entry, bool fileIntact, ICoreLoader loader, ConnectionTarget? connection,
        HardwareModel device, Provenance deviceSource)
    {
        if (!fileIntact) return new("The library copy of this core is missing or has changed since it was imported. Import it again.", null, []);
        if (loader.Refuses(entry.Kind) is { } refused) return new(refused, null, []);
        if (loader.Unavailable(connection) is { } unavailable) return new(unavailable, null, []);
        var verdict = CoreTargets.Check(entry.Identity, device, deviceSource);
        if (verdict.IsBlocked) return new(verdict.Summary, verdict, []);
        // A .bit does not say which board it is for, and every MEGA65 from R3 to R6 has the same
        // FPGA, so a bitstream for another board would load. It is only run once the user has
        // labelled it with the board it is for, and that label matches this machine.
        if (entry.Kind == CoreFileKind.Bit && entry.DeclaredModel is null)
            return new("A .bit file does not record which MEGA65 board it was built for. Set its board under \"Your notes, tags and artwork\" first; " +
                       "it is then run only on that board, after a warning.", verdict, []);
        var warnings = new List<string>();
        if (verdict.Result == CompatibilityResult.Unverified)
            warnings.Add($"Not confirmed for this MEGA65: {verdict.Summary} Loading a core built for another board can leave the machine unresponsive until you switch it off.");
        if (loader.Info.LostAtPowerOff)
            warnings.Add("This is temporary: the core runs until the MEGA65 is switched off. Nothing is written to flash and no slot changes.");
        warnings.Add("Anything unsaved on the MEGA65 is lost when the FPGA is reconfigured.");
        return new(null, verdict, warnings);
    }

    /// <summary>Restart the FPGA from a flash slot (m65 -Z). Nothing is written.</summary>
    public static OperationCheck CheckBootSlot(int slot, bool toolsInstalled, ConnectionTarget? connection, HardwareModel device, bool monitorAnswers = true)
    {
        if (slot is < 0 or > 7) return new("Flash slots are numbered 0 to 7.", null, []);
        if (!toolsInstalled) return new("Install the device tools first: starting a slot uses m65 from the official tools.", null, []);
        if (connection is null) return new("Connect to the MEGA65 over USB/JTAG first (Device page).", null, []);
        if (connection.Kind != ConnectionKind.Serial) return new("Starting a flash slot needs the USB/JTAG connection.", null, []);
        // m65 -Z writes the reconfiguration registers through the MEGA65 core's serial monitor.
        if (!monitorAnswers)
            return new("The running core does not answer the MEGA65 serial monitor (for example an alternate core, or one loaded over JTAG), so m65 cannot start a slot. " +
                       "Switch the MEGA65 off and on, or choose the slot in MEGAFLASH.", null, []);
        var warnings = new List<string>();
        if (HardwareModels.FlashSlots(device) is { } count && slot >= count)
            warnings.Add($"The MEGA65 {device} has {count} slots (0 to {count - 1}); slot {slot} does not exist on it.");
        if (slot == 0) warnings.Add("Slot 0 is the factory core.");
        warnings.Add("This app cannot read which core is stored in each slot. MEGAFLASH (hold NO SCROLL while switching on) shows them.");
        warnings.Add("Anything unsaved on the MEGA65 is lost when the FPGA restarts.");
        return new(null, null, warnings);
    }

    /// <summary>Copy a library core to the SD card so MEGAFLASH can install it.</summary>
    public static OperationCheck CheckStage(CoreLibraryEntry entry, bool fileIntact, HardwareModel device, Provenance deviceSource)
    {
        if (!fileIntact) return new("The library copy of this core is missing or has changed since it was imported. Import it again.", null, []);
        if (entry.Kind != CoreFileKind.Cor)
            return new("MEGAFLASH installs .cor files only. Build a .cor from this .bit with Disk & ROM Tools (bit2core) and import that.", null, []);
        var verdict = CoreTargets.Check(entry.Identity, device, deviceSource);
        if (verdict.IsBlocked) return new(verdict.Summary, verdict, []);
        var warnings = new List<string>
        {
            "Copying the core to the SD card only stores it there. The MEGA65 cannot start a core from the SD card: MEGAFLASH has to write it into a flash slot first.",
        };
        if (verdict.Result == CompatibilityResult.Unverified)
            warnings.Insert(0, $"Not confirmed for this MEGA65: {verdict.Summary}");
        return new(null, verdict, warnings);
    }
}
