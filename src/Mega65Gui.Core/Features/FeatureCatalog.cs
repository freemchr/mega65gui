namespace Mega65Gui.Core.Features;

public enum FeatureStatus
{
    /// <summary>Implemented and covered by automated tests; needs no hardware.</summary>
    Implemented,
    /// <summary>Implemented using documented tool flags or protocols, but not yet confirmed on a physical MEGA65.</summary>
    HardwareUnverified,
    /// <summary>Part of the behaviour exists.</summary>
    Partial,
    Planned,
    /// <summary>Deliberately not offered (unsafe, or impossible with official tools).</summary>
    NotOffered,
}

public sealed record Feature(string Area, string Name, FeatureStatus Status, string Connection, string Mechanism, string Notes);

/// <summary>
/// Feature parity tracker against M65Connect's documented functions. This is the single source
/// for the in-app Features page and docs/FEATURE-MATRIX.md.
/// </summary>
public static class FeatureCatalog
{
    public static IReadOnlyList<Feature> All { get; } =
    [
        // Filehost and updates (beyond M65Connect)
        new("Filehost", "Browse, search and filter the public catalogue", FeatureStatus.Implemented, "Internet", "readfilespublic.php", "6 h on-disk cache; offline fallback"),
        new("Filehost", "File detail and version history", FeatureStatus.Implemented, "Internet", "readfiledetail.php, readversionlistpublic.php", "Version lists are only requested for public files"),
        new("Filehost", "Download with resume, size and SHA-256 manifest", FeatureStatus.Implemented, "Internet", "Static /files/ URLs with Range", "Filehost publishes no checksums; SHA-256 is recorded locally"),
        new("Filehost", "Owner-restricted (closed ROM) downloads", FeatureStatus.NotOffered, "Internet", "Hand-off to the website", "Requires Filehost login plus a redeemed ownership code; not automated"),
        new("Update", "Update My MEGA65 (12-step guided workflow)", FeatureStatus.Implemented, "USB/JTAG or SD only", "UpdateEngine", "Approval before every write; backups; report"),
        new("Update", "Core flashing from the PC", FeatureStatus.NotOffered, "-", "MEGAFLASH on device (guided)", "Official tools refuse R4-R6; R2/R3 need Vivado"),
        new("Tools", "Install official mega65-tools from the Filehost", FeatureStatus.Implemented, "Internet", "Filehost release/dev packages", "GPL-3.0 tools run as separate processes"),

        // Connectivity
        new("Connection", "USB/JTAG serial connection", FeatureStatus.HardwareUnverified, "USB/JTAG", "m65 -l COMx -s 2000000", "Windows m65 needs the port passed explicitly"),
        new("Connection", "Device state readout (model, core, ROM)", FeatureStatus.HardwareUnverified, "USB/JTAG", "Serial monitor: h, mffd3629, m20016", "Clean-room implementation"),
        new("Connection", "Ethernet discovery and connection", FeatureStatus.HardwareUnverified, "Ethernet", "etherload -D", "Core 0.96+; SHIFT+£ on the MEGA65"),

        // Transfers
        new("Transfer", "PRG send and run", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "m65 -F [-4] [-r] / etherload -i [-r]", ""),
        new("Transfer", "SID send", FeatureStatus.Planned, "USB/JTAG", "m65 -F -4 -r file.sid", ""),
        new("Transfer", "BIT load (volatile)", FeatureStatus.Planned, "USB/JTAG", "m65 -b / -q", "Lost at power-off"),
        new("Transfer", "HIC (HICKUP) load", FeatureStatus.Planned, "USB/JTAG", "m65 -k", ""),
        new("Transfer", "ROM load (volatile)", FeatureStatus.Planned, "USB/JTAG, Ethernet", "m65 -R / etherload -R", "User-supplied ROM files only"),
        new("Transfer", "BASIC program text transfer", FeatureStatus.Planned, "USB/JTAG", "m65 -T / -t", "Types text; a tokeniser is a stretch goal"),
        new("Transfer", "Drag-and-drop routing by file type", FeatureStatus.Partial, "Any", "UI", "PRG drop on the Device page"),
        new("Transfer", "PRG autoload folder watch", FeatureStatus.Planned, "USB/JTAG, Ethernet", "UI", ""),

        // SD card
        new("SD card", "Local SD card synchronisation (plan, back up, apply, verify)", FeatureStatus.Implemented, "Card reader", "SdSyncPlanner/Executor", "Never deletes; case-insensitive FAT32 matching"),
        new("SD card", "D81 contiguity check after copy", FeatureStatus.HardwareUnverified, "Card reader", "FSCTL_GET_RETRIEVAL_POINTERS", "Windows only"),
        new("SD card", "Remote SD manager (dir, get, put, del, rename, mkdir, mount)", FeatureStatus.Partial, "USB/JTAG, Ethernet", "mega65_ftp -c", "Command console implemented; browser UI planned"),
        new("SD card", "Local SD file manager and eject", FeatureStatus.Planned, "Card reader", "Native", ""),

        // Device control
        new("Control", "Reset", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "m65 -F / etherload -5", ""),
        new("Control", "C64 / MEGA65 mode switching", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "m65 -F -4 / etherload --c64mode --m65mode", ""),
        new("Control", "PAL / NTSC", FeatureStatus.Planned, "USB/JTAG, Ethernet", "m65 -p/-n; etherload --pal/--ntsc", ""),
        new("Control", "Screenshot capture", FeatureStatus.HardwareUnverified, "USB/JTAG", "m65 -S<file>", "Not available over Ethernet"),
        new("Control", "Remote keyboard", FeatureStatus.Planned, "USB/JTAG", "Native serial (sffd3615)", "Clean-room key table needed"),
        new("Control", "Monitor terminal", FeatureStatus.Planned, "USB/JTAG", "Native serial 2 Mbaud", "m65dbg is not shipped for Windows"),

        // Offline tools
        new("Disk images", "D81 create, list, add, extract, delete, rename", FeatureStatus.Planned, "None", "Native", "No official CLI exists"),
        new("ROM", "ROM Configurator (speed, font, colours)", FeatureStatus.Planned, "None", "Native ($17FF8-$17FFF)", "ROM 920252+"),
        new("ROM", "ROM patch apply/create (.rdf)", FeatureStatus.Planned, "None", "romdiff", ".bdf needs third-party bsdiff"),
        new("Cores", "COR/MCS build and verify", FeatureStatus.Planned, "None", "coretool.py / bit2core", "coretool needs Python"),
        new("Developer", "Memory dump, unit-test runner, breakpoints", FeatureStatus.Planned, "USB/JTAG", "m65 --memsave, -u, -B", "Not exposed by M65Connect"),
    ];
}
