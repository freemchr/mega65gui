using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mega65Gui.App.Services;
using Mega65Gui.App.Views;
using Mega65Gui.Core.Cores;
using Mega65Gui.Core.DiskImages;
using Mega65Gui.Core.Roms;
using Microsoft.Win32;

namespace Mega65Gui.App.ViewModels;

public sealed record PaletteChoice(string Name, byte Index, SolidColorBrush Brush);
public sealed record ByteChoice(string Label, byte Value);

/// <summary>Offline tools: D81 disk image editor, ROM start-up settings and .rdf ROM patches.</summary>
public sealed partial class ToolsPageViewModel(AppServices services) : PageViewModel("Disk & ROM Tools", "")
{
    // ================= D81 editor =================

    private D81Image? _disk;
    public ObservableCollection<DiskEntry> DiskEntries { get; } = [];
    [ObservableProperty] private string? _diskPath;
    [ObservableProperty] private string _diskName = "";
    [ObservableProperty] private string _diskId = "";
    [ObservableProperty] private string _diskInfo = "Open a .D81 disk image or create a new one.";
    [ObservableProperty] private bool _hasDisk;
    [ObservableProperty] private bool _diskDirty;

    private void ShowDisk()
    {
        DiskEntries.Clear();
        if (_disk is null) { HasDisk = false; return; }
        foreach (var e in _disk.Entries) DiskEntries.Add(e);
        DiskName = _disk.DiskName;
        DiskId = _disk.DiskId;
        DiskInfo = $"{DiskEntries.Count} file(s) · {_disk.FreeBlocks} blocks free{(DiskDirty ? " · unsaved changes" : "")}";
        HasDisk = true;
    }

    [RelayCommand]
    private void NewDisk()
    {
        if (!ConfirmDiscard()) return;
        var name = InputDialog.Ask("New disk image", "Disk name (up to 16 characters):", "NEW DISK");
        if (name is null) return;
        _disk = D81Image.Create(name, "65");
        DiskPath = null;
        DiskDirty = true;
        ShowDisk();
    }

