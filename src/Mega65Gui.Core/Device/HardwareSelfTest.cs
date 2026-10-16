using System.Text;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Tools;

namespace Mega65Gui.Core.Device;

public sealed record SelfTestResult(string Name, bool Passed, string Detail);

/// <summary>
/// End-to-end check of the device features on a real MEGA65 over USB/JTAG, verified by reading
/// the MEGA65's screen back as text (m65 -S0). It resets the MEGA65 several times, overwrites
/// memory, and writes then deletes a file M65GTEST.TXT in the SD card's root.
/// </summary>
public sealed partial class HardwareSelfTest(ToolInstallation tools, IProcessRunner runner, string port)
{
    private readonly ConnectionTarget _target = ConnectionTarget.Serial(port);
    private readonly Mega65Commands _cmds = new(tools, runner);

    public async Task<IReadOnlyList<SelfTestResult>> RunAsync(IProgress<SelfTestResult>? progress = null, CancellationToken ct = default)
    {
        var results = new List<SelfTestResult>();
        async Task Step(string name, Func<Task<(bool, string)>> body)
        {
            SelfTestResult r;
            try { var (ok, detail) = await body(); r = new(name, ok, detail); }
            catch (Exception e) when (e is not OperationCanceledException) { r = new(name, false, e.Message); }
            results.Add(r);
            progress?.Report(r);
        }

        await Step("Connect and read model, core and ROM", async () =>
        {
            var s = await new DeviceProbe().ProbeAsync(_target, ct);
            return (s.Model != HardwareModel.Unknown && s.CoreBuild is not null, $"{s.Model}, {s.CoreBuild ?? "no core"}, ROM {s.RomVersion ?? "?"}");
        });
        await Step("Reset", async () =>
        {
            var r = await _cmds.ResetAsync(_target, ct);
            await Task.Delay(3000, ct);
            return (r.Succeeded && await WaitForScreenAsync("READY.", ct), $"exit {r.ExitCode}");
        });
        await Step("Type BASIC text", async () =>
        {
            var r = await _cmds.TypeBasicAsync(_target, "new\n10 print \"m65gui basic ok\"\nrun\n", ct);
            return (r.Succeeded && await WaitForScreenAsync("M65GUI BASIC OK", ct), $"exit {r.ExitCode}");
        });
        await Step("Remote keyboard", async () =>
        {
            using (var mon = MonitorConnection.Open(port))
            {
                foreach (var c in "?6*7") { var k = KeyMatrix.ForChar(c)!.Value; await mon.PressAsync(k.Key, k.Modifier, ct); }
                var ret = KeyMatrix.For(SpecialKey.Return);
                await mon.PressAsync(ret.Key, ret.Modifier, ct);
            }
            return (await WaitForScreenAsync(" 42", ct), "typed ?6*7 RETURN");
        });
        await Step("Send and run a PRG", async () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "m65gui-selftest.prg");
            await File.WriteAllBytesAsync(path, BasicPrintPrg("M65GUI PRG OK"), ct);
            var r = await _cmds.SendPrgAsync(_target, path, run: true, c64: false, ct);
            return (r.Succeeded && await WaitForScreenAsync("M65GUI PRG OK", ct), $"exit {r.ExitCode}");
        });
        await Step("Switch to C64 mode", async () =>
        {
            var r = await _cmds.SetModeAsync(_target, MachineMode.C64, ct);
            return (r.Succeeded && await WaitForScreenAsync("64K RAM SYSTEM", ct), $"exit {r.ExitCode}");
        });
        await Step("Switch back to MEGA65 mode", async () =>
        {
            var r = await _cmds.SetModeAsync(_target, MachineMode.Mega65, ct);
            return (r.Succeeded && await WaitForScreenAsync("BASIC 65", ct), $"exit {r.ExitCode}");
        });
        await Step("SD card: upload, list, download, compare, delete", () => CardRoundTripAsync(ct));
        return results;
    }

    /// <summary>Upload, list, download, compare and delete one file in the card's root (works with every tools version).</summary>
    /// <summary>
    /// Further checks that leave the MEGA65 as it was: the monitor terminal, a PNG screenshot, the
    /// release's own ROM loaded temporarily, the same HICKUP, and restarting the slot 1 core.
    /// </summary>
    public async Task<IReadOnlyList<SelfTestResult>> RunExtendedAsync(string romPath, string hickupPath, string screenshotPath,
        IProgress<SelfTestResult>? progress = null, CancellationToken ct = default)
    {
        var results = new List<SelfTestResult>();
        async Task Step(string name, Func<Task<(bool, string)>> body)
        {
            SelfTestResult r;
            try { var (ok, detail) = await body(); r = new(name, ok, detail); }
            catch (Exception e) when (e is not OperationCanceledException) { r = new(name, false, e.Message); }
            results.Add(r);
            progress?.Report(r);
        }
        var before = await new DeviceProbe().ProbeAsync(_target, ct);

        await Step("Terminal: monitor answers 'h'", async () =>
        {
            var text = new StringBuilder();
            using (var mon = MonitorConnection.Open(port))
            {
                mon.Received += s => { lock (text) text.Append(s); };
                await mon.SendCommandAsync("h", ct);
                await Task.Delay(1500, ct);
            }
            string got; lock (text) got = text.ToString();
            return (got.Contains("MEGA65 Serial Monitor", StringComparison.OrdinalIgnoreCase), $"{got.Length} characters received");
        });
        await Step("Screenshot to PNG", async () =>
        {
            File.Delete(screenshotPath);
            var r = await _cmds.ScreenshotAsync(_target, screenshotPath, ct);
            var size = File.Exists(screenshotPath) ? new FileInfo(screenshotPath).Length : 0;
            return (r.Succeeded && size > 1000, $"{size:N0} bytes");
        });
        await Step("Start with a ROM from the PC (same version)", async () =>
        {
            var r = await _cmds.LoadRomAsync(_target, romPath, ct);
            var ready = await WaitForScreenAsync("BASIC 65", ct);
            var after = await new DeviceProbe().ProbeAsync(_target, ct);
            return (r.Succeeded && ready && after.RomVersion == before.RomVersion, $"ROM now {after.RomVersion}");
        });
        await Step("Load HICKUP (same version)", async () =>
        {
            var r = await _cmds.LoadHickupAsync(_target, hickupPath, ct);
            return (r.Succeeded && await WaitForScreenAsync("BASIC 65", ct), $"exit {r.ExitCode}");
        });
        await Step("Start flash slot 1 (the running core)", async () =>
        {
            var r = await _cmds.BootSlotAsync(_target, 1, ct);
            await Task.Delay(8000, ct);
            var after = await new DeviceProbe().ProbeAsync(_target, ct);
            return (r.Succeeded && after.CoreBuild == before.CoreBuild, $"core now {after.CoreBuild}");
        });
        return results;
    }

    private async Task<(bool, string)> CardRoundTripAsync(CancellationToken ct)
    {
        const string name = "M65GTEST.TXT";
        var payload = Encoding.ASCII.GetBytes($"MEGA65 GUI self-test {DateTime.Now:O}\n");
        var up = Path.Combine(Path.GetTempPath(), name);
        var down = Path.Combine(Path.GetTempPath(), "m65gtest-back.txt");
        await File.WriteAllBytesAsync(up, payload, ct);
        File.Delete(down);
        await using var s = await _cmds.OpenFtpSessionAsync(_target, ct);
        var steps = new List<FtpReply> { await s.UploadAsync(up, name, ct) };
        var listed = (await s.ListAsync(ct)).Any(e => e.Matches(name) && e.Size == payload.Length);
        steps.Add(await s.DownloadAsync(name, down, ct));
        var same = File.Exists(down) && (await File.ReadAllBytesAsync(down, ct)).SequenceEqual(payload);
        steps.Add(await s.DeleteAsync(name, ct));
        var gone = !(await s.ListAsync(ct)).Any(e => e.Matches(name));
        var errors = steps.SelectMany(r => r.Errors).ToList();
        return (listed && same && gone && errors.Count == 0,
            $"listed={listed}, identical={same}, deleted={gone}{(errors.Count > 0 ? ", errors: " + string.Join("; ", errors) : "")}");
    }

    /// <summary>Reads the MEGA65 screen as text until it contains <paramref name="text"/>.</summary>
    private async Task<bool> WaitForScreenAsync(string text, CancellationToken ct, int seconds = 40)
    {
        var until = DateTime.Now.AddSeconds(seconds);
        do
        {
            if ((await ReadScreenAsync(ct)).Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            await Task.Delay(1000, ct);
        } while (DateTime.Now < until);
        return false;
    }

    // m65 -S0 renders each character with ANSI colour codes, e.g. "ESC[38;2;240;240;240mR".
    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial System.Text.RegularExpressions.Regex Ansi();

    internal static string PlainScreen(string s) => Ansi().Replace(s, "");

    public async Task<string> ReadScreenAsync(CancellationToken ct = default)
    {
        var r = await runner.RunAsync(tools.PathOf("m65")!, [.. Mega65Commands.SerialArgs(_target), "-S0"], TimeSpan.FromSeconds(40), null, ct);
        return PlainScreen(r.StdOut);
    }

    /// <summary>A one-line BASIC 65 program at $2001: 10 PRINT "text".</summary>
    internal static byte[] BasicPrintPrg(string text)
    {
        var line = new List<byte> { 10, 0, 0x99, (byte)' ', (byte)'"' };
        line.AddRange(Encoding.ASCII.GetBytes(text));
        line.AddRange(new byte[] { (byte)'"', 0 });
        var next = 0x2001 + 2 + line.Count;
        return [0x01, 0x20, (byte)next, (byte)(next >> 8), .. line, 0, 0];
    }
}
