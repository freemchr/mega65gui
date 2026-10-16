using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Device;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

public sealed partial class DeviceViewModel : PageViewModel
{
    private readonly AppServices services;

    public DeviceViewModel(AppServices services) : base("Device", "\uE7F4")
    {
        this.services = services;
        services.ToolsChanged += () => System.Windows.Application.Current.Dispatcher.Invoke(UpdateTools);
        UpdateTools();

        // Watch the COM port: the USB/JTAG adapter disappears from Windows when the MEGA65 is
        // switched off or unplugged. Only the list of port names is checked; the port is not opened.
        _watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watch.Tick += async (_, _) => await WatchPortAsync();
        _watch.Start();
    }

    private readonly System.Windows.Threading.DispatcherTimer _watch;
    private string? _lostPort;
    private DateTime _portBackAt;

    private async Task WatchPortAsync()
    {
        var ports = SerialMonitorProbe.ListPorts();

        if (services.Connection is { Kind: ConnectionKind.Serial } c && !ports.Contains(c.Address, StringComparer.OrdinalIgnoreCase))
        {
            _lostPort = c.Address;
            services.LostPort = c.Address;
            SetDisconnected($"Disconnected: {c.Address} has gone. The MEGA65 was switched off or its USB cable unplugged. " +
                            "It reconnects automatically when it comes back.");
            return;
        }

        if (_lostPort is null || IsBusy) return;
        if (!ports.Contains(_lostPort, StringComparer.OrdinalIgnoreCase)) { _portBackAt = default; return; }

        // Give the MEGA65 a few seconds to start its core before probing.
        if (_portBackAt == default) { _portBackAt = DateTime.Now; ConnectionText = $"{_lostPort} is back. Waiting for the MEGA65 to start…"; return; }
        if (DateTime.Now - _portBackAt < TimeSpan.FromSeconds(4)) return;

        var port = _lostPort;
        _lostPort = null;
        _portBackAt = default;
        RefreshPorts();
        SelectedPort = port;
        services.LostPort = null;
        await ConnectSerialAsync();
    }

    private void SetDisconnected(string message)
    {
        services.Connection = null;
        services.Device = DeviceState.Unknown;
        IsConnected = false;
        ConnectionText = "Disconnected";
        DeviceText = message;
        RefreshPorts();
        (System.Windows.Application.Current.MainWindow?.DataContext as MainViewModel)?.Shell.Refresh();
    }

    [ObservableProperty] private bool _toolsMissing;
    [ObservableProperty] private bool _installingTools;

    private void UpdateTools()
    {
        ToolsMissing = services.Tools.Current is null;
        InstallingTools = services.IsInstallingTools;
    }

    [RelayCommand]
    private Task InstallToolsAsync() => GuardAsync(async () =>
    {
        await services.InstallDeviceToolsAsync(progress: new Progress<string>(p => Status = p));
        Status = "Device tools installed.";
    }, "Installing device tools…");

    public ObservableCollection<string> Ports { get; } = [];
    public ObservableCollection<string> EthernetDevices { get; } = [];
    public ObservableCollection<string> Output { get; } = [];

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private string? _selectedEthernet;
    [ObservableProperty] private string _connectionText = "Not connected";
    [ObservableProperty] private string _deviceText = "Plug in the USB/JTAG cable, choose its COM port and press Connect. The model, core and ROM are read automatically.";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _runAfterLoad = true;
    [ObservableProperty] private bool _c64Mode;
    [ObservableProperty] private string _sdCommand = "dir";

    public override Task OnShownAsync()
    {
        RefreshPorts();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        foreach (var p in SerialMonitorProbe.ListPorts()) Ports.Add(p);
        SelectedPort ??= Ports.LastOrDefault();
    }

    [RelayCommand]
    private Task ConnectSerialAsync() => GuardAsync(async () =>
    {
        if (SelectedPort is null) { Status = "Choose a COM port."; return; }
        _lostPort = null;
        services.LostPort = null;
        var target = ConnectionTarget.Serial(SelectedPort);
        var state = await Task.Run(() => new DeviceProbe().ProbeAsync(target));
        SetConnected(target, state);
    }, "Reading device state over USB/JTAG…");

    [RelayCommand]
    private Task DiscoverEthernetAsync() => GuardAsync(async () =>
    {
        var cmds = RequireTools();
        EthernetDevices.Clear();
        foreach (var d in await cmds.DiscoverEthernetAsync()) EthernetDevices.Add(d);
        SelectedEthernet = EthernetDevices.FirstOrDefault();
        Status = EthernetDevices.Count == 0
            ? "No MEGA65 found. Press SHIFT+£ on the MEGA65 to enable the Ethernet loader (core 0.96 or later)."
            : $"Found {EthernetDevices.Count} MEGA65.";
    }, "Searching the local network…");

