using System.Diagnostics;
using System.Globalization;
using Mega65Gui.Core.Common;

namespace Mega65Gui.Core.Roms;

/// <summary>MEGA65 ROM (128 KB) facts: version tag "V920xxx" at $16 and start-up settings at $17FF8–$17FFF.</summary>
public static class RomFile
{
    public const int Size = 131072;
    public const int SettingsOffset = 0x17FF8;
    public const int MinConfigurableVersion = 920252;

    public static string? ReadVersion(byte[] rom) =>
        rom.Length == Size && rom[0x16] == 'V' && rom.AsSpan(0x17, 6).ToArray().All(b => b is >= (byte)'0' and <= (byte)'9')
            ? System.Text.Encoding.ASCII.GetString(rom, 0x16, 7) : null;

    public static int? VersionNumber(byte[] rom) =>
        ReadVersion(rom) is { } v && int.TryParse(v[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}

public enum RomSpeed : byte { Mhz1 = 1, Mhz3_5 = 3, Mhz40 = 0 }

/// <summary>Start-up settings stored in the last 8 bytes of the ROM's $17FF8 block.</summary>
public sealed record RomSettings(byte Speed, byte Font, byte Foreground, byte Border, byte Background, byte ErrorMessage, byte Remark, byte Keyword)
{
    /// <summary>Fonts are stored as letters: A/B/C uppercase, D/E/F the lowercase variants of A/B/C.</summary>
    public static readonly IReadOnlyList<(string Label, byte Value)> Fonts =
    [
        ("Font A – uppercase", (byte)'A'), ("Font A – lowercase", (byte)'D'),
        ("Font B – uppercase", (byte)'B'), ("Font B – lowercase", (byte)'E'),
        ("Font C – uppercase", (byte)'C'), ("Font C – lowercase", (byte)'F'),
    ];

    public static readonly IReadOnlyList<(string Label, byte Value)> Speeds =
        [("1 MHz", (byte)RomSpeed.Mhz1), ("3.5 MHz", (byte)RomSpeed.Mhz3_5), ("40 MHz", (byte)RomSpeed.Mhz40)];

    /// <summary>MEGA65 default palette: 0–15 are the classic colours, 16–31 the MEGA65 additions.</summary>
    public static readonly IReadOnlyList<(string Name, uint Rgb)> Palette =
    [
        ("Black", 0x000000), ("White", 0xFFFFFF), ("Red", 0x880000), ("Cyan", 0xAAFFEE), ("Purple", 0xCC44CC), ("Green", 0x00CC55),
        ("Blue", 0x0000AA), ("Yellow", 0xEEEE77), ("Orange", 0xDD8855), ("Brown", 0x664400), ("Pink", 0xFF7777), ("Dark grey", 0x333333),
        ("Medium grey", 0x777777), ("Light green", 0xAAFF66), ("Light blue", 0x0088FF), ("Light grey", 0xBBBBBB),
        ("Guru Meditation", 0xEE0000), ("Rambutan", 0xFF5500), ("Carrot", 0xFFBB00), ("Lemon Tart", 0xEEEE00), ("Pandan", 0x77FF00),
        ("Seasick Green", 0x66EE66), ("Soylent Green", 0x00EE33), ("Slimer Green", 0x00FF99), ("The Other Cyan", 0x00DDDD),
        ("Sea Sky", 0x0099FF), ("Smurf Blue", 0x0033FF), ("Screen of Death", 0x0000EE), ("Plum Sauce", 0x7700FF),
        ("Sour Grape", 0xCC00FF), ("Bubblegum", 0xFF00BB),
    ];

    public static RomSettings Read(byte[] rom)
    {
        Check(rom);
        var o = RomFile.SettingsOffset;
        return new(rom[o], rom[o + 1], rom[o + 2], rom[o + 3], rom[o + 4], rom[o + 5], rom[o + 6], rom[o + 7]);
    }

    /// <summary>Returns a copy of the ROM with these settings; the input is not modified.</summary>
    public byte[] ApplyTo(byte[] rom)
    {
        Check(rom);
        var copy = (byte[])rom.Clone();
        var o = RomFile.SettingsOffset;
        copy[o] = Speed; copy[o + 1] = Font; copy[o + 2] = Foreground; copy[o + 3] = Border;
        copy[o + 4] = Background; copy[o + 5] = ErrorMessage; copy[o + 6] = Remark; copy[o + 7] = Keyword;
        return copy;
    }

    /// <summary>Saves the settings on their own (a preset to apply to other ROMs later).</summary>
    public string ToPreset() => System.Text.Json.JsonSerializer.Serialize(this, PresetJson);

    public static RomSettings FromPreset(string json)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<RomSettings>(json, PresetJson) ?? throw new InvalidDataException("This is not a ROM settings file.");
        if (!Speeds.Any(x => x.Value == s.Speed) || !Fonts.Any(x => x.Value == s.Font)
            || new[] { s.Foreground, s.ErrorMessage, s.Remark, s.Keyword }.Any(c => c > 15) || s.Border >= Palette.Count || s.Background >= Palette.Count)
            throw new InvalidDataException("This ROM settings file has values the ROM does not accept.");
        return s;
    }

    private static readonly System.Text.Json.JsonSerializerOptions PresetJson = new() { WriteIndented = true };

    private static void Check(byte[] rom)
    {
        if (rom.Length != RomFile.Size) throw new InvalidDataException($"Not a MEGA65 ROM: {rom.Length:N0} bytes (expected 131,072).");
        if (RomFile.VersionNumber(rom) is not { } v) throw new InvalidDataException("This ROM has no MEGA65 version tag.");
        if (v < RomFile.MinConfigurableVersion) throw new InvalidDataException($"ROM {v} is too old; start-up settings need 920252 or later.");
    }
}

/// <summary>Applies MEGA65 ROM diff (.rdf) patches with the official romdiff tool.</summary>
public sealed class RomPatcher(IProcessRunner runner)
{
    public const string Magic = "MEGA65ROMPATCH01";

