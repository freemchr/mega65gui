using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Update;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

public sealed partial class StepRow(int number, string title) : ObservableObject
{
    public int Number { get; } = number;
    public string Title { get; } = title;
    [ObservableProperty] private StepStatus _status = StepStatus.Pending;
    [ObservableProperty] private string _detail = "";
}

public sealed partial class UpdateViewModel : PageViewModel
{
    private readonly AppServices services;
    private readonly IApprovalGate gate;

    public UpdateViewModel(AppServices services, IApprovalGate gate) : base("Update My MEGA65", "\uE895")
    {
        this.services = services;
        this.gate = gate;
        services.ConnectionChanged += ShowConnection;
    }

    private CancellationTokenSource? _cts;


    public ObservableCollection<StepRow> Steps { get; } =
        new(UpdateEngine.StepTitles.Select((t, i) => new StepRow(i + 1, t)));

    public ObservableCollection<string> Log { get; } = [];

    public sealed record ModelChoice(string Label, HardwareModel? Model);

    public IReadOnlyList<ModelChoice> ModelChoices { get; } =
    [
        new("Auto (read from device)", null),
        new("MEGA65 R6", HardwareModel.R6),
        new("MEGA65 R5 (beta PCB)", HardwareModel.R5),
        new("MEGA65 R3 / R3a / DevKit", HardwareModel.R3),
        new("MEGA65 R2", HardwareModel.R2),
        new("Nexys4 DDR (widget)", HardwareModel.Nexys4DdrWidget),
    ];

    [ObservableProperty] private string _sdCardPath = "";
    [ObservableProperty] private ModelChoice? _selectedModel;
    [ObservableProperty] private bool _includeRom;
    /// <summary>True: update the card inside the MEGA65 over the current connection. False: a card in a PC reader.</summary>
    [ObservableProperty] private bool _cardInDevice;
    [ObservableProperty] private bool _isConnected;

    [ObservableProperty] private string? _reportPath;
    [ObservableProperty] private string _connectionText = "Not connected";

    private bool _cardChoiceMade;

    partial void OnCardInDeviceChanged(bool value) => _cardChoiceMade = true;

    public ObservableCollection<SerialPortInfo> Ports { get; } = [];
    [ObservableProperty] private string? _selectedPort;

    public override Task OnShownAsync()
    {
        SelectedModel ??= ModelChoices[0];
        RefreshPorts();
        ShowConnection();
        // Default to the card inside the MEGA65 only when one is connected. Any COM port is not
        // enough: many PCs have Bluetooth or modem ports that are not a MEGA65.
        if (!_cardChoiceMade) { CardInDevice = IsConnected; _cardChoiceMade = false; }
        return Task.CompletedTask;
    }

