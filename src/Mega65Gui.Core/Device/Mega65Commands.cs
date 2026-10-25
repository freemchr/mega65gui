using Mega65Gui.Core.Common;
using Mega65Gui.Core.Tools;

namespace Mega65Gui.Core.Device;

public enum MachineMode { Mega65, C64 }

/// <summary>How a PRG is sent. Pal: null keeps the current video mode. MountD81: a .D81 in the card's root (Ethernet only).</summary>
public sealed record PrgOptions(bool Run = true, bool C64Mode = false, bool ResetFirst = true, bool UseFileLoadAddress = false,
    bool? Pal = null, string? MountD81 = null);

/// <summary>
/// Device operations implemented by running the official tools. Argument lists follow the
/// documented flags of m65, mega65_ftp and etherload. On Windows m65 cannot auto-detect the
/// serial port, so the port is always passed explicitly.
/// </summary>
public sealed class Mega65Commands(ToolInstallation tools, IProcessRunner runner)
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<string> SerialArgs(ConnectionTarget t) => ["-l", t.Address, "-s", "2000000"];

    // ---- argument builders (pure, unit tested) ----

    public static (string Tool, List<string> Args) ResetArgs(ConnectionTarget t) => t.Kind switch
    {
        ConnectionKind.Serial => ("m65", [.. SerialArgs(t), "-F"]),
        ConnectionKind.Ethernet => ("etherload", ["-i", t.Address, "-5"]),
        _ => throw new InvalidOperationException("Not connected."),
    };

    public static (string Tool, List<string> Args) ModeArgs(ConnectionTarget t, MachineMode mode) => (t.Kind, mode) switch
    {
        (ConnectionKind.Serial, MachineMode.C64) => ("m65", [.. SerialArgs(t), "-F", "-4"]),
        (ConnectionKind.Serial, MachineMode.Mega65) => ("m65", [.. SerialArgs(t), "-F"]),
        (ConnectionKind.Ethernet, MachineMode.C64) => ("etherload", ["-i", t.Address, "--c64mode"]),
        (ConnectionKind.Ethernet, MachineMode.Mega65) => ("etherload", ["-i", t.Address, "--m65mode"]),
        _ => throw new InvalidOperationException("Not connected."),
    };

    /// <summary>Loads (and optionally runs) a PRG. C64 mode is chosen when the program is a C64 one.</summary>
    public static (string Tool, List<string> Args) SendPrgArgs(ConnectionTarget t, string prg, bool run, bool c64Mode) =>
        SendPrgArgs(t, prg, new PrgOptions(run, c64Mode));

    /// <summary>
    /// Loads a PRG with the chosen options. Over USB/JTAG: -F reset first, -4 C64 mode, -1 load
    /// to the address in the file (as LOAD"…",8,1), -p/-n PAL/NTSC, -r run. Over Ethernet
    /// etherload always resets into the chosen mode and always uses the file's address; it can
    /// also mount a D81 from the card's root folder (-m).
    /// </summary>
    public static (string Tool, List<string> Args) SendPrgArgs(ConnectionTarget t, string prg, PrgOptions o)
    {
        if (o.MountD81 is not null && t.Kind != ConnectionKind.Ethernet) throw new NotSupportedException("Mounting a disk image with the program is only available over Ethernet.");
        string[] video = o.Pal is { } pal ? [t.Kind == ConnectionKind.Serial ? (pal ? "-p" : "-n") : (pal ? "--pal" : "--ntsc")] : [];
        return t.Kind switch
        {
            ConnectionKind.Serial => ("m65", [.. SerialArgs(t), .. Flag(o.ResetFirst, "-F"), .. Flag(o.C64Mode, "-4"), .. Flag(o.UseFileLoadAddress, "-1"),
                .. video, .. Flag(o.Run, "-r"), prg]),
            ConnectionKind.Ethernet => ("etherload", ["-i", t.Address, o.C64Mode ? "-4" : "-5", .. video,
                .. (o.MountD81 is { } m ? new[] { "-m", m } : []), .. Flag(o.Run, "-r"), prg]),
            _ => throw new InvalidOperationException("Not connected."),
        };
    }

    private static string[] Flag(bool on, string flag) => on ? [flag] : [];

    /// <summary>
    /// Serves a D81 image on this PC as the MEGA65's drive 8 ("m65 -d"): m65 stays running and
    /// answers the MEGA65's disk requests over the USB cable until it is stopped. The MEGA65 is
    /// not reset. Writes from the MEGA65 go straight into the file.
    /// </summary>
    public static (string Tool, List<string> Args) VirtualD81Args(ConnectionTarget t, string d81) => SerialOnly(t, "Serving a disk image from this PC", "-d", d81);

    /// <summary>Monitor command that turns the MEGA65's disk virtualisation off again after m65 -d is stopped.</summary>
    public const string VirtualD81Off = "sffd3659 00";

    /// <summary>Switches to C64 mode without a reset (m65 -4 without -F types GO64 for you).</summary>
    public static (string Tool, List<string> Args) Go64Args(ConnectionTarget t) => SerialOnly(t, "Going to C64 mode without a reset", "-4");

    /// <summary>Typed in C64 mode, returns to MEGA65 mode without a reset.</summary>
    public const string Go65Command = "sys58552";

    private static (string Tool, List<string> Args) SerialOnly(ConnectionTarget t, string what, params string[] args) =>
        t.Kind == ConnectionKind.Serial ? ("m65", [.. SerialArgs(t), .. args]) : throw new NotSupportedException($"{what} is only available over USB/JTAG.");

    /// <summary>Plays a SID tune with m65's built-in player (C64 mode).</summary>
    public static (string Tool, List<string> Args) SendSidArgs(ConnectionTarget t, string sid) => SerialOnly(t, "Playing SID files", "-F", "-4", "-r", sid);

    /// <summary>Loads an FPGA bitstream until power-off. "-q" uses m65's built-in JTAG loader (no Vivado).</summary>
    public static (string Tool, List<string> Args) LoadBitstreamArgs(ConnectionTarget t, string bit) => SerialOnly(t, "Loading a bitstream", "-q", bit);

    /// <summary>Replaces the hypervisor (HICKUP) until the next reset.</summary>
    public static (string Tool, List<string> Args) LoadHickupArgs(ConnectionTarget t, string hickup) => SerialOnly(t, "Loading HICKUP", "-F", "-k", hickup);

    /// <summary>Starts the MEGA65 with a ROM file from the PC (not stored on the card; lost at power-off).</summary>
    public static (string Tool, List<string> Args) LoadRomArgs(ConnectionTarget t, string rom) => t.Kind switch
    {
        ConnectionKind.Serial => ("m65", [.. SerialArgs(t), "-F", "-R", rom]),
        ConnectionKind.Ethernet => ("etherload", ["-i", t.Address, "-R", rom]),
        _ => throw new InvalidOperationException("Not connected."),
    };

    public static (string Tool, List<string> Args) VideoModeArgs(ConnectionTarget t, bool pal) => t.Kind switch
    {
        ConnectionKind.Serial => ("m65", [.. SerialArgs(t), pal ? "-p" : "-n"]),
        ConnectionKind.Ethernet => ("etherload", ["-i", t.Address, "-5", pal ? "--pal" : "--ntsc"]),
        _ => throw new InvalidOperationException("Not connected."),
    };

    /// <summary>Restarts the FPGA from a core already stored in flash (nothing is written to flash).</summary>
    public static (string Tool, List<string> Args) BootSlotArgs(ConnectionTarget t, int slot) =>
        slot is >= 0 and <= 7 ? SerialOnly(t, "Starting a flash slot", "-Z", slot.ToString(System.Globalization.CultureInfo.InvariantCulture))
                              : throw new ArgumentOutOfRangeException(nameof(slot));

    /// <summary>Types text via the virtual keyboard, one line at a time with RETURN ("-T -" reads stdin until ".").</summary>
    public static (string Tool, List<string> Args) TypeTextArgs(ConnectionTarget t) => SerialOnly(t, "Typing text", "-T", "-");

    /// <summary>
    /// Prepares a BASIC listing for typing: blank lines dropped and letters lowercased, because
    /// lowercase keys produce BASIC keywords in the MEGA65's default character set.
    /// </summary>
    public static string PrepareBasicListing(string text) =>
        string.Join("\n", text.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0 && l != ".")
            .Select(l => l.ToLowerInvariant())) + "\n.\n";

    public static (string Tool, List<string> Args) ScreenshotArgs(ConnectionTarget t, string pngPath) => t.Kind == ConnectionKind.Serial
        ? ("m65", [.. SerialArgs(t), "-S" + pngPath])
        : throw new NotSupportedException("Screenshots are only available over USB/JTAG.");

    /// <summary>
    /// mega65_ftp arguments. "-y" (skip confirmation prompts) only exists in newer builds; the
    /// 1.00 release rejects it and prints its usage, so it is only passed when supported.
    /// "exit" as the last command unloads the helper from the MEGA65's memory.
    /// </summary>
    public static (string Tool, List<string> Args) FtpArgs(ConnectionTarget t, bool supportsYes, params string[] commands)
    {
        List<string> args = t.Kind switch
        {
            ConnectionKind.Serial => ["-F", .. SerialArgs(t)],
            ConnectionKind.Ethernet => ["-F", "-e", "-i", t.Address],
            _ => throw new InvalidOperationException("Not connected."),
        };
        if (supportsYes) args.Insert(1, "-y");
        foreach (var c in commands) { args.Add("-c"); args.Add(c); }
        args.Add("-c"); args.Add("exit");
        return ("mega65_ftp", args);
    }

    /// <summary>True when the help text lists the "-y" option.</summary>
    public static bool HelpListsYesOption(string helpText) =>
        helpText.Split('\n').Any(l => l.TrimStart().StartsWith("-y ", StringComparison.Ordinal));

    private bool? _ftpSupportsYes;

    private async Task<bool> FtpSupportsYesAsync(CancellationToken ct)
    {
        if (_ftpSupportsYes is { } known) return known;
        var exe = tools.PathOf("mega65_ftp") ?? throw new FileNotFoundException("mega65_ftp is not installed.");
        var help = await runner.RunAsync(exe, ["-h"], TimeSpan.FromSeconds(10), null, ct);
        return (_ftpSupportsYes = HelpListsYesOption(help.StdOut + help.StdErr)).Value;
    }

    // ---- execution ----

    public Task<ProcessResult> ResetAsync(ConnectionTarget t, CancellationToken ct = default) => Run(ResetArgs(t), Short, ct);
    public Task<ProcessResult> SetModeAsync(ConnectionTarget t, MachineMode m, CancellationToken ct = default) => Run(ModeArgs(t, m), Short, ct);
    public Task<ProcessResult> SendPrgAsync(ConnectionTarget t, string prg, bool run, bool c64, CancellationToken ct = default) => Run(SendPrgArgs(t, prg, run, c64), Long, ct);
    public Task<ProcessResult> SendPrgAsync(ConnectionTarget t, string prg, PrgOptions options, CancellationToken ct = default) => Run(SendPrgArgs(t, prg, options), Long, ct);
    public Task<ProcessResult> Go64Async(ConnectionTarget t, CancellationToken ct = default) => Run(Go64Args(t), Short, ct);
    public Task<ProcessResult> Go65Async(ConnectionTarget t, CancellationToken ct = default) => TypeBasicAsync(t, Go65Command, ct);

    /// <summary>Runs m65 -d until <paramref name="stop"/> is cancelled (then m65 is ended and OperationCanceledException thrown).</summary>
    public Task<ProcessResult> ServeD81Async(ConnectionTarget t, string d81, IProgress<string>? output, CancellationToken stop) =>
        Run(VirtualD81Args(t, d81), TimeSpan.FromHours(24), stop, output);
    public Task<ProcessResult> ScreenshotAsync(ConnectionTarget t, string png, CancellationToken ct = default) => Run(ScreenshotArgs(t, png), Short, ct);
    public Task<ProcessResult> SendSidAsync(ConnectionTarget t, string sid, CancellationToken ct = default) => Run(SendSidArgs(t, sid), Long, ct);
    public Task<ProcessResult> LoadBitstreamAsync(ConnectionTarget t, string bit, CancellationToken ct = default, IProgress<string>? output = null) => Run(LoadBitstreamArgs(t, bit), Long, ct, output);
    public Task<ProcessResult> LoadHickupAsync(ConnectionTarget t, string file, CancellationToken ct = default) => Run(LoadHickupArgs(t, file), Long, ct);
    public Task<ProcessResult> LoadRomAsync(ConnectionTarget t, string rom, CancellationToken ct = default) => Run(LoadRomArgs(t, rom), Long, ct);
    public Task<ProcessResult> SetVideoModeAsync(ConnectionTarget t, bool pal, CancellationToken ct = default) => Run(VideoModeArgs(t, pal), Short, ct);
    public Task<ProcessResult> BootSlotAsync(ConnectionTarget t, int slot, CancellationToken ct = default, IProgress<string>? output = null) => Run(BootSlotArgs(t, slot), Long, ct, output);

    public Task<ProcessResult> TypeBasicAsync(ConnectionTarget t, string listing, CancellationToken ct = default)
    {
        var (tool, args) = TypeTextArgs(t);
        var exe = tools.PathOf(tool) ?? throw new FileNotFoundException($"{tool} is not installed.");
        return runner.RunAsync(exe, args, Long, null, ct, null, PrepareBasicListing(listing));
    }

    public async Task<ProcessResult> RemoteSdAsync(ConnectionTarget t, IProgress<string>? output, CancellationToken ct, params string[] commands) =>
        await Run(FtpArgs(t, await FtpSupportsYesAsync(ct), commands), Long, ct, output);

    /// <summary>
    /// Runs a program in unit-test mode: the program reports results by writing tokens to $D643
    /// ($F0 start, $F2 pass, $F3 fail, $FF all done). m65 exits with the number of failures,
    /// or 254 when the program does not finish within the timeout.
    /// </summary>
    public static (string Tool, List<string> Args) UnitTestArgs(ConnectionTarget t, string prg, int timeoutSeconds, string logFile) =>
        SerialOnly(t, "Running unit tests", "-F", "-r", $"-u{Math.Max(10, timeoutSeconds)}", "-w", logFile, prg);

    public Task<ProcessResult> RunUnitTestAsync(ConnectionTarget t, string prg, int timeoutSeconds, string logFile, IProgress<string>? output = null, CancellationToken ct = default) =>
        Run(UnitTestArgs(t, prg, timeoutSeconds, logFile), TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds) + 60), ct, output);

    /// <summary>
    /// Makes an .mcs flash file (for Vivado) with the official bit2mcs: from a .bit, or from a .cor
    /// placed at a flash slot (slot n starts at n × $800000).
    /// </summary>
    public static (string Tool, List<string> Args) Bit2McsArgs(string input, string output, int? slot) =>
        slot is { } n
            ? n is >= 0 and <= 7 ? ("bit2mcs", [input, output, (n * 0x800000).ToString("X", System.Globalization.CultureInfo.InvariantCulture)]) : throw new ArgumentOutOfRangeException(nameof(slot))
            : ("bit2mcs", [input, output]);

    public Task<ProcessResult> BuildMcsAsync(string input, string output, int? slot, CancellationToken ct = default) =>
        Run(Bit2McsArgs(input, output, slot), TimeSpan.FromMinutes(2), ct);

    /// <summary>Builds a .cor core file from a .bit bitstream with the official bit2core tool.</summary>
    public Task<ProcessResult> BuildCoreAsync(string target, string bitPath, string name, string version, string outPath, CancellationToken ct = default) =>
        Run(("bit2core", Cores.CoreFile.Bit2CoreArgs(target, bitPath, name, version, outPath)), TimeSpan.FromMinutes(2), ct);

    /// <summary>Discovers MEGA65s on the LAN that have the Ethernet loader enabled (SHIFT+£).</summary>
    public async Task<IReadOnlyList<string>> DiscoverEthernetAsync(CancellationToken ct = default)
    {
        var r = await Run(("etherload", ["-D"]), TimeSpan.FromSeconds(15), ct);
        return ParseDiscovery(r.StdOut);
    }

    /// <summary>etherload prints "MEGA65 found at fe80::...%eth0" (etherload.c discover_mega65).</summary>
    public static IReadOnlyList<string> ParseDiscovery(string stdout) =>
        stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("MEGA65 found at ", StringComparison.Ordinal))
            .Select(l => l["MEGA65 found at ".Length..].Trim())
            .ToList();

    /// <summary>
    /// Runs mega65_ftp commands with <paramref name="workingDirectory"/> as the local folder
    /// ("get" saves there, "put" reads from there). Each run loads a helper into the MEGA65's
    /// memory and resets the machine when it exits.
    /// </summary>
    public async Task<ProcessResult> FtpAsync(ConnectionTarget t, string workingDirectory, IProgress<string>? output, CancellationToken ct, params string[] commands)
    {
        var (tool, args) = FtpArgs(t, await FtpSupportsYesAsync(ct), commands);
        var exe = tools.PathOf(tool) ?? throw new FileNotFoundException("mega65_ftp is not installed. Install the device tools first.");
        return await runner.RunAsync(exe, args, Long, output, ct, workingDirectory);
    }

    /// <summary>Opens an interactive mega65_ftp session (holds the port until disposed).</summary>
    public async Task<FtpSession> OpenFtpSessionAsync(ConnectionTarget t, CancellationToken ct = default)
    {
        var (tool, args) = FtpArgs(t, await FtpSupportsYesAsync(ct));
        args.RemoveRange(args.Count - 2, 2); // no "-c exit": the session stays at the prompt
        var exe = tools.PathOf(tool) ?? throw new FileNotFoundException("mega65_ftp is not installed. Install the device tools first.");
        return await FtpSession.StartAsync(exe, args, ct);
    }

    private Task<ProcessResult> Run((string Tool, List<string> Args) cmd, TimeSpan timeout, CancellationToken ct, IProgress<string>? output = null)
    {
        var exe = tools.PathOf(cmd.Tool) ?? throw new FileNotFoundException($"{cmd.Tool} is not installed. Install the MEGA65 tools first.");
        return runner.RunAsync(exe, cmd.Args, timeout, output, ct);
    }
}
