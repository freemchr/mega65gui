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
        _monitor.Received += s => Application.Current.Dispatcher.BeginInvoke(() => Append(s));
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

    private void Append(string s)
    {
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

    /// <summary>Presses a key on the MEGA65 (remote keyboard).</summary>
    public async Task PressAsync((byte Key, byte Modifier)? key)
    {
        if (_monitor is null || key is not { } k) return;
        try { await _monitor.PressAsync(k.Key, k.Modifier); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException) { Status = e.Message; }
    }

    public Task OnHiddenAsync() { Close(); return Task.CompletedTask; }
}
