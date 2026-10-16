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

public sealed partial class UpdateViewModel(AppServices services, IApprovalGate gate) : PageViewModel("Update My MEGA65", "\uE895")
{
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

    public override Task OnShownAsync()
    {
        SelectedModel ??= ModelChoices[0];
        IsConnected = services.Connection is not null;
        // Default to the card inside the MEGA65 whenever one is connected, until the user chooses.
        if (!_cardChoiceMade) { CardInDevice = IsConnected; _cardChoiceMade = false; }
        ConnectionText = services.Connection is { } c
            ? $"{c} – {services.Device.Model} ({services.Device.ModelSource})"
            : "Not connected. Connect on the Device page to read the model and core automatically, or choose the model below.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void BrowseSdCard()
    {
        var dlg = new OpenFolderDialog { Title = "Select your MEGA65 SD card (in a card reader)" };
        if (dlg.ShowDialog() == true) SdCardPath = dlg.FolderName;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (CardInDevice)
        {
            if (services.Connection is null) { Status = "Connect to the MEGA65 on the Device page first, or choose a card in a PC reader."; return; }
            if (services.Tools.Current is null) { Status = "Updating the card inside the MEGA65 needs the device tools. Install them from the sidebar."; return; }
        }
        else if (string.IsNullOrWhiteSpace(SdCardPath) || !Directory.Exists(SdCardPath))
        {
            Status = "Choose the SD card folder first.";
            return;
        }
        if (IncludeRom && !services.Session.IsSignedIn && !Views.SignInWindow.Show(services))
        {
            Status = "Sign in to the Filehost to include the closed ROM, or untick that option.";
            return;
        }
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
