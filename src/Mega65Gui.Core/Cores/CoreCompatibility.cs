using System.Text.RegularExpressions;
using Mega65Gui.Core.Device;

namespace Mega65Gui.Core.Cores;

public enum CompatibilityResult { Compatible, Incompatible, Unverified }

public sealed record CompatibilityVerdict(CompatibilityResult Result, string Summary)
{
    public bool IsCompatible => Result == CompatibilityResult.Compatible;
    public bool IsBlocked => Result == CompatibilityResult.Incompatible;
}

/// <summary>
/// What a core file says about the hardware it was built for. <see cref="ModelSource"/> is
/// <see cref="Provenance.ReadFromFile"/> only when a CRC-checked .cor header names the board
/// consistently in both its target name and its model ID.
/// </summary>
public sealed record CoreIdentity(HardwareModel? Model, Provenance ModelSource, string? TargetName, string? Fpga, bool FileValid, string? Note = null)
{
    public static CoreIdentity FromCore(CoreFileInfo info)
    {
        var byName = CoreTargets.ModelOf(info.Target);
        var byId = HardwareModels.FromModelByte(info.ModelId);
        if (byName is null)
            return new(null, Provenance.NotKnown, info.Target, null, info.IsValid, $"The header names the target '{info.Target}', which is not a MEGA65 board this app can identify.");
        if (byId != HardwareModel.Unknown && byId != byName)
            return new(null, Provenance.NotKnown, info.Target, null, info.IsValid,
                $"The header is inconsistent: its target is {info.Target} but its model ID ${info.ModelId:X2} is for {byId}.");
        return new(byName, info.IsValid ? Provenance.ReadFromFile : Provenance.NotKnown, info.Target, null, info.IsValid);
    }

    /// <summary>A .bit file records the FPGA it configures, but not which board it was built for.</summary>
    public static CoreIdentity FromBitstream(BitstreamInfo info, HardwareModel? declared = null) =>
        new(declared, declared is null ? Provenance.NotKnown : Provenance.UserSupplied, null, info.Fpga, info.IsValid);
}

/// <summary>
/// Compatibility between a core and a MEGA65. Only an exact board match, read from both the
/// core's header and the machine itself, counts as compatible. A missing, guessed or
/// user-supplied value is never treated as confirmed.
/// </summary>
public static partial class CoreTargets
{
    /// <summary>bit2core target names that identify exactly one board model.</summary>
    public static HardwareModel? ModelOf(string? target) => target?.Trim().ToLowerInvariant() switch
    {
        "mega65r2" => HardwareModel.R2,
        "mega65r3" => HardwareModel.R3,
        "mega65r4" => HardwareModel.R4,
        "mega65r5" => HardwareModel.R5,
        "mega65r6" => HardwareModel.R6,
        "nexys4ddrwidget" or "nexys4ddr-widget" => HardwareModel.Nexys4DdrWidget,
        "wukonga100t" => HardwareModel.Wukong,
        "megaphoner1" => HardwareModel.MegaPhone,
        // "nexys4" and "nexys4ddr" share one HardwareModel, so neither identifies the board exactly.
        _ => null,
    };

    /// <summary>FPGA fitted to each board (mega65-tools bit2core.c target table; Nexys boards from m65's IDCODE map).</summary>
    public static string? FpgaOf(HardwareModel model) => model switch
    {
        // bit2core.c maps mega65r1, r3, r4, r5 and r6 to part 7a200tfbg484; R2 is not listed there.
        HardwareModel.R1 or HardwareModel.R3 or HardwareModel.R4 or HardwareModel.R5 or HardwareModel.R6 => "XC7A200T",
        HardwareModel.Nexys4 or HardwareModel.Nexys4DdrWidget => "XC7A100T",
        _ => null,
    };

    public static CompatibilityVerdict Check(CoreIdentity core, HardwareModel device, Provenance deviceSource)
    {
        if (!core.FileValid)
            return new(CompatibilityResult.Incompatible, "The file failed its integrity check. Do not load or flash it.");

        var expectedFpga = FpgaOf(device);
        if (core.Fpga is { } fpga && expectedFpga is not null && fpga != expectedFpga)
            return new(CompatibilityResult.Incompatible, $"The bitstream is for an {fpga} FPGA; the MEGA65 {device} has an {expectedFpga}.");

        if (device == HardwareModel.Unknown)
            return new(CompatibilityResult.Unverified, "The MEGA65's model is not known. Connect over USB/JTAG so it can be read.");

        if (core.Model is not { } model)
            return new(CompatibilityResult.Unverified, core.Note ?? (core.TargetName is null
                ? $"A .bit file does not record which board it was built for{(core.Fpga is null ? "" : $" (only that it is for an {core.Fpga})")}. Check the core's documentation."
                : $"The core's target '{core.TargetName}' does not identify one board."));

        // mega65-core's Makefile builds the mega65r6 target for R5 boards as well, but the R6
        // package also ships a separate mega65r5 core: an R6 core on an R5 is plausible, not confirmed.
        if (model == HardwareModel.R6 && device == HardwareModel.R5)
            return new(CompatibilityResult.Unverified, "This core is built for the R6. R5 boards can often run R6 cores, but that is not confirmed for this core; prefer an R5 build if there is one.");
        if (model != device)
            return new(CompatibilityResult.Incompatible, core.ModelSource == Provenance.UserSupplied
                ? $"You marked this core as built for {model}; this MEGA65 is an {device}."
                : $"This core is built for the MEGA65 {model}; this MEGA65 is an {device}.");

        if (core.ModelSource != Provenance.ReadFromFile)
            return new(CompatibilityResult.Unverified, $"Marked as built for {model}, but the file itself does not confirm it.");
        if (deviceSource != Provenance.ReadFromDevice)
            return new(CompatibilityResult.Unverified, $"The core is for the {model}, which matches the model you chose; the MEGA65 itself has not confirmed it.");
        return new(CompatibilityResult.Compatible, $"Built for the MEGA65 {model}, which matches this machine.");
    }

    // r3, R3a, mega65r6, "_r4_r5_r6", "R3/R6"; letters or digits must not run into the token.
    [GeneratedRegex(@"(?<![a-z0-9])(?:mega65)?r(?<n>[2-6])a?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex BoardToken();

    /// <summary>
    /// Boards named in a title or file name, such as "galaga_r3_r6.zip". These are hints for
    /// searching and filtering only: they never make a core compatible.
    /// </summary>
    public static IReadOnlyList<HardwareModel> ModelsNamedIn(params string?[] texts) =>
        texts.OfType<string>()
            .SelectMany(t => BoardToken().Matches(t.Replace('_', ' ').Replace('-', ' ')))
            .Select(m => (HardwareModel)Enum.Parse(typeof(HardwareModel), "R" + m.Groups["n"].Value))
            .Distinct()
            .Order()
            .ToList();
}
