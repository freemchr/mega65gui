using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Device;

namespace Mega65Gui.App.ViewModels;

/// <summary>Serial monitor terminal and remote keyboard (USB/JTAG). Holds the port while open.</summary>
public sealed partial class TerminalViewModel : PageViewModel
{
    private readonly AppServices services;
    private MonitorConnection? _monitor;
    private volatile bool _quiet;
    private readonly StringBuilder _text = new();

    public TerminalViewModel(AppServices services) : base("Terminal", "")
    {
        this.services = services;
        services.ReleasePortRequested += () => Application.Current.Dispatcher.Invoke(Close);
    }

    [ObservableProperty] private string _transcript = "";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _keyboardActive;

    public event Action? TranscriptAppended;

    [RelayCommand]
    private Task OpenAsync() => GuardAsync(async () =>
    {
        if (services.Connection is not { Kind: ConnectionKind.Serial } c)
            throw new InvalidOperationException("The terminal needs a USB/JTAG connection. Connect on the Device or Update page first.");
        await services.ReleasePortAsync();
        _monitor = MonitorConnection.Open(c.Address);
        _monitor.Received += s => { if (!_quiet) Application.Current.Dispatcher.BeginInvoke(() => Append(s)); };
        IsOpen = true;
        Append($"--- connected to the MEGA65 serial monitor on {c.Address}. Type h for help. ---\n");
        await _monitor.SendCommandAsync("h");
        Status = "";
    }, "Opening the serial monitor…");

    public void Close()
    {
        if (_monitor is null) return;
        _monitor.Dispose();
        _monitor = null;
        IsOpen = false;
        KeyboardActive = false;
        Append("--- disconnected ---\n");
    }

    [RelayCommand] private void Disconnect() => Close();

    [RelayCommand]
    private void Clear() { _text.Clear(); Transcript = ""; }

    [RelayCommand]
    private void SaveTranscript()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save the terminal text", Filter = "Text files (*.txt)|*.txt", FileName = $"mega65-terminal-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, _text.ToString().Replace("\n", Environment.NewLine));
            Status = $"Saved the terminal text to {dlg.FileName}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = $"Cannot save: {e.Message}"; }
    }

    private bool _lastWasCr;

    private void Append(string s)
    {
        // CR LF can arrive split across two reads; a LF right after a CR is the same line end.
        if (_lastWasCr && s.StartsWith('\n')) s = s[1..];
        _lastWasCr = s.EndsWith('\r');
        _text.Append(s.Replace("\r\n", "\n").Replace('\r', '\n'));
        if (_text.Length > 200_000) _text.Remove(0, _text.Length - 150_000);
        Transcript = _text.ToString();
        TranscriptAppended?.Invoke();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (_monitor is null || Command.Length == 0) return;
        var cmd = Command;
        Command = "";
        await _monitor.SendCommandAsync(cmd);
    }

    /// <summary>Sends one of the quick-command buttons (h, r, …).</summary>
    [RelayCommand]
    private async Task QuickAsync(string cmd)
    {
        if (_monitor is null) return;
        await _monitor.SendCommandAsync(cmd);
    }

    // ===== Developer: memory dump and breakpoints =====

    [ObservableProperty] private string _dumpStart = "2001";
    [ObservableProperty] private string _dumpLength = "1000";
    [ObservableProperty] private string _breakAddress = "";
    [ObservableProperty] private double _dumpProgress;

    /// <summary>Saves a memory range to a file, read through the monitor (256 bytes per command).</summary>
    [RelayCommand]
    private Task DumpMemoryAsync() => GuardAsync(async () =>
    {
        if (_monitor is null) throw new InvalidOperationException("Open the terminal first.");
        if (!MemoryDump.TryParseAddress(DumpStart, out var start)) throw new InvalidOperationException("Enter the start address in hex, for example 2001 or 20000.");
        if (!MemoryDump.TryParseAddress(DumpLength, out var length) || length == 0 || length > 0x100000)
            throw new InvalidOperationException("Enter the length in hex, from 1 to 100000 (1 MB).");
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save memory dump", FileName = $"mem-{start:X}-{start + length - 1:X}.bin", Filter = "Binary files (*.bin)|*.bin|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        Append($"--- reading ${start:X}-${start + length - 1:X} ({length} bytes) ---\n");
        _quiet = true;
        try
        {
            var data = await MemoryDump.ReadAsync(_monitor, start, (int)length, new Progress<double>(p => DumpProgress = p * 100));
            await File.WriteAllBytesAsync(dlg.FileName, data);
        }
        finally { _quiet = false; DumpProgress = 0; }
        Append($"--- saved {length} bytes to {dlg.FileName} ---\n");
        Status = $"Saved {length} bytes to {Path.GetFileName(dlg.FileName)}.";
    }, "Reading memory…");

    [RelayCommand]
    private async Task SetBreakpointAsync()
    {
        if (_monitor is null) return;
        if (!MemoryDump.TryParseAddress(BreakAddress, out var addr) || addr > 0xFFFF)
        { Status = "Enter the breakpoint address in hex (0000-FFFF), for example E1C5."; return; }
        await _monitor.SendCommandAsync($"b{addr:x}");
        Status = $"Breakpoint set at ${addr:X4}. When the CPU reaches it, it stops; press Registers to see where it is, and Continue to carry on.";
    }

    /// <summary>Clears the breakpoint and lets the CPU run again ("b" then "t0").</summary>
    [RelayCommand]
    private async Task ContinueAsync()
    {
        if (_monitor is null) return;
        await _monitor.SendCommandAsync("b");
        await _monitor.SendCommandAsync("t0");
        Status = "Breakpoint cleared; the CPU is running.";
    }

    // On-screen CTRL, MEGA and ALT: held down for the next key, then released.
    [ObservableProperty] private bool _holdCtrl;
    [ObservableProperty] private bool _holdMega;
    [ObservableProperty] private bool _holdAlt;

    /// <summary>An on-screen key (RUN/STOP, ESC, HELP, F9–F14 …), named as in <see cref="SpecialKey"/>.</summary>
    [RelayCommand]
    private Task PressKeyAsync(string name) => PressAsync(KeyMatrix.For(Enum.Parse<SpecialKey>(name)));

    /// <summary>Presses a key on the MEGA65 (remote keyboard), with an on-screen modifier if one is held.</summary>
    public async Task PressAsync((byte Key, byte Modifier)? key)
    {
        if (_monitor is null || key is not { } k) return;
        var held = HoldCtrl ? KeyMatrix.Ctrl : HoldMega ? KeyMatrix.Mega : HoldAlt ? KeyMatrix.Alt : (byte?)null;
        if (held is { } m) k = KeyMatrix.With(k, m);
        HoldCtrl = HoldMega = HoldAlt = false;
        try { await _monitor.PressAsync(k.Key, k.Modifier); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException) { Status = e.Message; }
    }

    public Task OnHiddenAsync() { Close(); return Task.CompletedTask; }
}