    [RelayCommand]
    private void ConnectEthernet()
    {
        if (SelectedEthernet is null) return;
        SetConnected(ConnectionTarget.Ethernet(SelectedEthernet), DeviceState.Unknown);
    }

    private void SetConnected(ConnectionTarget target, DeviceState state)
    {
        services.Connection = target;
        services.Device = state with { Connection = target };
        IsConnected = true;
        (System.Windows.Application.Current.MainWindow?.DataContext as MainViewModel)?.Shell.Refresh();
        ConnectionText = target.ToString();
        DeviceText = services.Device.NoResponse
            ? $"The MEGA65 did not answer on {target.Address}. The port opened, but nothing came back.\n\n" +
              "Check that:\n" +
              " • the MEGA65 is switched on and showing its normal screen (not MEGAFLASH or a black screen);\n" +
              " • the JTAG adapter is firmly seated on the MEGA65's JTAG header and its USB cable is a data cable;\n" +
              " • no other program (M65Connect, a terminal, another m65) is using the port;\n" +
              " • the adapter's DIP switches are set as described in the MEGA65 User's Guide.\n" +
              "Then press Connect again."
            : target.Kind == ConnectionKind.Ethernet
            ? "Connected over Ethernet. Model and core cannot be read over Ethernet with the official tools."
            : $"Model: {state.Model} ({state.ModelSource})\nRunning core: {state.CoreBuild ?? "not read"}\nROM: {state.RomVersion ?? "not read"}\n\n" +
              "This is the core running right now. If the MEGA65 started from the factory core (slot 0), slot 1 may hold a newer one; MEGAFLASH shows each slot.";
        Status = "";
    }

    [RelayCommand] private Task ResetAsync() => RunToolAsync((c, t) => c.ResetAsync(t), "Resetting…");
    [RelayCommand] private Task GoC64Async() => RunToolAsync((c, t) => c.SetModeAsync(t, MachineMode.C64), "Switching to C64 mode…");
    [RelayCommand] private Task GoMega65Async() => RunToolAsync((c, t) => c.SetModeAsync(t, MachineMode.Mega65), "Switching to MEGA65 mode…");

    [RelayCommand]
    private Task SendPrgAsync()
    {
        var dlg = new OpenFileDialog { Filter = "C64/MEGA65 programs (*.prg)|*.prg" };
        return dlg.ShowDialog() == true ? SendFileAsync(dlg.FileName) : Task.CompletedTask;
    }

    /// <summary>Drag-and-drop entry point; routes by file type.</summary>
    public Task SendFileAsync(string path)
    {
        if (Path.GetExtension(path).Equals(".prg", StringComparison.OrdinalIgnoreCase))
            return RunToolAsync((c, t) => c.SendPrgAsync(t, path, RunAfterLoad, C64Mode), $"Sending {Path.GetFileName(path)}…");
        Status = $"{Path.GetExtension(path)} files are not supported yet. See the Features page.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ScreenshotAsync()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), $"MEGA65-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        return RunToolAsync(async (c, t) =>
        {
            var r = await c.ScreenshotAsync(t, file);
            if (r.Succeeded && File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return r;
        }, "Capturing screenshot…");
    }

    [RelayCommand]
    private Task RunSdCommandAsync()
    {
        var cmd = SdCommand.Trim();
        if (cmd.Length == 0) return Task.CompletedTask;
        var progress = new Progress<string>(l => Output.Add(l));
        Output.Add($"> {cmd}");
        return RunToolAsync((c, t) => c.RemoteSdAsync(t, progress, CancellationToken.None, cmd), $"mega65_ftp: {cmd}", echo: false);
    }

    private Mega65Commands RequireTools() =>
        services.Commands ?? throw new InvalidOperationException("The device tools are not installed. Click Install device tools at the top of this page.");

    private Task RunToolAsync(Func<Mega65Commands, ConnectionTarget, Task<ProcessResult>> op, string busy, bool echo = true) => GuardAsync(async () =>
    {
        var target = services.Connection ?? throw new InvalidOperationException("Connect to a MEGA65 first.");
        var r = await op(RequireTools(), target);
        if (echo)
            foreach (var line in (r.StdOut + r.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(40))
                Output.Add(line.TrimEnd());
        Status = r.Succeeded ? "Done." : $"The tool exited with code {r.ExitCode}. See the output below.";
    }, busy);
}
