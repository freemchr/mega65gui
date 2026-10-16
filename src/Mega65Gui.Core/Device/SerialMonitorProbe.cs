using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;

namespace Mega65Gui.Core.Device;

/// <summary>
/// Reads hardware model, core build and ROM version from the MEGA65 serial monitor
/// (2,000,000 baud, 8N1). It only issues read commands ("h" and "m"). This is a clean-room
/// implementation from documented register addresses, not code taken from M65Connect.
///
/// HARDWARE-UNVERIFIED: the response formats below were derived from mega65-tools source
/// (m65common.c) and have not yet been confirmed against a physical machine.
/// </summary>
public sealed partial class SerialMonitorProbe
{
    public const int BaudRate = 2_000_000;

    public static string[] ListPorts() => SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<DeviceState> ProbeAsync(string comPort, CancellationToken ct = default)
    {
        using var port = new SerialPort(comPort, BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.RequestToSend,
            ReadTimeout = 500,
            WriteTimeout = 1000,
            NewLine = "\r",
            Encoding = Encoding.ASCII,
        };
        port.Open();
        port.DiscardInBuffer();

        // "\x0f#\r" resynchronises the monitor console before the first command (m65common.c).
        var help = await CommandAsync(port, "\x0f#\rh", ct);
        var model = await CommandAsync(port, "mffd3629 1", ct);
        var rom = await CommandAsync(port, "m20016 7", ct);

        var state = Parse(help, model, rom) with { Connection = ConnectionTarget.Serial(comPort) };
        return state;
    }

    private static async Task<string> CommandAsync(SerialPort port, string cmd, CancellationToken ct)
    {
        port.Write(cmd + "\r");
        var sb = new StringBuilder();
        var quiet = 0;
        while (quiet < 3 && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, ct);
            var chunk = port.ReadExisting();
            if (chunk.Length == 0) quiet++; else { quiet = 0; sb.Append(chunk); }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"build\s+GIT:\s*(?<build>[^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BuildLine();

    // A git hash, not a build date: 7+ hex digits including at least one letter.
    [GeneratedRegex(@"\b(?=[0-9a-f]*[a-f])[0-9a-f]{7,40}\b", RegexOptions.IgnoreCase)]
    private static partial Regex GitHash();

    // Monitor memory dump line, e.g. ":0FFD3629:06" — hex pairs follow the second colon.
    [GeneratedRegex(@":(?<addr>[0-9A-F]{7,8}):(?<bytes>[0-9A-F ]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DumpLine();

    internal static DeviceState Parse(string helpText, string modelDump, string romDump)
    {
        string? build = BuildLine().Match(helpText) is { Success: true } b ? b.Groups["build"].Value.Trim() : null;
        string? hash = build is not null && GitHash().Match(build) is { Success: true } h ? h.Value.ToLowerInvariant() : null;

        var modelBytes = FirstBytes(modelDump);
        var model = modelBytes.Length > 0 ? HardwareModels.FromModelByte(modelBytes[0]) : HardwareModel.Unknown;

        var romBytes = FirstBytes(romDump);
        string? rom = romBytes.Length >= 7 ? Encoding.ASCII.GetString(romBytes, 0, 7).Trim('\0', ' ') : null;
        if (rom is not null && !rom.All(c => c is >= ' ' and <= '~')) rom = null;

        return new DeviceState(null, model, model == HardwareModel.Unknown ? Provenance.NotKnown : Provenance.ReadFromDevice,
            build, hash, rom, $"{helpText}\n{modelDump}\n{romDump}");
    }

    private static byte[] FirstBytes(string dump)
    {
        var m = DumpLine().Match(dump);
        if (!m.Success) return [];
        var hex = m.Groups["bytes"].Value.Replace(" ", "");
        return Enumerable.Range(0, hex.Length / 2)
            .Select(i => byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
    }
}
