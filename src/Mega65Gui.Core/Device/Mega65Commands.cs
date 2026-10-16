using Mega65Gui.Core.Common;
using Mega65Gui.Core.Tools;

namespace Mega65Gui.Core.Device;

public enum MachineMode { Mega65, C64 }

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
    public static (string Tool, List<string> Args) SendPrgArgs(ConnectionTarget t, string prg, bool run, bool c64Mode) => t.Kind switch
    {
        ConnectionKind.Serial => ("m65", [.. SerialArgs(t), "-F", .. (c64Mode ? new[] { "-4" } : []), .. (run ? new[] { "-r" } : []), prg]),
        ConnectionKind.Ethernet => ("etherload", ["-i", t.Address, .. (c64Mode ? new[] { "-4" } : ["-5"]), .. (run ? new[] { "-r" } : []), prg]),
        _ => throw new InvalidOperationException("Not connected."),
    };

    public static (string Tool, List<string> Args) ScreenshotArgs(ConnectionTarget t, string pngPath) => t.Kind == ConnectionKind.Serial
        ? ("m65", [.. SerialArgs(t), "-S" + pngPath])
        : throw new NotSupportedException("Screenshots are only available over USB/JTAG.");

    public static (string Tool, List<string> Args) FtpArgs(ConnectionTarget t, params string[] commands)
    {
        List<string> args = t.Kind switch
        {
            ConnectionKind.Serial => ["-F", "-y", .. SerialArgs(t)],
            ConnectionKind.Ethernet => ["-F", "-y", "-e", "-i", t.Address],
            _ => throw new InvalidOperationException("Not connected."),
        };
        foreach (var c in commands) { args.Add("-c"); args.Add(c); }
        args.Add("-c"); args.Add("quit");
        return ("mega65_ftp", args);
    }

    // ---- execution ----

    public Task<ProcessResult> ResetAsync(ConnectionTarget t, CancellationToken ct = default) => Run(ResetArgs(t), Short, ct);
    public Task<ProcessResult> SetModeAsync(ConnectionTarget t, MachineMode m, CancellationToken ct = default) => Run(ModeArgs(t, m), Short, ct);
    public Task<ProcessResult> SendPrgAsync(ConnectionTarget t, string prg, bool run, bool c64, CancellationToken ct = default) => Run(SendPrgArgs(t, prg, run, c64), Long, ct);
    public Task<ProcessResult> ScreenshotAsync(ConnectionTarget t, string png, CancellationToken ct = default) => Run(ScreenshotArgs(t, png), Short, ct);

    public Task<ProcessResult> RemoteSdAsync(ConnectionTarget t, IProgress<string>? output, CancellationToken ct, params string[] commands) =>
        Run(FtpArgs(t, commands), Long, ct, output);

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

    private Task<ProcessResult> Run((string Tool, List<string> Args) cmd, TimeSpan timeout, CancellationToken ct, IProgress<string>? output = null)
    {
        var exe = tools.PathOf(cmd.Tool) ?? throw new FileNotFoundException($"{cmd.Tool} is not installed. Install the MEGA65 tools first.");
        return runner.RunAsync(exe, cmd.Args, timeout, output, ct);
    }
}
