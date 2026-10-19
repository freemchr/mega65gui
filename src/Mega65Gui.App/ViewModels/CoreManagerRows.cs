using CommunityToolkit.Mvvm.ComponentModel;
using Mega65Gui.Core.Cores;
using Mega65Gui.Core.Device;

namespace Mega65Gui.App.ViewModels;

/// <summary>A library core as shown in the list and card views, with its compatibility against the chosen MEGA65.</summary>
public sealed partial class LibraryRow(CoreLibraryEntry entry, CompatibilityVerdict verdict, string? artworkPath, bool hasUpdate) : ObservableObject
{
    public CoreLibraryEntry Entry { get; } = entry;
    public CompatibilityVerdict Verdict { get; } = verdict;
    public string Id => Entry.Id;
    public string Name => Entry.DisplayName;
    public string? Version => Entry.Version;
    public string KindLabel => Entry.Kind == CoreFileKind.Cor ? ".cor" : ".bit";
    public string? ArtworkPath { get; } = artworkPath;
    public bool HasArtwork => ArtworkPath is not null;
    public bool Favorite => Entry.Favorite;
    public bool HasUpdate { get; } = hasUpdate;
    public CompatibilityResult Compatibility => Verdict.Result;
    public string CompatibilityLabel => Verdict.Result switch
    {
        CompatibilityResult.Compatible => "Compatible",
        CompatibilityResult.Incompatible => "Not for this MEGA65",
        _ => "Not confirmed",
    };
    public string TargetLabel => CoreManagerText.Target(Entry);
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}

public enum FilehostCoreState { Available, InLibrary, UpdateAvailable }

public sealed partial class FilehostCoreRow(FilehostCore core, FilehostCoreState state) : ObservableObject
{
    public FilehostCore Core { get; } = core;
    public string Title => Core.Title;
    public string Kind => Core.Kind;
    public string? Author => Core.File.Author;
    public DateTime? Updated => Core.File.Updated;
    public string? VersionHint => Core.VersionHint;
    public FilehostCoreState State { get; } = state;
    public string StateLabel => State switch
    {
        FilehostCoreState.InLibrary => "In library",
        FilehostCoreState.UpdateAvailable => "Update",
        _ => "",
    };
    /// <summary>Boards named in the title or file name. Shown as hints, never as compatibility.</summary>
    public string NamedModels => Core.NamedModels.Count == 0 ? "" : string.Join(", ", Core.NamedModels);
}

/// <summary>A core file found in the root of the SD card.</summary>
public sealed record CardCoreRow(string Name, long Size, string Detail, string LibraryState, CompatibilityResult? Compatibility, string? LocalPath);

public sealed partial class SlotRow(int number, string? note, bool exists) : ObservableObject
{
    public int Number { get; } = number;
    public bool IsFactory => Number == 0;
    public bool Exists { get; } = exists;
    public string Label => IsFactory ? "Slot 0 · factory core" : $"Slot {Number}";
    [ObservableProperty] private string _note = note ?? "";
}

public sealed record ModelChoice(string Label, HardwareModel? Model)
{
    public override string ToString() => Label;
}

internal static class CoreManagerText
{
    public static string BoardLabel(string? target) =>
        CoreFile.Targets.FirstOrDefault(t => string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase)).Label ?? target ?? "unknown";

    public static string Target(CoreLibraryEntry e) => e.Kind == CoreFileKind.Cor
        ? BoardLabel(e.Core?.Target)
        : e.DeclaredModel is { } m ? $"{m} (your label)" : "Not recorded in .bit files";
}