    private void ShowConnection()
    {
        IsConnected = services.Connection is not null;
        ConnectionText = services.Connection is { } c
            ? services.Device.NoResponse
                ? $"{c} – the MEGA65 is not answering. Make sure it is on and showing its normal screen."
                : $"{c} – MEGA65 {services.Device.Model}, running core {services.Device.CoreBuild ?? "unknown"}, ROM {services.Device.RomVersion ?? "unknown"}"
            : services.LostPort is { } lost
                ? $"Disconnected ({lost} has gone). Switch the MEGA65 on; it reconnects automatically."
                : "Not connected. Update My MEGA65 connects automatically, or pick the port and press Connect.";
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
    private Task ConnectAsync() => GuardAsync(async () =>
    {
        if (await EnsureConnectedAsync()) Status = "";
    }, "Connecting…");

    /// <summary>
    /// Connects over USB/JTAG when not connected: the selected port, or the only port there is.
    /// Returns false with a message in Status when that is not possible.
    /// </summary>
    private async Task<bool> EnsureConnectedAsync()
    {
        if (services.Connection is not null && !services.Device.NoResponse) return true;
        RefreshPorts();
        var port = SelectedPort;
        if (port is null && !Ports.Any(p => p.CouldBeMega65))
        {
            Status = (Ports.Count == 0 ? "No MEGA65 found over USB/JTAG." : $"No MEGA65 found over USB/JTAG ({string.Join(", ", Ports.Select(p => p.Label))}).") +
                     " Switch it on and plug in the USB cable" + (CardInDevice ? ", or choose \"In a PC card reader\"." : ", or choose your model.");
            return false;
        }
        if (port is null) { Status = "Choose the MEGA65's COM port and press Connect."; return false; }

        Status = $"Connecting to the MEGA65 on {port}…";
        var state = await services.ConnectSerialAsync(port);
        if (state.NoResponse)
        {
            Status = $"The MEGA65 did not answer on {port}. Make sure it is switched on and showing its normal screen (not the ROM selection screen or MEGAFLASH), then try again.";
            return false;
        }
        return true;
    }

    [RelayCommand]
    private void EjectCard()
    {
        if (Services.CardEjector.RemovableDriveOf(SdCardPath) is not { } drive)
        {
            Status = "Choose the SD card's drive first (a removable drive such as E:).";
            return;
        }
        try
        {
            Services.CardEjector.Eject(drive);
            Status = $"The SD card in {drive} has been ejected. You can take it out of the card reader.";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { Status = e.Message; }
    }

    [RelayCommand]
    private void BrowseSdCard()
    {
        var dlg = new OpenFolderDialog { Title = "Select your MEGA65 SD card (in a card reader)" };
        if (dlg.ShowDialog() == true) SdCardPath = dlg.FolderName;
    }

    private UpdatePreflight Preflight() => new(
        CardInDevice,
        ModelChosen: SelectedModel?.Model is not null,
        Connected: services.Connection is not null && !services.Device.NoResponse,
        ConnectedOverUsb: services.Connection?.Kind == ConnectionKind.Serial,
        ToolsInstalled: services.Tools.Current is not null,
        CardFolderExists: !string.IsNullOrWhiteSpace(SdCardPath) && Directory.Exists(SdCardPath));

    [RelayCommand]
    private async Task RunAsync()
    {
        var check = Preflight().Check();
        if (check.Action == PreflightAction.Connect)
        {
            IsBusy = true;
            try
            {
                if (!await EnsureConnectedAsync()) return;
            }
            finally { IsBusy = false; }
            check = Preflight().Check();
        }
        if (check.Action != PreflightAction.Go)
        {
            Status = check.Message ?? "";
            return;
        }
        if (IncludeRom && !services.Session.IsSignedIn && !Views.SignInWindow.Show(services))
        {
            Status = "Sign in to the Filehost to include the closed ROM, or untick that option.";
            return;
        }
        await services.ReleasePortAsync();
        foreach (var s in Steps) { s.Status = StepStatus.Pending; s.Detail = ""; }
        Log.Clear();
        ReportPath = null;
        _cts = new CancellationTokenSource();

        var engine = new UpdateEngine(services.Catalogue, services.Downloads, new DeviceProbe(), gate, services.Paths, services.Contiguity,
            target => new Mega65FtpCard(services.Commands!, target));
        engine.StepChanged += rec => Application.Current.Dispatcher.Invoke(() =>
        {
            var row = Steps[rec.Number - 1];
            row.Status = rec.Status;
            row.Detail = rec.Detail;
        });
        var log = new Progress<string>(l => { Log.Add(l); if (Log.Count > 500) Log.RemoveAt(0); });

        await GuardAsync(async () =>
        {
            var report = await Task.Run(() => engine.RunAsync(new UpdateOptions(SdCardPath, services.Connection, SelectedModel?.Model, IncludeRom, CardInDevice), log, _cts.Token));
            ReportPath = report.Steps.FirstOrDefault(s => s.Number == 12)?.Detail;
            Status = report.Outcome ?? "";
        }, "Working…");
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenReport()
    {
        if (ReportPath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
