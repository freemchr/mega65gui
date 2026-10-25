using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Device;
using System.IO;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

public sealed partial class DeviceViewModel : PageViewModel
{
    private readonly AppServices services;

    public DeviceViewModel(AppServices services) : base("Device", "\uE7F4")
    {
        this.services = services;
        services.ToolsChanged += () => System.Windows.Application.Current.Dispatcher.Invoke(UpdateTools);
        services.ConnectionChanged += ShowConnection;
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
            services.SetConnection(null, DeviceState.Unknown, c.Address);
            RefreshPorts();
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
        await ConnectSerialAsync();
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

    public ObservableCollection<SerialPortInfo> Ports { get; } = [];
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
        var keep = SelectedPort;
        Ports.Clear();
        foreach (var p in SerialPorts.List()) Ports.Add(p);
        SelectedPort = keep is not null && Ports.Any(p => p.Name == keep) ? keep : SerialPorts.Preferred(Ports);
    }

    [RelayCommand]
    private Task ConnectSerialAsync() => GuardAsync(async () =>
    {
        if (SelectedPort is null)
        {
            Status = Ports.Any(p => p.CouldBeMega65) ? "Choose the MEGA65's COM port."
                : "No MEGA65 found over USB/JTAG. Switch it on and plug in the USB cable, then press the refresh button.";
            return;
        }
        _lostPort = null;
        await services.ConnectSerialAsync(SelectedPort);
        Status = "";
    }, "Reading device state over USB/JTAG…");

    [RelayCommand]
    private Task DiscoverEthernetAsync() => GuardAsync(async () =>
    {
        var cmds = RequireTools();
        EthernetDevices.Clear();
        foreach (var d in await cmds.DiscoverEthernetAsync()) EthernetDevices.Add(d);
        SelectedEthernet = EthernetDevices.FirstOrDefault();
        Status = EthernetDevices.Count == 0
            ? NetworkCheck.NothingFoundMessage(NetworkCheck.WiredAdaptersWithoutIpv6())
            : $"Found {EthernetDevices.Count} MEGA65.";
    }, "Searching the local network…");

    [RelayCommand]
    private void ConnectEthernet()
    {
        if (SelectedEthernet is null) return;
        services.SetConnection(ConnectionTarget.Ethernet(SelectedEthernet), DeviceState.Unknown);
    }

    /// <summary>Shows the shared connection state (set here, by Update My MEGA65 or by the port watcher).</summary>
    private void ShowConnection()
    {
        var target = services.Connection;
        var state = services.Device;
        IsConnected = target is not null;
        if (target is null)
        {
            ConnectionText = services.LostPort is null ? "Not connected" : "Disconnected";
            DeviceText = services.LostPort is { } lost
                ? $"Disconnected: {lost} has gone. The MEGA65 was switched off or its USB cable unplugged. It reconnects automatically when it comes back."
                : "Plug in the USB/JTAG cable, choose its COM port and press Connect. The model, core and ROM are read automatically.";
            return;
        }
        if (target.Kind == ConnectionKind.Serial) SelectedPort = target.Address;
        ConnectionText = target.ToString();
        DeviceText = state.NoResponse
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
    }