    [RelayCommand]
    private void OpenDisk()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = "D81 disk images (*.d81)|*.d81" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _disk = D81Image.Load(File.ReadAllBytes(dlg.FileName));
            DiskPath = dlg.FileName;
            DiskDirty = false;
            ShowDisk();
            Status = "";
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { Status = $"Cannot open: {e.Message}"; }
    }

    [RelayCommand]
    private void SaveDisk()
    {
        if (_disk is null) return;
        if (DiskPath is null)
        {
            var dlg = new SaveFileDialog { Filter = "D81 disk images (*.d81)|*.d81", FileName = (_disk.DiskName.Length > 0 ? _disk.DiskName : "disk") + ".d81" };
            if (dlg.ShowDialog() != true) return;
            DiskPath = dlg.FileName;
        }
        ApplyHeader();
        File.WriteAllBytes(DiskPath, _disk.ToArray());
        DiskDirty = false;
        ShowDisk();
        Status = $"Saved {DiskPath}. Copy it to the MEGA65 with the SD Card Browser; disk images must be stored unfragmented.";
    }

    private void ApplyHeader()
    {
        if (_disk is null) return;
        if (DiskName != _disk.DiskName) { _disk.DiskName = DiskName; DiskDirty = true; }
        if (DiskId != _disk.DiskId) { _disk.DiskId = DiskId; DiskDirty = true; }
    }

    [RelayCommand]
    private void AddFiles()
    {
        if (_disk is null) return;
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "Programs and files|*.prg;*.seq;*.usr;*.*" };
        if (dlg.ShowDialog() != true) return;
        AddPaths(dlg.FileNames);
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        if (_disk is null) return;
        var added = 0;
        foreach (var p in paths.Where(File.Exists))
        {
            try
            {
                var ext = Path.GetExtension(p).ToLowerInvariant();
                var type = ext switch { ".seq" => CbmFileType.Seq, ".usr" => CbmFileType.Usr, _ => CbmFileType.Prg };
                _disk.Add(Path.GetFileNameWithoutExtension(p), File.ReadAllBytes(p), type);
                added++;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Status = $"{Path.GetFileName(p)}: {e.Message}"; }
        }
        if (added > 0) { DiskDirty = true; Status = $"Added {added} file(s)."; }
        ShowDisk();
    }

    /// <summary>Copies chosen files from another D81 image onto this one.</summary>
    [RelayCommand]
    private void CopyFromDisk()
    {
        if (_disk is null) return;
        var dlg = new OpenFileDialog { Title = "Copy files from another disk image", Filter = "D81 disk images (*.d81)|*.d81" };
        if (dlg.ShowDialog() != true) return;
        D81Image other;
        try { other = D81Image.Load(File.ReadAllBytes(dlg.FileName)); }
        catch (Exception e) when (e is IOException or InvalidDataException) { Status = $"Cannot open: {e.Message}"; return; }
        var files = other.Entries.Where(e => e.Closed && e.Type is CbmFileType.Prg or CbmFileType.Seq or CbmFileType.Usr).ToList();
        if (files.Count == 0) { Status = $"{Path.GetFileName(dlg.FileName)} has no PRG, SEQ or USR files to copy."; return; }
        var picked = PickItemsWindow.Ask("Copy files", $"Files on {Path.GetFileName(dlg.FileName)} ({other.DiskName}). Choose the ones to copy onto {(_disk.DiskName.Length > 0 ? _disk.DiskName : "this disk")}:",
            files.Select(e => $"{e.Blocks,5}  {e.Name,-16}  {e.TypeName}").ToList(), "Copy");
        var copied = 0;
        var problems = new List<string>();
        foreach (var e in picked.Select(i => files[i]))
        {
            try { _disk.Add(e.Name, other.Extract(e), e.Type); copied++; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException) { problems.Add($"{e.Name}: {ex.Message}"); }
        }
        if (copied > 0) { DiskDirty = true; ShowDisk(); }
        if (picked.Count > 0)
            Status = (copied > 0 ? $"Copied {copied} file(s). Save to keep them." : "Nothing was copied.") + (problems.Count > 0 ? " " + string.Join(" ", problems) : "");
    }

    [RelayCommand]
    private void ExtractFiles(IList? selected)
    {
        var items = selected?.OfType<DiskEntry>().ToList() ?? [];
        if (_disk is null || items.Count == 0) { Status = "Select files to extract."; return; }
        var dlg = new OpenFolderDialog { Title = "Extract to folder" };
        if (dlg.ShowDialog() != true) return;
        foreach (var e in items)
        {
            var name = string.Concat(e.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            File.WriteAllBytes(Path.Combine(dlg.FolderName, $"{name}.{e.Type.ToString().ToLowerInvariant()}"), _disk.Extract(e));
        }
        Status = $"Extracted {items.Count} file(s) to {dlg.FolderName}.";
    }

    [RelayCommand]
    private void RenameFile(IList? selected)
    {
        if (_disk is null || selected?.OfType<DiskEntry>().ToList() is not [var e]) { Status = "Select one file to rename."; return; }
        var name = InputDialog.Ask("Rename", $"New name for {e.Name}:", e.Name);
        if (name is null) return;
        try { _disk.Rename(e, name); DiskDirty = true; ShowDisk(); }
        catch (InvalidOperationException ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void DeleteFiles(IList? selected)
    {
        var items = selected?.OfType<DiskEntry>().ToList() ?? [];
        if (_disk is null || items.Count == 0) return;
        if (MessageBox.Show(Application.Current.MainWindow, $"Delete {items.Count} file(s) from the disk image?", "Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var e in items) _disk.Delete(e);
        DiskDirty = true;
        ShowDisk();
    }

    private bool ConfirmDiscard() => !DiskDirty || MessageBox.Show(Application.Current.MainWindow,
        "The disk image has unsaved changes. Discard them?", "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    // ================= ROM settings =================

    private byte[]? _rom;
    [ObservableProperty] private string? _romPath;
    [ObservableProperty] private string _romInfo = "Open a MEGA65.ROM file (version 920252 or later) to change its start-up settings.";
    [ObservableProperty] private bool _hasRom;

    public IReadOnlyList<ByteChoice> Speeds { get; } = RomSettings.Speeds.Select(x => new ByteChoice(x.Label, x.Value)).ToList();
    public IReadOnlyList<ByteChoice> Fonts { get; } = RomSettings.Fonts.Select(x => new ByteChoice(x.Label, x.Value)).ToList();
    public IReadOnlyList<PaletteChoice> TextColours { get; } = Palette(16);
    public IReadOnlyList<PaletteChoice> AreaColours { get; } = Palette(RomSettings.Palette.Count);

    private static List<PaletteChoice> Palette(int count) => RomSettings.Palette.Take(count).Select((p, i) =>
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(p.Rgb >> 16), (byte)(p.Rgb >> 8), (byte)p.Rgb));
        b.Freeze();
        return new PaletteChoice($"{i} {p.Name}", (byte)i, b);
    }).ToList();

    [ObservableProperty] private ByteChoice? _speed;
    [ObservableProperty] private ByteChoice? _font;
    [ObservableProperty] private PaletteChoice? _foreground;
    [ObservableProperty] private PaletteChoice? _border;
    [ObservableProperty] private PaletteChoice? _background;
    [ObservableProperty] private PaletteChoice? _errorColour;
    [ObservableProperty] private PaletteChoice? _remarkColour;
    [ObservableProperty] private PaletteChoice? _keywordColour;

    [RelayCommand]
    private void OpenRom()
    {
        var dlg = new OpenFileDialog { Filter = "MEGA65 ROMs (*.rom;*.bin)|*.rom;*.bin" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var rom = File.ReadAllBytes(dlg.FileName);
            var s = RomSettings.Read(rom);
            _rom = rom;
            RomPath = dlg.FileName;
            Show(s);
            RomInfo = $"{Path.GetFileName(dlg.FileName)} · ROM {RomFile.ReadVersion(rom)}";
            HasRom = true;
            Status = "";
        }
        catch (Exception e) when (e is IOException or InvalidDataException) { Status = $"Cannot use this ROM: {e.Message}"; }
    }

    private static PaletteChoice Pick(IReadOnlyList<PaletteChoice> list, byte index) => list.FirstOrDefault(c => c.Index == index) ?? list[0];

    private RomSettings CurrentSettings() => new(Speed!.Value, Font!.Value, Foreground!.Index, Border!.Index, Background!.Index,
        ErrorColour!.Index, RemarkColour!.Index, KeywordColour!.Index);

    private void Show(RomSettings s)
    {
        Speed = Speeds.FirstOrDefault(x => x.Value == s.Speed, Speeds[2]);
        Font = Fonts.FirstOrDefault(x => x.Value == s.Font, Fonts[0]);
        Foreground = Pick(TextColours, s.Foreground); Border = Pick(AreaColours, s.Border); Background = Pick(AreaColours, s.Background);
        ErrorColour = Pick(TextColours, s.ErrorMessage); RemarkColour = Pick(TextColours, s.Remark); KeywordColour = Pick(TextColours, s.Keyword);
    }

    private const string PresetFilter = "MEGA65 ROM settings (*.m65rom.json)|*.m65rom.json|JSON files (*.json)|*.json";

    /// <summary>Keeps the chosen settings in a small file, to apply to another ROM (such as a newer version) later.</summary>
    [RelayCommand]
    private void SavePreset()
    {
        if (!HasRom) return;
        var dlg = new SaveFileDialog { Title = "Save these settings", Filter = PresetFilter, FileName = "my settings.m65rom.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, CurrentSettings().ToPreset());
            Status = $"Saved the settings to {dlg.FileName}. Open another ROM and load them to use them again.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = $"Cannot save: {e.Message}"; }
    }

    [RelayCommand]
    private void LoadPreset()
    {
        if (!HasRom) return;
        var dlg = new OpenFileDialog { Title = "Load saved settings", Filter = PresetFilter };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Show(RomSettings.FromPreset(File.ReadAllText(dlg.FileName)));
            Status = $"Loaded {Path.GetFileName(dlg.FileName)}. Save as… writes a ROM with these settings.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { Status = $"Cannot use that file: {e.Message}"; }
    }

    [RelayCommand]
    private void SaveRom()
    {
        if (_rom is null || RomPath is null) return;
        var dlg = new SaveFileDialog { Filter = "MEGA65 ROMs (*.rom)|*.rom", FileName = "MEGA65.ROM", InitialDirectory = Path.GetDirectoryName(RomPath) };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllBytes(dlg.FileName, CurrentSettings().ApplyTo(_rom));
        Status = $"Saved {dlg.FileName}. Copy it to the card as MEGA65.ROM (or MEGA65x.ROM) to use it.";
    }

    // ================= ROM patch =================

    [ObservableProperty] private string? _patchPath;
    [ObservableProperty] private string? _patchBasePath;
    [ObservableProperty] private string _patchInfo = "A .rdf patch turns one ROM version into another. It needs the exact ROM it was made from.";

    [RelayCommand]
    private void OpenPatch()
    {
        var dlg = new OpenFileDialog { Filter = "MEGA65 ROM patches (*.rdf)|*.rdf" };
        if (dlg.ShowDialog() != true) return;
        var header = File.ReadAllBytes(dlg.FileName);
        if (RomPatcher.ReferenceName(header) is not { } reference) { Status = "That is not a MEGA65 ROM patch."; return; }
        PatchPath = dlg.FileName;
        PatchInfo = $"Turns {reference} into {RomPatcher.TargetName(header)}. Choose your copy of {reference}.";
    }

    [RelayCommand]
    private void ChoosePatchBase()
    {
        var dlg = new OpenFileDialog { Filter = "MEGA65 ROMs (*.rom;*.bin)|*.rom;*.bin" };
        if (dlg.ShowDialog() == true) PatchBasePath = dlg.FileName;
    }

    [RelayCommand]
    private Task ApplyPatchAsync() => GuardAsync(async () =>
    {
        if (PatchPath is null || PatchBasePath is null) { Status = "Choose a patch and the ROM it applies to."; return; }
        var romdiff = services.Tools.Current?.PathOf("romdiff") ?? throw new InvalidOperationException("romdiff is part of the device tools; install them first.");
        var target = RomPatcher.TargetName(File.ReadAllBytes(PatchPath)) ?? "patched.rom";
        var dlg = new SaveFileDialog { Filter = "MEGA65 ROMs (*.rom;*.bin)|*.rom;*.bin", FileName = target };
        if (dlg.ShowDialog() != true) return;
        var r = await new RomPatcher(services.Runner).ApplyAsync(romdiff, PatchPath, PatchBasePath, dlg.FileName);
        Status = r.Succeeded
            ? $"Patched ROM saved as {dlg.FileName} (version {RomFile.ReadVersion(File.ReadAllBytes(dlg.FileName)) ?? "unknown"})."
            : $"romdiff failed: {RomPatcher.Problem(r)}";
    }, "Applying patch…");

    // Making a patch: the original ROM and the changed one.
    [ObservableProperty] private string? _patchOriginalPath;
    [ObservableProperty] private string? _patchChangedPath;

    [RelayCommand]
    private void ChoosePatchOriginal()
    {
        var dlg = new OpenFileDialog { Title = "The original ROM (the patch will need this exact file)", Filter = "MEGA65 ROMs (*.rom;*.bin)|*.rom;*.bin" };
        if (dlg.ShowDialog() == true) PatchOriginalPath = dlg.FileName;
    }

    [RelayCommand]
    private void ChoosePatchChanged()
    {
        var dlg = new OpenFileDialog { Title = "The changed ROM", Filter = "MEGA65 ROMs (*.rom;*.bin)|*.rom;*.bin" };
        if (dlg.ShowDialog() == true) PatchChangedPath = dlg.FileName;
    }

    [RelayCommand]
    private Task CreatePatchAsync() => GuardAsync(async () =>
    {
        if (PatchOriginalPath is null || PatchChangedPath is null) { Status = "Choose the original ROM and the changed ROM."; return; }
        var romdiff = services.Tools.Current?.PathOf("romdiff") ?? throw new InvalidOperationException("romdiff is part of the device tools; install them first.");
        var dlg = new SaveFileDialog { Filter = "MEGA65 ROM patches (*.rdf)|*.rdf", FileName = Path.GetFileNameWithoutExtension(PatchChangedPath) + ".rdf" };
        if (dlg.ShowDialog() != true) return;
        Status = "Making the patch (romdiff compares the whole ROM, so this can take a few minutes)…";
        var r = await new RomPatcher(services.Runner).CreateAsync(romdiff, PatchOriginalPath, PatchChangedPath, dlg.FileName);
        Status = r.Succeeded
            ? $"Patch saved as {dlg.FileName} ({new FileInfo(dlg.FileName).Length:N0} bytes). Anyone with {Path.GetFileName(PatchOriginalPath).ToUpperInvariant()} can apply it."
            : $"romdiff failed: {RomPatcher.Problem(r)}";
    }, "Making the patch…");

    // ================= Core files (.cor) =================

    public sealed record TargetChoice(string Target, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<TargetChoice> CoreTargets { get; } = CoreFile.Targets.Select(t => new TargetChoice(t.Target, t.Label)).ToList();
    [ObservableProperty] private TargetChoice? _coreTarget;
    [ObservableProperty] private string? _coreBitPath;
    [ObservableProperty] private string _coreName = "";
    [ObservableProperty] private string _coreVersion = "";
    [ObservableProperty] private string _coreReport = "Choose a .cor file to check it, or build one from a .bit bitstream.";

    [RelayCommand]
    private Task VerifyCoreAsync()
    {
        var dlg = new OpenFileDialog { Filter = "MEGA65 core files (*.cor)|*.cor" };
        return dlg.ShowDialog() == true ? ShowCoreAsync(dlg.FileName) : Task.CompletedTask;
    }

    private Task ShowCoreAsync(string path) => GuardAsync(async () =>
    {
        var info = await Task.Run(() => CoreFile.Read(path));
        var label = CoreFile.Targets.FirstOrDefault(t => t.Target == info.Target).Label ?? info.Target;
        CoreReport =
            $"{Path.GetFileName(path)}\n" +
            $"Core: {info.CoreName}\nVersion: {info.CoreVersion}\nFor: {label} (model ${info.ModelId:X2})\n" +
            $"Size: {info.CoreSize:N0} bytes{(info.EmbeddedFiles > 0 ? $", {info.EmbeddedFiles} embedded file(s)" : "")}\n" +
            (info.IsValid
                ? $"Check: OK – the CRC32 ({info.StoredCrc:X8}) matches the contents."
                : info.SizeFits
                    ? $"Check: FAILED – the file is damaged (CRC32 stored {info.StoredCrc:X8}, calculated {info.ComputedCrc:X8}). Do not flash it."
                    : "Check: FAILED – the size in the header does not match the file. Do not flash it.");
        Status = info.IsValid ? "The core file is valid." : "The core file failed its check.";
    }, "Checking the core file…");

    /// <summary>Saves the FPGA bitstream stored inside a .cor, to load it over JTAG with Bitstream on the Device page.</summary>
    [RelayCommand]
    private void SaveBitFromCore()
    {
        var open = new OpenFileDialog { Title = "Core file to take the bitstream from", Filter = "MEGA65 core files (*.cor)|*.cor" };
        if (open.ShowDialog() != true) return;
        try
        {
            var bit = CoreFile.ExtractBitstream(File.ReadAllBytes(open.FileName));
            var save = new SaveFileDialog { Filter = "FPGA bitstreams (*.bit)|*.bit", FileName = Path.GetFileNameWithoutExtension(open.FileName) + ".bit" };
            if (save.ShowDialog() != true) return;
            File.WriteAllBytes(save.FileName, bit);
            Status = $"Saved {save.FileName}. Load it on the Device page with Bitstream (until power-off).";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Status = $"Cannot read that core: {e.Message}"; }
    }

    /// <summary>Makes an .mcs flash file for Vivado with the official bit2mcs: from a .bit, or a .cor placed at a slot.</summary>
    [RelayCommand]
    private Task MakeMcsAsync() => GuardAsync(async () =>
    {
        var commands = services.Commands ?? throw new InvalidOperationException("Making an .mcs uses bit2mcs from the device tools; install them first.");
        var open = new OpenFileDialog { Title = "Bitstream or core to convert", Filter = "Bitstreams and cores (*.bit;*.cor)|*.bit;*.cor" };
        if (open.ShowDialog() != true) return;
        int? slot = null;
        if (open.FileName.EndsWith(".cor", StringComparison.OrdinalIgnoreCase))
        {
            if (InputDialog.Ask("Flash slot", "Which flash slot is the core for (0–7)? Slot 0 is the factory core.", "1") is not { } answer) return;
            if (!int.TryParse(answer, out var n) || n is < 0 or > 7) { Status = "The slot must be a number from 0 to 7."; return; }
            slot = n;
        }
        var save = new SaveFileDialog { Filter = "Flash files (*.mcs)|*.mcs", FileName = Path.GetFileNameWithoutExtension(open.FileName) + (slot is { } s ? $"-slot{s}" : "") + ".mcs" };
        if (save.ShowDialog() != true) return;
        var r = await commands.BuildMcsAsync(open.FileName, save.FileName, slot);
        Status = r.Succeeded && File.Exists(save.FileName)
            ? $"Saved {save.FileName}. It is for flashing with Xilinx Vivado and a JTAG programmer; on an R4–R6 use MEGAFLASH with the .cor instead."
            : "bit2mcs failed: " + (r.StdOut + r.StdErr).Trim();
    }, "Making the .mcs file…");

    [RelayCommand]
    private void ChooseCoreBit()
    {
        var dlg = new OpenFileDialog { Filter = "FPGA bitstreams (*.bit)|*.bit" };
        if (dlg.ShowDialog() != true) return;
        CoreBitPath = dlg.FileName;
        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        CoreTarget ??= CoreTargets.FirstOrDefault(t => name.StartsWith(t.Target, StringComparison.OrdinalIgnoreCase)) ?? CoreTargets[0];
        if (CoreName.Length == 0) CoreName = name.Length > 31 ? name[..31] : name;
        if (CoreVersion.Length == 0) CoreVersion = DateTime.Now.ToString("yyyyMMdd");
    }

    [RelayCommand]
    private Task BuildCoreAsync() => GuardAsync(async () =>
    {
        if (CoreBitPath is null || CoreTarget is null) { Status = "Choose a .bit bitstream and the model it is for."; return; }
        var commands = services.Commands ?? throw new InvalidOperationException("Building a core uses bit2core from the device tools; install them first.");
        var dlg = new SaveFileDialog { Filter = "MEGA65 core files (*.cor)|*.cor", FileName = Path.GetFileNameWithoutExtension(CoreBitPath) + ".cor" };
        if (dlg.ShowDialog() != true) return;
        var r = await commands.BuildCoreAsync(CoreTarget.Target, CoreBitPath, CoreName.Trim(), CoreVersion.Trim(), dlg.FileName);
        if (!r.Succeeded || !File.Exists(dlg.FileName))
        {
            Status = "bit2core failed: " + string.Join(" ", (r.StdErr + r.StdOut).Split('\n').Where(l => l.Contains("ERROR")).Take(2)).Trim();
            return;
        }
        await ShowCoreAsync(dlg.FileName);
        Status = $"Core saved as {dlg.FileName}. Copy it to the SD card and install it with MEGAFLASH (hold No Scroll while switching on).";
    }, "Building the core file…");
}