    /// <summary>Reference ROM file name stored in the patch header (offset 32).</summary>
    public static string? ReferenceName(byte[] patch) =>
        patch.Length > 96 && System.Text.Encoding.ASCII.GetString(patch, 0, 16) == Magic
            ? System.Text.Encoding.ASCII.GetString(patch, 32, 64).TrimEnd('\0') : null;

    /// <summary>Name of the patched ROM stored in the header (offset 96).</summary>
    public static string? TargetName(byte[] patch) =>
        patch.Length > 256 && System.Text.Encoding.ASCII.GetString(patch, 0, 16) == Magic
            ? System.Text.Encoding.ASCII.GetString(patch, 96, 160).TrimEnd('\0') : null;

    /// <summary>
    /// Makes a .rdf patch that turns <paramref name="referenceRom"/> into <paramref name="newRom"/>
    /// ("romdiff reference new out"). The patch stores both file names, upper-cased, so the
    /// reference name must be 31 characters or fewer. This can take a few minutes.
    /// </summary>
    public async Task<ProcessResult> CreateAsync(string romdiffExe, string referenceRom, string newRom, string outputPath, CancellationToken ct = default)
    {
        if (Path.GetFileName(referenceRom).Length > 31) throw new InvalidDataException("The original ROM's file name must be 31 characters or fewer (romdiff stores it in the patch). Rename a copy, for example to MEGA65.ROM.");
        if (Path.GetFileName(newRom).Length > 159) throw new InvalidDataException("The changed ROM's file name is too long for a patch.");
        foreach (var rom in new[] { referenceRom, newRom })
            if (new FileInfo(rom).Length < RomFile.Size) throw new InvalidDataException($"{Path.GetFileName(rom)} is smaller than a 128 KB MEGA65 ROM.");
        var r = await runner.RunAsync(romdiffExe, [referenceRom, newRom, outputPath], TimeSpan.FromMinutes(15), null, ct);
        return r.Succeeded && !File.Exists(outputPath) ? r with { ExitCode = -1 } : r;
    }

    /// <summary>romdiff's error lines; it also prints megabytes of progress, which are left out.</summary>
    public static string Problem(ProcessResult r)
    {
        var errors = (r.StdErr + "\n" + r.StdOut).Split('\n', '\r').Select(l => l.Trim()).Where(l => l.StartsWith("ERROR", StringComparison.Ordinal)).Distinct().ToList();
        return errors.Count > 0 ? string.Join(" ", errors) : $"exit code {r.ExitCode}";
    }

    public async Task<ProcessResult> ApplyAsync(string romdiffExe, string patchPath, string baseRomPath, string outputPath, CancellationToken ct = default)
    {
        var patch = await File.ReadAllBytesAsync(patchPath, ct);
        var reference = ReferenceName(patch) ?? throw new InvalidDataException("Not a MEGA65 ROM patch (.rdf) file.");
        if (reference.Contains('/') || reference.Contains('\\') || reference.Contains("..")) throw new InvalidDataException("The patch names an invalid reference file.");

        // romdiff looks for the reference ROM, by the name in the patch header, in its working folder.
        var work = Directory.CreateTempSubdirectory("m65romdiff").FullName;
        try
        {
            File.Copy(baseRomPath, Path.Combine(work, reference));
            File.Copy(patchPath, Path.Combine(work, "patch.rdf"));
            var r = await runner.RunAsync(romdiffExe, ["patch.rdf", "out.rom"], TimeSpan.FromMinutes(1), null, ct, work);
            var produced = Path.Combine(work, "out.rom");
            if (!r.Succeeded || !File.Exists(produced)) return r with { ExitCode = r.ExitCode == 0 ? -1 : r.ExitCode };
            File.Copy(produced, outputPath, overwrite: true);
            return r;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