    [RelayCommand]
    private Task SelfTestAsync()
    {
        if (services.Connection is not { Kind: ConnectionKind.Serial } c) { Status = "Connect over USB/JTAG first."; return Task.CompletedTask; }
        if (!Confirm("Hardware self-test",
                "Run the self-test on your MEGA65?\n\nIt resets the MEGA65 several times, types and runs small BASIC programs (anything in memory is lost), " +
                "switches to C64 mode and back, and creates then deletes a folder M65GTEST on the SD card. It takes about two minutes."))
            return Task.CompletedTask;
        return GuardAsync(async () =>
        {
            await services.ReleasePortAsync();
            Output.Clear();
            var progress = new Progress<SelfTestResult>(r => Output.Add($"{(r.Passed ? "PASS" : "FAIL")}  {r.Name}  ({r.Detail})"));
            var results = await new HardwareSelfTest(services.Tools.Current ?? throw new InvalidOperationException("Install the device tools first."),
                services.Runner, c.Address).RunAsync(progress);
            Status = $"Self-test: {results.Count(r => r.Passed)} passed, {results.Count(r => !r.Passed)} failed.";
        }, "Running the hardware self-test…");
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
        var name = Path.GetFileName(path);
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".prg":
                return RunToolAsync((c, t) => c.SendPrgAsync(t, path, RunAfterLoad, C64Mode), $"Sending {name}…");
            case ".sid":
                return RunToolAsync((c, t) => c.SendSidAsync(t, path), $"Playing {name}…");
            case ".bas" or ".txt" or ".lst":
                return TypeBasicFileAsync(path);
            case ".rom" or ".bin":
                return LoadRomFileAsync(path);
            case ".bit":
                return LoadBitFileAsync(path);
            case ".m65" when name.Contains("HICKUP", StringComparison.OrdinalIgnoreCase):
                return RunToolAsync((c, t) => c.LoadHickupAsync(t, path), $"Loading {name}…");
            case ".d81" or ".d64" or ".cor":
                Status = $"{name}: use the SD Card Browser to copy it to the MEGA65's card.";
                return Task.CompletedTask;
            default:
                Status = $"{name}: this file type is not supported. Drop PRG, SID, BASIC text (.bas/.txt), ROM, BIT or HICKUP.M65 files.";
                return Task.CompletedTask;
        }
    }

    private static string? Pick(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static bool Confirm(string title, string message) =>
        System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow, message, title,
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    [RelayCommand]
    private Task SendSidAsync() => Pick("Play a SID tune", "SID tunes (*.sid)|*.sid") is { } f ? SendFileAsync(f) : Task.CompletedTask;

    [RelayCommand]
    private Task TypeBasicAsync() => Pick("Type a BASIC listing", "BASIC text (*.bas;*.txt;*.lst)|*.bas;*.txt;*.lst|All files|*.*") is { } f ? TypeBasicFileAsync(f) : Task.CompletedTask;

    private Task TypeBasicFileAsync(string path)
    {
        var text = File.ReadAllText(path);
        var lines = text.Split('\n').Count(l => l.Trim().Length > 0);
        if (!Confirm("Type BASIC listing", $"Type {lines} line(s) from {Path.GetFileName(path)} into the MEGA65?\n\n" +
                     "The MEGA65 should be at the READY prompt. Lines are typed as if on the keyboard, so this takes a moment."))
            return Task.CompletedTask;
        return RunToolAsync((c, t) => c.TypeBasicAsync(t, text), $"Typing {Path.GetFileName(path)}…");
    }

    [RelayCommand]
    private Task LoadRomAsync() => Pick("Start with a ROM from this PC", "MEGA65 ROMs (*.rom;*.bin)|*.rom;*.bin") is { } f ? LoadRomFileAsync(f) : Task.CompletedTask;

    private Task LoadRomFileAsync(string path) =>
        Confirm("Start with this ROM", $"Restart the MEGA65 using {Path.GetFileName(path)}?\n\nThe ROM is loaded from this PC only; the SD card is not changed and the normal ROM returns at the next power-on.")
            ? RunToolAsync((c, t) => c.LoadRomAsync(t, path), $"Starting with {Path.GetFileName(path)}…")
            : Task.CompletedTask;

    [RelayCommand]
    private Task LoadBitAsync() => Pick("Load an FPGA bitstream", "Bitstreams (*.bit)|*.bit") is { } f ? LoadBitFileAsync(f) : Task.CompletedTask;

    private Task LoadBitFileAsync(string path) =>
        Confirm("Load bitstream", $"Load {Path.GetFileName(path)} into the FPGA?\n\nThis replaces the running core until the MEGA65 is switched off. Nothing is written to flash.")
            ? RunToolAsync(async (c, t) =>
            {
                var r = await c.LoadBitstreamAsync(t, path);
                if (r.Succeeded) services.NoteCoreReplaced($"{Path.GetFileName(path)} (loaded over JTAG from this PC until power-off)");
                return r;
            }, $"Loading {Path.GetFileName(path)} into the FPGA…")
            : Task.CompletedTask;

    [RelayCommand]
    private Task LoadHickupAsync() => Pick("Load HICKUP (hypervisor)", "HICKUP (HICKUP.M65)|*.m65") is { } f
        ? RunToolAsync((c, t) => c.LoadHickupAsync(t, f), $"Loading {Path.GetFileName(f)}…")
        : Task.CompletedTask;

    /// <summary>Runs a test program in unit-test mode and reports how many tests failed.</summary>
    [RelayCommand]
    private Task RunUnitTestAsync()
    {
        if (Pick("Run a unit-test program", "Programs (*.prg)|*.prg") is not { } f) return Task.CompletedTask;
        var log = Path.Combine(services.Paths.Reports, $"unittest-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        return GuardAsync(async () =>
        {
            var target = services.Connection ?? throw new InvalidOperationException("Connect to a MEGA65 first.");
            await services.ReleasePortAsync();
            var r = await RequireTools().RunUnitTestAsync(target, f, 60, log);
            foreach (var line in (r.StdOut + r.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(60))
                Output.Add(line.TrimEnd());
            Status = r.ExitCode switch
            {
                0 => $"All tests passed. Log: {log}",
                254 or 255 or -1 => $"The program did not report that it finished within 60 seconds. Log: {log}",
                var n => $"{n} test(s) failed. Log: {log}",
            };
        }, $"Running {Path.GetFileName(f)} in unit-test mode…");
    }

    [RelayCommand] private Task SetPalAsync() => RunToolAsync((c, t) => c.SetVideoModeAsync(t, pal: true), "Switching to PAL…");
    [RelayCommand] private Task SetNtscAsync() => RunToolAsync((c, t) => c.SetVideoModeAsync(t, pal: false), "Switching to NTSC…");

    public IReadOnlyList<int> Slots { get; } = [0, 1, 2, 3, 4, 5, 6, 7];
    [ObservableProperty] private int _selectedSlot = 1;

    [RelayCommand]
    private async Task BootSlotAsync()
    {
        var slot = SelectedSlot;
        if (!Confirm("Start flash slot", $"Restart the MEGA65 from the core in slot {slot}?{(slot == 0 ? " Slot 0 is the factory core." : "")}\n\nNothing is written to flash."))
            return;
        await RunToolAsync((c, t) => c.BootSlotAsync(t, slot), $"Starting slot {slot}…");
        // The new core takes a few seconds to start; read it again so the page shows what is running.
        if (services.Connection is { Kind: ConnectionKind.Serial } conn)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            await GuardAsync(async () => { await services.ConnectSerialAsync(conn.Address); Status = $"Slot {slot} started."; }, "Reading the new core…");
        }
    }

    // ---- PRG autoload: send a program whenever it changes in a watched folder ----

    [ObservableProperty] private string? _autoloadFolder;
    [ObservableProperty] private bool _autoloadEnabled;
    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, DateTime> _lastSent = new(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void BrowseAutoload()
    {
        var dlg = new OpenFolderDialog { Title = "Folder to watch for .prg files" };
        if (dlg.ShowDialog() == true) AutoloadFolder = dlg.FolderName;
    }

    partial void OnAutoloadFolderChanged(string? value) { if (AutoloadEnabled) StartWatching(); }
    partial void OnAutoloadEnabledChanged(bool value) { if (value) StartWatching(); else StopWatching(); }

    private void StartWatching()
    {
        StopWatching();
        if (AutoloadFolder is null || !Directory.Exists(AutoloadFolder)) { Status = "Choose a folder to watch first."; AutoloadEnabled = false; return; }
        _watcher = new FileSystemWatcher(AutoloadFolder, "*.prg") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        _watcher.Changed += OnPrgChanged;
        _watcher.Created += OnPrgChanged;
        _watcher.Renamed += OnPrgChanged;
        Status = $"Watching {AutoloadFolder}: changed .prg files are sent to the MEGA65 automatically.";
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnPrgChanged(object sender, FileSystemEventArgs e) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            // Builds write files in several steps; wait until it settles and send each change once.
            await Task.Delay(700);
            if (_lastSent.TryGetValue(e.FullPath, out var t) && DateTime.Now - t < TimeSpan.FromSeconds(2)) return;
            if (IsBusy || services.Connection is null || !File.Exists(e.FullPath)) return;
            _lastSent[e.FullPath] = DateTime.Now;
            await SendFileAsync(e.FullPath);
        });

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
        using var claim = services.DeviceGate.Enter(busy.TrimEnd('…'));
        await services.ReleasePortAsync();
        var r = await op(RequireTools(), target);
        if (echo)
            foreach (var line in (r.StdOut + r.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(40))
                Output.Add(line.TrimEnd());
        Status = r.Succeeded ? "Done." : ToolFailureHints.Explain(r.StdOut + r.StdErr) ?? $"The tool exited with code {r.ExitCode}. See the output below.";
    }, busy);
}
