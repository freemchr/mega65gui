using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.Core.Common;
using Mega65Gui.Core.Cores;
using Mega65Gui.Core.Device;
using Mega65Gui.Core.Filehost;
using Mega65Gui.Core.Packages;
using Mega65Gui.Core.Sd;
using Mega65Gui.Core.Update;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

/// <summary>
/// Core Manager: a local library of FPGA cores, Filehost cores to download, and three clearly
/// separate ways to use a core: run it now over JTAG (temporary), start a flash slot (nothing
/// written), or copy it to the SD card and install it with MEGAFLASH on the machine.
/// </summary>
public sealed partial class CoreManagerViewModel : PageViewModel
{
    private readonly AppServices services;
    private readonly IApprovalGate gate;
    private readonly CoreLibrary library;

    public CoreManagerViewModel(AppServices services, IApprovalGate gate) : base("Core Manager", "")
    {
        this.services = services;
        this.gate = gate;
        library = services.CoreLibrary;
        services.ConnectionChanged += () => { ShowHardware(); RefreshLibrary(); RefreshSlots(); };
        services.ToolsChanged += () => Application.Current.Dispatcher.Invoke(ShowHardware);
        services.DeviceGate.Changed += () => Application.Current?.Dispatcher.BeginInvoke(ShowHardware);
        _compareAgainst = CompareChoices[0];
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy)) UpdateChecks(); };
        ShowHardware();
        RefreshLibrary();
        RefreshSlots();
    }

    /// <summary>Set by the shell so the page can open the Device page.</summary>
    public Action<string>? Navigate { get; set; }

    public ObservableCollection<string> Log { get; } = [];

    private void AddLog(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > 400) Log.RemoveAt(0);
    }

    // ================= Hardware strip =================

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isUsbJtag;
    [ObservableProperty] private bool _toolsInstalled;
    [ObservableProperty] private bool _adapterPresent;
    [ObservableProperty] private string _connectionText = "";
    [ObservableProperty] private string _hardwareHint = "";
    [ObservableProperty] private string? _deviceBusy;

    private void ShowHardware()
    {
        var c = services.Connection;
        IsConnected = c is not null;
        IsUsbJtag = c?.Kind == ConnectionKind.Serial;
        ToolsInstalled = services.Tools.Current is not null;
        AdapterPresent = IsUsbJtag || Ports.Any(p => p.CouldBeMega65);
        DeviceBusy = services.DeviceGate.Current is { } busy ? $"Busy: {busy}" : null;
        ConnectionText = c is null
            ? "No MEGA65 connected"
            : services.Device.NoResponse
                ? $"{c} · not answering the serial monitor"
                : services.Device.Model == HardwareModel.Unknown ? c.ToString() : $"MEGA65 {services.Device.Model} · {c}";
        HardwareHint = !ToolsInstalled
            ? "The device tools are not installed. Running a core, starting a slot and uploading to the card inside the MEGA65 use them."
            : c is null
                ? AdapterPresent
                    ? "A USB/JTAG adapter is plugged in. Connect on the Device page to run cores or start slots."
                    : "No USB/JTAG adapter found. Plug in the adapter's USB cable and switch the MEGA65 on. You can still manage the library and stage cores on a card in a PC reader."
                : c.Kind == ConnectionKind.Ethernet
                    ? "Connected over Ethernet: cores cannot be loaded and slots cannot be started over Ethernet; use USB/JTAG."
                    : services.Device.CoreBuild is { } core ? $"Running now: {core}" : "The running core could not be read.";
        OnPropertyChanged(nameof(CompareAgainstText));
    }

    [RelayCommand] private void OpenDevice() => Navigate?.Invoke("Device");

    // ---- Connect from this page (USB/JTAG), as on Update My MEGA65 ----

    public ObservableCollection<SerialPortInfo> Ports { get; } = [];
    [ObservableProperty] private string? _selectedPort;

    [RelayCommand]
    private void RefreshPorts()
    {
        var keep = SelectedPort;
        Ports.Clear();
        foreach (var p in SerialPorts.List()) Ports.Add(p);
        SelectedPort = keep is not null && Ports.Any(p => p.Name == keep) ? keep : SerialPorts.Preferred(Ports);
        ShowHardware();
    }

    [RelayCommand]
    private Task ConnectAsync()
    {
        RefreshPorts();
        if (SelectedPort is not { } port)
        {
            Status = "No USB/JTAG port found. Plug in the adapter's USB cable and switch the MEGA65 on.";
            return Task.CompletedTask;
        }
        return RunAsync($"Connecting on {port}", usesDevice: true, async ct =>
        {
            var state = await services.ConnectSerialAsync(port, ct);
            Status = state.NoResponse
                ? $"The MEGA65 did not answer on {port}. Make sure it is switched on and showing its normal screen, then press Connect again."
                : $"Connected to MEGA65 {state.Model} on {port}.";
            AddLog(Status);
        });
    }

    [RelayCommand]
    private Task InstallToolsAsync() => GuardAsync(async () =>
    {
        await services.InstallDeviceToolsAsync(progress: new Progress<string>(p => Status = p));
        Status = "Device tools installed.";
    }, "Installing device tools…");

    // ================= Model used for compatibility =================

    public IReadOnlyList<ModelChoice> CompareChoices { get; } =
    [
        new("The connected MEGA65", null),
        new("MEGA65 R6", HardwareModel.R6),
        new("MEGA65 R5", HardwareModel.R5),
        new("MEGA65 R4", HardwareModel.R4),
        new("MEGA65 R3 / R3a", HardwareModel.R3),
    ];

    [ObservableProperty] private ModelChoice? _compareAgainst;
    partial void OnCompareAgainstChanged(ModelChoice? value) { RefreshLibrary(); RefreshSlots(); OnPropertyChanged(nameof(CompareAgainstText)); }

    /// <summary>The model checks are made against, and where it came from.</summary>
    private (HardwareModel Model, Provenance Source) Machine =>
        CompareAgainst?.Model is { } chosen ? (chosen, Provenance.UserSupplied)
        : services.Connection is not null && services.Device.Model != HardwareModel.Unknown ? (services.Device.Model, services.Device.ModelSource)
        : (HardwareModel.Unknown, Provenance.NotKnown);

    public string CompareAgainstText => Machine switch
    {
        (HardwareModel.Unknown, _) => "Compatibility: model unknown",
        (var m, Provenance.ReadFromDevice) => $"Compatibility checked against this MEGA65 {m} (read from the machine)",
        var (m, _) => $"Compatibility checked against an {m} you chose (not read from a machine)",
    };

    // ================= Library =================

    public ObservableCollection<LibraryRow> LibraryRows { get; } = [];
    public ObservableCollection<string> LibraryCategories { get; } = ["All"];
    public IReadOnlyList<string> KnownCategories => library.Categories;

    [ObservableProperty] private string _librarySearch = "";
    [ObservableProperty] private string _libraryCategory = "All";
    [ObservableProperty] private bool _favoritesOnly;
    [ObservableProperty] private bool _compatibleOnly;
    [ObservableProperty] private bool _cardView;
    [ObservableProperty] private LibraryRow? _selected;
    [ObservableProperty] private bool _libraryEmpty = true;
    [ObservableProperty] private string _libraryInfo = "";

    private bool _refreshing;

    partial void OnLibrarySearchChanged(string value) => RefreshLibrary();
    partial void OnLibraryCategoryChanged(string value) { if (!_refreshing) RefreshLibrary(); }
    partial void OnFavoritesOnlyChanged(bool value) => RefreshLibrary();
    partial void OnCompatibleOnlyChanged(bool value) => RefreshLibrary();

    private void RefreshLibrary()
    {
        var keep = Selected?.Id;
        var (model, source) = Machine;
        var catalogue = services.Catalogue.Current?.Files ?? [];
        var entries = library.Entries;
        var rows = library.Search(new CoreLibraryQuery(LibrarySearch, LibraryCategory == "All" ? null : LibraryCategory, FavoritesOnly))
            .Select(e => new LibraryRow(e, CoreTargets.Check(e.Identity, model, source), library.ArtworkPathOf(e) is { } a && File.Exists(a) ? a : null,
                e.Origin.FilehostFileId is { } fid && catalogue.FirstOrDefault(f => f.FileId == fid) is { } file && FilehostCores.HasNewerVersion(file, entries)))
            .Where(r => !CompatibleOnly || r.Compatibility == CompatibilityResult.Compatible)
            .ToList();
        _refreshing = true;
        try
        {
            LibraryRows.Clear();
            foreach (var r in rows) LibraryRows.Add(r);
            // Clearing the list deselects the ComboBox, so restore the selection afterwards.
            var cat = LibraryCategory;
            LibraryCategories.Clear();
            LibraryCategories.Add("All");
            foreach (var c in library.Categories) LibraryCategories.Add(c);
            LibraryCategory = LibraryCategories.Contains(cat) ? cat : "All";
            OnPropertyChanged(nameof(LibraryCategory));
            OnPropertyChanged(nameof(KnownCategories));
        }
        finally { _refreshing = false; }

        LibraryEmpty = entries.Count == 0;
        var updates = rows.Count(r => r.HasUpdate);
        LibraryInfo = entries.Count == 0 ? "" : $"{rows.Count} of {entries.Count} core(s){(updates > 0 ? $" · {updates} with a newer Filehost version" : "")}";
        Selected = keep is null ? null : LibraryRows.FirstOrDefault(r => r.Id == keep);
        if (library.LoadWarning is { } warning) Status = warning;
    }

    [RelayCommand] private void ShowList() => CardView = false;
    [RelayCommand] private void ShowCards() => CardView = true;

    [RelayCommand]
    private Task ImportFilesAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add cores to the library",
            Multiselect = true,
            Filter = "MEGA65 cores and packages (*.cor;*.bit;*.zip;*.7z)|*.cor;*.bit;*.zip;*.7z|Core files (*.cor)|*.cor|Bitstreams (*.bit)|*.bit|All files|*.*",
        };
        return dlg.ShowDialog() == true ? ImportPathsAsync(dlg.FileNames) : Task.CompletedTask;
    }

    /// <summary>Drag-and-drop entry point: .cor, .bit and archives that contain them.</summary>
    public Task ImportPathsAsync(IReadOnlyList<string> paths) => RunAsync("Adding cores to the library", usesDevice: false, async ct =>
    {
        string? last = null;
        int added = 0, duplicates = 0, failed = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                var results = PackageExtractor.IsArchive(path)
                    ? ReportPackage(await library.ImportPackageAsync(path, null, ct), Path.GetFileName(path))
                    : [await library.ImportAsync(path, null, ct)];
                foreach (var r in results)
                {
                    AddLog(r.WasDuplicate ? $"{r.Entry.FileName}: already in the library as \"{r.Entry.DisplayName}\" (same SHA-256)." : $"Added {r.Entry.FileName} as \"{r.Entry.DisplayName}\".");
                    if (r.WasDuplicate) duplicates++; else added++;
                    last = r.Entry.Id;
                }
            }
            catch (InvalidDataException e) { failed++; AddLog(e.Message); }
            catch (IOException e) { failed++; AddLog($"{Path.GetFileName(path)}: {e.Message}"); }
        }
        RefreshLibrary();
        if (last is not null) Selected = LibraryRows.FirstOrDefault(r => r.Id == last);
        Status = $"{added} added, {duplicates} already in the library, {failed} not added{(failed > 0 ? " (see the log)" : "")}.";
    });

    private IReadOnlyList<CoreImportResult> ReportPackage(PackageImportResult r, string package)
    {
        foreach (var (file, reason) in r.Rejected) AddLog($"{package}/{file}: {reason}");
        if (r.Imported.Count == 0 && r.Rejected.Count == 0) AddLog($"{package} contains no .cor or .bit files.");
        return r.Imported;
    }

    // ---- selected core: details and editing ----

    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string? _editCategory;
    [ObservableProperty] private string _editTags = "";
    [ObservableProperty] private string _editNotes = "";
    [ObservableProperty] private ModelChoice? _editDeclaredModel;
    [ObservableProperty] private string _facts = "";
    [ObservableProperty] private string _runBlocker = "";
    [ObservableProperty] private string _stageBlocker = "";
    [ObservableProperty] private bool _canRun;
    [ObservableProperty] private bool _canStage;
    [ObservableProperty] private bool _isBitstream;

    /// <summary>For .bit files: the user's own label; "Not set" leaves the board unknown.</summary>
    public IReadOnlyList<ModelChoice> DeclaredChoices { get; } =
    [
        new("Not set (unknown board)", null), new("R6", HardwareModel.R6), new("R5", HardwareModel.R5),
        new("R4", HardwareModel.R4), new("R3 / R3a", HardwareModel.R3),
    ];

    private string? _editingId;

    partial void OnSelectedChanged(LibraryRow? value)
    {
        if (value is null) { Facts = ""; _editingId = null; FlashSteps = null; return; }
        var e = value.Entry;
        Facts = BuildFacts(e);
        SdFilesInfo = CoreStaging.PackageOf(e) is { } package
            ? $"Copies the folder(s) for the SD card from {Path.GetFileName(package)}, laid out exactly as in the package (for example vixen-32/BOOT.BIN). Many cores need these to start."
            : "This core was added on its own, not from a package, so there are no SD-card files to copy. Add or download the core's package to get them.";
        UpdateChecks();
        // A refresh re-selects the same core: keep what the user is typing.
        if (_editingId == e.Id) return;
        _editingId = e.Id;
        FlashSteps = null;
        EditName = e.DisplayName;
        EditCategory = e.Category;
        EditTags = string.Join(", ", e.Tags);
        EditNotes = e.Notes ?? "";
        IsBitstream = e.Kind == CoreFileKind.Bit;
        EditDeclaredModel = DeclaredChoices.FirstOrDefault(c => c.Model == e.DeclaredModel) ?? DeclaredChoices[0];
    }

    private static string BuildFacts(CoreLibraryEntry e)
    {
        var lines = new List<string> { $"File: {e.FileName} ({e.Size / 1048576.0:0.0} MB, {(e.Kind == CoreFileKind.Cor ? "MEGA65 core file" : "Xilinx bitstream")})" };
        if (e.Core is { } c)
        {
            lines.Add($"Header: \"{c.CoreName}\", version \"{c.CoreVersion}\"");
            lines.Add($"Built for: {CoreManagerText.BoardLabel(c.Target)} (target {c.Target}, model ID ${c.ModelId:X2}, read from the header)");
            lines.Add($"Check: CRC32 {c.StoredCrc:X8} matches{(c.EmbeddedFiles > 0 ? $"; {c.EmbeddedFiles} embedded file(s)" : "")}");
        }
        if (e.Bitstream is { } b)
        {
            lines.Add($"Bitstream: design \"{b.Design.Split(';')[0]}\", part {b.Part}, built {b.Date} {b.Time}");
            lines.Add($"FPGA: {b.Fpga ?? "not identified"}{(b.IdCode is { } id ? $" (IDCODE {id:X8})" : "")}. A .bit file does not record which MEGA65 board it is for.");
        }
        if (e.NamedModels.Count > 0) lines.Add($"Boards named in the file name: {string.Join(", ", e.NamedModels)} (a hint only, not checked)");
        if (e.Origin.FilehostTitle is { } t)
            lines.Add($"From the Filehost: {t}{(e.Origin.Author is { } a ? $" by {a}" : "")}{(e.Origin.Published is { } p ? $", published {p}" : "")}{(e.Origin.PackageFileName is { } pkg ? $", in {pkg}" : "")}");
        else if (e.Origin.SourcePath is { } src) lines.Add($"Imported from: {src}");
        lines.Add($"Added {e.ImportedAt.LocalDateTime:g}{(e.LastRunAt is { } r ? $" · last run over JTAG {r.LocalDateTime:g}" : "")}");
        lines.Add($"SHA-256: {e.Id}");
        return string.Join("\n", lines);
    }

    private void UpdateChecks()
    {
        if (Selected is not { } row) return;
        var (model, source) = Machine;
        var intact = library.FileExists(row.Entry);
        // Running is checked against the connected machine only: a chosen model cannot be loaded into.
        var run = CoreOperations.CheckRun(row.Entry, intact, Loader, services.Connection,
            services.Connection is null ? HardwareModel.Unknown : services.Device.Model, services.Device.ModelSource);
        CanRun = run.Allowed && !IsBusy;
        RunBlocker = run.Blocker ?? (run.NeedsExplicitConfirmation ? $"Not confirmed for this MEGA65: {run.Verdict!.Summary}" : run.Verdict?.Summary ?? "");
        var stage = CoreOperations.CheckStage(row.Entry, intact, model, source);
        CanStage = stage.Allowed && !IsBusy;
        StageBlocker = stage.Blocker ?? stage.Verdict?.Summary ?? "";
    }

    private ICoreLoader Loader => new JtagCoreLoader(services.Commands);

    [RelayCommand]
    private void SaveDetails()
    {
        if (Selected is not { } row) return;
        try
        {
            _editingId = null;
            library.Update(row.Id, e => e with
            {
                DisplayName = EditName,
                Category = EditCategory,
                Tags = CoreLibrary.ParseTags(EditTags),
                Notes = EditNotes,
                DeclaredModel = e.Kind == CoreFileKind.Bit ? EditDeclaredModel?.Model : null,
            });
            RefreshLibrary();
            Status = "Details saved.";
        }
        catch (ArgumentException e) { Status = e.Message; }
    }

    [RelayCommand]
    private void ToggleFavorite(LibraryRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        library.Update(row.Id, e => e with { Favorite = !e.Favorite });
        RefreshLibrary();
    }

    [RelayCommand]
    private Task ChooseArtworkAsync() => Selected is not { } row ? Task.CompletedTask : GuardAsync(async () =>
    {
        var dlg = new OpenFileDialog { Title = "Choose artwork or a screenshot", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await library.SetArtworkAsync(row.Id, dlg.FileName);
            RefreshLibrary();
            Status = "Artwork saved in the library.";
        }
        catch (InvalidDataException e) { Status = e.Message; }
    });

    [RelayCommand]
    private void ClearArtwork()
    {
        if (Selected is not { } row) return;
        library.ClearArtwork(row.Id);
        RefreshLibrary();
    }

    [RelayCommand]
    private Task ExportAsync() => Selected is not { } row ? Task.CompletedTask : GuardAsync(async () =>
    {
        var dlg = new SaveFileDialog { FileName = row.Entry.FileName, Filter = row.Entry.Kind == CoreFileKind.Cor ? "Core files (*.cor)|*.cor" : "Bitstreams (*.bit)|*.bit" };
        if (dlg.ShowDialog() != true) return;
        await library.ExportAsync(row.Id, dlg.FileName);
        AddLog($"Exported {row.Entry.FileName} to {dlg.FileName} (SHA-256 checked).");
        Status = $"Saved {dlg.FileName}.";
    }, "Exporting…");

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { } row) return;
        if (!Confirm("Remove from library", $"Remove \"{row.Name}\" from the library?\n\nIts file, artwork and notes are deleted from this PC. Cores on your SD card or in flash are not affected.", warning: true))
            return;
        library.Remove(row.Id);
        AddLog($"Removed {row.Entry.FileName} from the library.");
        Selected = null;
        RefreshLibrary();
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (Selected is { } row && library.FileExists(row.Entry))
            Process.Start("explorer.exe", $"/select,\"{library.PathOf(row.Entry)}\"");
        else Process.Start("explorer.exe", $"\"{library.Root}\"");
    }

    // ================= A. Run now over JTAG (temporary) =================

    [RelayCommand]
    private Task RunCoreAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        return RunAsync($"Loading {row.Entry.FileName} over JTAG", usesDevice: true, async ct =>
        {
            var intact = await library.VerifyAsync(row.Entry, ct);
            var check = CoreOperations.CheckRun(row.Entry, intact, Loader, services.Connection, services.Device.Model, services.Device.ModelSource);
            if (!check.Allowed) { Status = check.Blocker!; AddLog($"Not run: {check.Blocker}"); return; }
            var message = $"Load \"{row.Name}\" into the FPGA now?\n\n" + string.Join("\n\n", check.Warnings.Select(w => "• " + w));
            if (!Confirm(check.NeedsExplicitConfirmation ? "Run a core that is not confirmed for this MEGA65" : "Run core (temporary)", message, check.NeedsExplicitConfirmation))
            {
                Status = "Not run.";
                return;
            }
            var target = services.Connection!;
            // m65 needs the serial port: close the terminal and the card browser's session first.
            await services.ReleasePortAsync();
            AddLog($"m65 -q {row.Entry.FileName} on {target.Address}");
            var result = await Loader.LoadAsync(library.PathOf(row.Entry), target, new Progress<string>(AddLog), ct);
            AddLog(result.Message);
            Status = result.Message;
            if (!result.Succeeded) return;
            library.MarkRun(row.Id);
            services.NoteCoreReplaced($"{row.Name} (loaded over JTAG from this PC until power-off)");
            RefreshLibrary();
        });
    }

    // ================= B. Start a flash slot =================

    public ObservableCollection<SlotRow> SlotRows { get; } = [];

    private void RefreshSlots()
    {
        var notes = library.SlotNotes;
        var count = HardwareModels.FlashSlots(Machine.Model);
        SlotRows.Clear();
        for (var i = 0; i < 8; i++) SlotRows.Add(new SlotRow(i, notes.GetValueOrDefault(i), count is null || i < count));
        SlotInfo = count is { } n
            ? $"The MEGA65 {Machine.Model} has {n} slots. This app cannot read what each slot holds; the notes are yours, not a reading of the flash. MEGAFLASH shows the real contents."
            : "This app cannot read what each slot holds; the notes are yours, not a reading of the flash. MEGAFLASH (hold NO SCROLL while switching on) shows the real contents.";
    }

    [ObservableProperty] private string _slotInfo = "";

    [RelayCommand]
    private void SaveSlotNote(SlotRow? slot)
    {
        if (slot is null) return;
        library.SetSlotNote(slot.Number, slot.Note);
        Status = $"Note for slot {slot.Number} saved.";
    }

    [RelayCommand]
    private Task BootSlotAsync(SlotRow? slot)
    {
        if (slot is null) return Task.CompletedTask;
        return RunAsync($"Starting flash slot {slot.Number}", usesDevice: true, async ct =>
        {
            var check = CoreOperations.CheckBootSlot(slot.Number, services.Tools.Current is not null, services.Connection, services.Device.Model, services.Mega65CoreAnswering);
            if (!check.Allowed) { Status = check.Blocker!; return; }
            var note = slot.Note.Length > 0 ? $" (your note: {slot.Note})" : "";
            if (!Confirm("Start flash slot", $"Restart the MEGA65 from the core in slot {slot.Number}{note}?\n\nNothing is written to flash.\n\n" +
                                             string.Join("\n", check.Warnings.Select(w => "• " + w))))
                return;
            var target = services.Connection!;
            await services.ReleasePortAsync();
            AddLog($"m65 -Z {slot.Number} on {target.Address}");
            var r = await services.Commands!.BootSlotAsync(target, slot.Number, ct, new Progress<string>(AddLog));
            if (!r.Succeeded)
            {
                Status = ToolFailureHints.Explain(r.StdOut + r.StdErr) ?? $"m65 exited with code {r.ExitCode}; see the log.";
                AddLog(Status);
                return;
            }
            // Give the new core a moment, then read what is running (only MEGA65 cores answer).
            AddLog($"Slot {slot.Number} started. Reading the running core…");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var state = await services.ConnectSerialAsync(target.Address, ct);
            Status = state.NoResponse
                ? $"Slot {slot.Number} started. It does not answer the MEGA65 serial monitor, which is normal for cores other than the MEGA65 core."
                : $"Slot {slot.Number} started: {state.CoreBuild ?? "running core not read"}.";
            AddLog(Status);
        });
    }

    // ================= C. Install to flash: stage on the SD card for MEGAFLASH =================

    [ObservableProperty] private bool _cardInDevice = true;
    [ObservableProperty] private string _sdCardPath = "";
    [ObservableProperty] private string? _flashSteps;

    [RelayCommand]
    private void BrowseSdCard()
    {
        var dlg = new OpenFolderDialog { Title = "Select your MEGA65 SD card (in a card reader)" };
        if (dlg.ShowDialog() == true) { SdCardPath = dlg.FolderName; CardInDevice = false; }
    }

    [RelayCommand]
    private Task StageAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        return RunAsync($"Copying {row.Entry.FileName} to the SD card", usesDevice: CardInDevice, async ct =>
        {
            var (model, source) = Machine;
            var intact = await library.VerifyAsync(row.Entry, ct);
            var check = CoreOperations.CheckStage(row.Entry, intact, model, source);
            if (!check.Allowed) { Status = check.Blocker!; return; }
            if (check.NeedsExplicitConfirmation &&
                !Confirm("Core not confirmed for this MEGA65", string.Join("\n\n", check.Warnings.Select(w => "• " + w)) + "\n\nCopy it to the SD card anyway?", warning: true))
                return;

            var staging = new CoreStaging(library, gate, services.Paths, services.Contiguity);
            StageResult result;
            if (CardInDevice)
            {
                if (services.Connection is not { } target) { Status = "Connect to the MEGA65 first, or choose a card in a PC reader."; return; }
                if (services.Commands is not { } commands) { Status = "Install the device tools first."; return; }
                if (!await EnsureMega65CoreAsync(ct)) return;
                target = services.Connection!;
                if (!Confirm("Upload to the MEGA65's SD card",
                        $"Upload {CoreStaging.CardFileName(row.Entry)} ({row.Entry.Size / 1048576.0:0.0} MB) to the card inside the MEGA65 over {target}?\n\n" +
                        "mega65_ftp loads a helper into the MEGA65's memory: anything unsaved is lost and the MEGA65 resets after each step. " +
                        "Over USB/JTAG an 8 MB core takes several minutes. You approve the exact change before anything is written."))
                    return;
                await services.ReleasePortAsync();
                result = await staging.StageToDeviceAsync(row.Entry, new Mega65FtpCard(commands, target), new Progress<string>(AddLog), ct);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(SdCardPath) || !Directory.Exists(SdCardPath)) { Status = "Choose the SD card's folder first (SD card tab or Browse)."; return; }
                result = await staging.StageToFolderAsync(row.Entry, SdCardPath, new Progress<string>(AddLog), ct);
            }
            AddLog(result.Message);
            Status = result.Message;
            if (result.Staged)
            {
                FlashSteps = CoreStaging.FlashInstructions(model, result.CardFileName) +
                             "\n\nThe core is now stored on the SD card. It is not installed until MEGAFLASH writes it to a slot.";
                AddLog("Next: install it with MEGAFLASH on the MEGA65 (steps shown on the page).");
            }
        });
    }

    // ---- The core's own SD-card files (for example Vixen-32's vixen-32 folder) ----

    [ObservableProperty] private string _sdFilesInfo = "";

    [RelayCommand]
    private Task CopySdFilesAsync()
    {
        if (Selected is not { } row) return Task.CompletedTask;
        return RunAsync($"Copying the SD-card files of {row.Name}", usesDevice: CardInDevice, async ct =>
        {
            var staging = new CoreStaging(library, gate, services.Paths, services.Contiguity);
            if (CoreStaging.PackageOf(row.Entry) is null) { Status = SdFilesInfo; return; }
            StageResult result;
            if (CardInDevice)
            {
                if (services.Connection is not { Kind: ConnectionKind.Serial or ConnectionKind.Ethernet }) { Status = "Connect to the MEGA65 first, or choose a card in a PC reader."; return; }
                if (services.Tools.Current is null) { Status = "Install the device tools first."; return; }
                if (!Confirm("Copy SD-card files to the MEGA65",
                        $"Copy the SD-card folder(s) from {Path.GetFileName(row.Entry.Origin.SourcePath)} to the root of the card inside the MEGA65?\n\n" +
                        "You will see the exact list of files before anything is written. Files of the same name on the card are backed up to this PC first, every upload is read back and checked, and nothing is deleted.\n\n" +
                        "mega65_ftp loads a helper into the MEGA65's memory: anything unsaved is lost, and the MEGA65 resets when it finishes. Over USB/JTAG large files take several minutes."))
                    return;
                if (!await EnsureMega65CoreAsync(ct)) return;
                var target = services.Connection!;
                try
                {
                    var session = await services.GetFtpSessionAsync(ct);
                    result = await staging.CopySdFilesToDeviceAsync(row.Entry, new FtpCardTree(session, $"SD card in the MEGA65 via {target}"), new Progress<string>(AddLog), ct);
                }
                finally { await services.CloseFtpSessionAsync(); }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(SdCardPath) || !Directory.Exists(SdCardPath)) { Status = "Choose the SD card's folder first."; return; }
                result = await staging.CopySdFilesToFolderAsync(row.Entry, SdCardPath, new Progress<string>(AddLog), ct);
            }
            AddLog(result.Message);
            Status = result.Message;
        });
    }

    /// <summary>
    /// The SD card and flash slots can only be reached while the MEGA65 core runs (mega65_ftp and
    /// m65 -Z need its hypervisor and serial monitor). If another core is running, asks the user to
    /// switch the MEGA65 off and on, then waits until the MEGA65 core answers.
    /// </summary>
    private async Task<bool> EnsureMega65CoreAsync(CancellationToken ct)
    {
        if (services.Mega65CoreAnswering || services.Connection?.Kind == ConnectionKind.Ethernet) return true;
        var running = services.OtherCoreRunning && services.Device.CoreBuild is { } core ? core : "a core that does not answer the MEGA65 serial monitor";
        if (MessageBox.Show(Application.Current.MainWindow,
                $"The MEGA65 is running {running}.\n\nThe SD card can only be reached from the MEGA65 core. Press OK, then switch the MEGA65 off and on: " +
                "it starts the MEGA65 core again, and this app reconnects and carries on by itself. It waits up to two minutes; Cancel on this page stops waiting.",
                "Switch back to the MEGA65 core", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return false;

        var port = services.Connection?.Address ?? services.LostPort ?? SelectedPort;
        Status = "Waiting: switch the MEGA65 off and on…";
        AddLog("Waiting for the MEGA65 core: switch the MEGA65 off and on.");
        var until = DateTime.Now.AddMinutes(2);
        while (DateTime.Now < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            if (services.Mega65CoreAnswering) break; // the Device page's port watcher reconnected
            var ports = SerialMonitorProbe.ListPorts();
            port ??= SerialPorts.Preferred(SerialPorts.List());
            if (port is null || !ports.Contains(port, StringComparer.OrdinalIgnoreCase)) continue; // switched off: the adapter's port is gone
            try { await services.ConnectSerialAsync(port, ct); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException) { }
            if (services.Mega65CoreAnswering) break;
        }
        if (!services.Mega65CoreAnswering)
        {
            Status = "The MEGA65 core did not answer within two minutes. Switch the MEGA65 off and on, then try again.";
            AddLog(Status);
            return false;
        }
        AddLog($"The MEGA65 core is running again ({services.Device.CoreBuild ?? "core not read"}). Continuing.");
        // Give the core a moment to finish starting before the card is used.
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        return true;
    }

    [RelayCommand]
    private void ShowFlashSteps()
    {
        if (Selected is not { } row) return;
        FlashSteps = CoreStaging.FlashInstructions(Machine.Model, CoreStaging.CardFileName(row.Entry));
    }

    [RelayCommand] private void HideFlashSteps() => FlashSteps = null;

    // ================= Filehost cores =================

    public ObservableCollection<FilehostCoreRow> FilehostRows { get; } = [];
    public ObservableCollection<VersionRow> Versions { get; } = [];
    public IReadOnlyList<string> FilehostKinds { get; } = ["All", .. FilehostCores.Kinds];
    public IReadOnlyList<ModelChoice> ModelFilters { get; } =
    [
        new("Any board", null), new("Named R6", HardwareModel.R6), new("Named R5", HardwareModel.R5),
        new("Named R4", HardwareModel.R4), new("Named R3", HardwareModel.R3),
    ];

    [ObservableProperty] private string _filehostSearch = "";
    [ObservableProperty] private string _filehostKind = "All";
    [ObservableProperty] private ModelChoice? _filehostModel;
    [ObservableProperty] private bool _filehostOfficialOnly;
    [ObservableProperty] private FilehostCoreRow? _selectedFilehost;
    [ObservableProperty] private FilehostFileDetail? _filehostDetail;
    [ObservableProperty] private VersionRow? _selectedVersion;
    [ObservableProperty] private string _filehostInfo = "";
    [ObservableProperty] private double _downloadPercent;
    private bool _catalogueLoaded;

    partial void OnFilehostSearchChanged(string value) => ApplyFilehostFilter();
    partial void OnFilehostKindChanged(string value) => ApplyFilehostFilter();
    partial void OnFilehostModelChanged(ModelChoice? value) => ApplyFilehostFilter();
    partial void OnFilehostOfficialOnlyChanged(bool value) => ApplyFilehostFilter();
    partial void OnSelectedFilehostChanged(FilehostCoreRow? value) { if (value is not null && !_refreshing) _ = LoadFilehostDetailAsync(value); }

    public override async Task OnShownAsync()
    {
        RefreshPorts();
        RefreshLibrary();
        if (!_catalogueLoaded) await RefreshFilehostAsync(false);
    }

    [RelayCommand]
    private Task RefreshFilehostAsync(bool force) => GuardAsync(async () =>
    {
        try
        {
            var snap = await services.Catalogue.LoadAsync(force);
            _catalogueLoaded = true;
            FilehostInfo = snap.StaleReason is not null
                ? $"Offline: showing the Filehost list from {snap.FetchedAt.LocalDateTime:g}"
                : $"Filehost list from {snap.FetchedAt.LocalDateTime:g}";
        }
        catch (HttpRequestException e) { FilehostInfo = $"The Filehost could not be reached ({e.Message}). The library works offline."; }
        ApplyFilehostFilter();
        RefreshLibrary();
        Status = "";
    }, force ? "Checking the Filehost for cores…" : null);

    private void ApplyFilehostFilter()
    {
        var keep = SelectedFilehost?.Core.File.FileId;
        var entries = library.Entries;
        FilehostRows.Clear();
        var q = new FilehostCoreQuery(FilehostSearch, FilehostKind == "All" ? null : FilehostKind, FilehostModel?.Model, FilehostOfficialOnly);
        foreach (var c in FilehostCores.Search(services.Catalogue.Current?.Files ?? [], q))
            FilehostRows.Add(new FilehostCoreRow(c,
                FilehostCores.HasNewerVersion(c.File, entries) ? FilehostCoreState.UpdateAvailable
                : FilehostCores.IsInLibrary(c.File, entries) ? FilehostCoreState.InLibrary : FilehostCoreState.Available));
        if (keep is not null && FilehostRows.FirstOrDefault(r => r.Core.File.FileId == keep) is { } again)
        {
            // Keep the highlighted row after filtering without reloading its versions.
            _refreshing = true;
            SelectedFilehost = again;
            _refreshing = false;
        }
    }

    private async Task LoadFilehostDetailAsync(FilehostCoreRow row)
    {
        FilehostDetail = null;
        Versions.Clear();
        await GuardAsync(async () =>
        {
            FilehostDetail = await services.Catalogue.GetDetailAsync(row.Core.File.FileId);
            foreach (var v in await services.Catalogue.GetVersionsAsync(row.Core.File.FileId))
            {
                var vr = new VersionRow(v);
                if (services.Downloads.TryGetCached(v.VersionId) is { } m) vr.CachedPath = services.Downloads.PathOf(m);
                Versions.Add(vr);
            }
            SelectedVersion = Versions.FirstOrDefault(v => v.Row.IsActive) ?? Versions.FirstOrDefault();
            Status = "";
        }, "Loading versions…");
    }

    [RelayCommand]
    private Task DownloadAsync()
    {
        if (SelectedFilehost is not { } row || SelectedVersion is not { } version) return Task.CompletedTask;
        return RunAsync($"Downloading {version.Row.FileName}", usesDevice: false, async ct =>
        {
            DownloadPercent = 0;
            var progress = new Progress<DownloadProgress>(p => DownloadPercent = p.Total is > 0 ? 100.0 * p.Received / p.Total.Value : 0);
            // Restricted files go through the same session-checked catalogue path as the File Host page.
            var manifest = await services.Downloads.DownloadAsync(version.Row, progress, ct);
            var path = services.Downloads.PathOf(manifest);
            version.CachedPath = path;
            AddLog($"Downloaded {manifest.FileName} ({manifest.Size:N0} bytes, SHA-256 {manifest.Sha256[..16]}…).");
            var origin = new CoreOrigin
            {
                SourcePath = path,
                FilehostFileId = row.Core.File.FileId,
                FilehostVersionId = version.Row.VersionId,
                FilehostTitle = row.Title,
                FilehostVersion = row.Core.VersionHint ?? version.Release,
                FilehostCategory = row.Kind,
                Author = row.Author,
                Published = version.Row.Published,
            };
            IReadOnlyList<CoreImportResult> imported;
            if (PackageExtractor.IsArchive(path)) imported = ReportPackage(await library.ImportPackageAsync(path, origin, ct), manifest.FileName);
            else if (Path.GetExtension(path).ToLowerInvariant() is ".cor" or ".bit") imported = [await library.ImportAsync(path, origin, ct)];
            else
            {
                Status = $"{manifest.FileName} is not a core or an archive of cores, so nothing was added to the library. It is in the download cache.";
                AddLog(Status);
                return;
            }
            var (model, source) = Machine;
            foreach (var r in imported)
                AddLog($"{(r.WasDuplicate ? "Already in library" : "Added")}: {r.Entry.FileName} – {CoreTargets.Check(r.Entry.Identity, model, source).Summary}");
            RefreshLibrary();
            ApplyFilehostFilter();
            Status = imported.Count == 0
                ? $"{manifest.FileName} contains no usable .cor or .bit files (see the log)."
                : $"{imported.Count(r => !r.WasDuplicate)} core(s) added to the library from {manifest.FileName}.";
            if (imported.Count > 0)
            {
                SelectedTab = 0;
                Selected = LibraryRows.FirstOrDefault(r => r.Id == imported[0].Entry.Id);
            }
        });
    }

    [RelayCommand]
    private void OpenOnWebsite()
    {
        if (SelectedFilehost is { } row)
            Process.Start(new ProcessStartInfo(FilehostClient.WebPageFor(row.Core.File.FileId).ToString()) { UseShellExecute = true });
    }

    // ================= SD card =================

    public ObservableCollection<CardCoreRow> CardRows { get; } = [];
    [ObservableProperty] private string _cardInfo = "Choose where the SD card is, then press List cores on card.";
    [ObservableProperty] private CardCoreRow? _selectedCardCore;

    [RelayCommand]
    private Task RefreshCardAsync() => RunAsync("Listing cores on the SD card", usesDevice: CardInDevice, async ct =>
    {
        CardRows.Clear();
        var entries = library.Entries;
        var (model, source) = Machine;
        if (CardInDevice)
        {
            if (services.Connection is not { } target || services.Commands is not { } commands) { CardInfo = "Connect to the MEGA65 (and install the device tools) to list the card inside it."; return; }
            if (!Confirm("List the MEGA65's SD card", "Listing the card inside the MEGA65 loads mega65_ftp's helper into its memory. Anything unsaved is lost and the MEGA65 resets afterwards.\n\nContinue?"))
                return;
            if (!await EnsureMega65CoreAsync(ct)) return;
            target = services.Connection!;
            await services.ReleasePortAsync();
            var listing = await new Mega65FtpCard(commands, target).ListRootAsync(ct);
            foreach (var f in listing.Where(f => !f.IsDirectory && f.Name.EndsWith(".cor", StringComparison.OrdinalIgnoreCase)))
            {
                var match = entries.FirstOrDefault(e => string.Equals(CoreStaging.CardFileName(e), f.Name, StringComparison.OrdinalIgnoreCase) && e.Size == f.Size);
                CardRows.Add(new CardCoreRow(f.Name, f.Size, "Header not read: the card is inside the MEGA65",
                    match is null ? "Not in library" : $"Same name and size as \"{match.DisplayName}\"",
                    match is null ? null : CoreTargets.Check(match.Identity, model, source).Result, null));
            }
            CardInfo = $"{CardRows.Count} core file(s) in the root of the card inside the MEGA65.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(SdCardPath) || !Directory.Exists(SdCardPath)) { CardInfo = "Choose the SD card's folder first."; return; }
            var rows = await Task.Run(() => ReadLocalCard(SdCardPath, entries, model, source, ct), ct);
            foreach (var r in rows) CardRows.Add(r);
            CardInfo = $"{CardRows.Count} core file(s) in the root of {SdCardPath}.";
        }
        CardInfo += " Cores on the card are only stored there: MEGAFLASH must write one into a flash slot before the MEGA65 can start it.";
    });

    private static List<CardCoreRow> ReadLocalCard(string root, IReadOnlyList<CoreLibraryEntry> entries, HardwareModel model, Provenance source, CancellationToken ct)
    {
        var rows = new List<CardCoreRow>();
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*.cor").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = File.ReadAllBytes(f.FullName);
                var info = CoreFile.Parse(data);
                var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
                var match = entries.FirstOrDefault(e => e.Id == sha);
                var verdict = CoreTargets.Check(CoreIdentity.FromCore(info), model, source);
                rows.Add(new CardCoreRow(f.Name, f.Length,
                    $"{info.CoreName} {info.CoreVersion} · for {CoreManagerText.BoardLabel(info.Target)} · {(info.IsValid ? "CRC OK" : "CRC FAILED")}",
                    match is null ? "Not in library" : $"In library as \"{match.DisplayName}\"", verdict.Result, f.FullName));
            }
            catch (InvalidDataException e) { rows.Add(new CardCoreRow(f.Name, f.Length, e.Message, "", CompatibilityResult.Incompatible, null)); }
        }
        return rows;
    }

    [RelayCommand]
    private Task ImportFromCardAsync()
    {
        if (SelectedCardCore?.LocalPath is not { } path) { Status = "Select a core on a card in a PC reader to add it to the library."; return Task.CompletedTask; }
        return ImportPathsAsync([path]);
    }

    [RelayCommand]
    private void EjectCard()
    {
        if (CardEjector.RemovableDriveOf(SdCardPath) is not { } drive) { Status = "Choose the SD card's drive first (a removable drive such as E:)."; return; }
        try
        {
            CardEjector.Eject(drive);
            Status = $"The SD card in {drive} has been ejected. You can take it out of the card reader.";
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { Status = e.Message; }
    }

    // ================= Tabs, operations and cancellation =================

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string? _operation;
    private CancellationTokenSource? _cts;

    [RelayCommand] private void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Runs one Core Manager operation: busy state, cancellation, a log line at start and end, and
    /// (for anything that talks to the MEGA65) the shared device lock, so two operations never use
    /// the machine at once.
    /// </summary>
    private async Task RunAsync(string name, bool usesDevice, Func<CancellationToken, Task> body)
    {
        if (IsBusy) { Status = $"Wait for \"{Operation}\" to finish first."; return; }
        IDisposable? claim = null;
        if (usesDevice)
        {
            try { claim = services.DeviceGate.Enter(name); }
            catch (DeviceBusyException e) { Status = e.Message; AddLog(e.Message); return; }
        }
        _cts = new CancellationTokenSource();
        Operation = name;
        AddLog(name + "…");
        try
        {
            await GuardAsync(() => body(_cts.Token), name + "…");
            if (Status.StartsWith("Error: ", StringComparison.Ordinal) || Status == "Cancelled.") AddLog(Status);
        }
        finally
        {
            claim?.Dispose();
            Operation = null;
            _cts.Dispose();
            _cts = null;
            UpdateChecks();
        }
    }

    private static bool Confirm(string title, string message, bool warning = false) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo,
            warning ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
}
