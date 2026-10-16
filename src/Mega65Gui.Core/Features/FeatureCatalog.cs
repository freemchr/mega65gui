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
        new("Filehost", "Sign in with your own Filehost account", FeatureStatus.Implemented, "Internet", "login.php session (as the website)", "Verified with a real account 2026-10-16. Optional DPAPI-encrypted saved password"),
        new("Filehost", "Owner-restricted (closed ROM) downloads", FeatureStatus.Implemented, "Internet", "Private link + readfiledetail under your session", "Server decides access; needs your redeemed ownership code. Verified with a real account and a with-ROM update 2026-10-16"),
        new("Update", "Update My MEGA65 (12-step guided workflow)", FeatureStatus.Implemented, "Card reader, USB/JTAG, Ethernet", "UpdateEngine", "Approval before every write; backups; report. Run against a real card copy (R6, with ROM) 2026-10-16"),
        new("Update", "Update the SD card inside the MEGA65 (USB/JTAG or Ethernet)", FeatureStatus.Implemented, "USB/JTAG, Ethernet", "mega65_ftp dir/get/put", "Verified in-app over USB/JTAG on an R6 2026-10-16; Ethernet still to confirm"),
        new("Update", "Core flashing from the PC", FeatureStatus.NotOffered, "-", "MEGAFLASH on device (guided)", "Official tools refuse R4-R6; R2/R3 need Vivado"),
        new("Tools", "Install official mega65-tools from the Filehost", FeatureStatus.Implemented, "Internet", "Filehost release/dev packages", "GPL-3.0 tools run as separate processes"),

        // Connectivity
        new("Connection", "USB/JTAG serial connection", FeatureStatus.Implemented, "USB/JTAG", "m65 -l COMx -s 2000000", "Verified on an R6 (TE0790 adapter, COM3) 2026-10-16. The MEGA65 must be running its core, not the ROM selection screen"),
        new("Connection", "Device state readout (model, core, ROM)", FeatureStatus.Implemented, "USB/JTAG", "Serial monitor: h, mffd3629, m20016", "Verified on an R6 running core 0.96 2026-10-16; real responses kept as a test"),
        new("Connection", "Ethernet discovery and connection", FeatureStatus.HardwareUnverified, "Ethernet", "etherload -D", "Core 0.96+; SHIFT+£ on the MEGA65"),

        // Transfers
        new("Transfer", "PRG send and run", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "m65 -F [-4] [-r] / etherload -i [-r]", ""),
        new("Transfer", "SID send", FeatureStatus.HardwareUnverified, "USB/JTAG", "m65 -F -4 -r file.sid", "Implemented; untested on hardware"),
        new("Transfer", "BIT load (volatile)", FeatureStatus.HardwareUnverified, "USB/JTAG", "m65 -q", "Lost at power-off; uses m65's built-in JTAG loader"),
        new("Transfer", "HIC (HICKUP) load", FeatureStatus.HardwareUnverified, "USB/JTAG", "m65 -F -k", ""),
        new("Transfer", "ROM load (volatile)", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "m65 -F -R / etherload -R", "User-supplied ROM files only; lost at power-off"),
        new("Transfer", "BASIC program text transfer", FeatureStatus.HardwareUnverified, "USB/JTAG", "m65 -T - (stdin)", "Types the listing (lowercased) at the READY prompt"),
        new("Transfer", "Drag-and-drop routing by file type", FeatureStatus.HardwareUnverified, "Any", "UI", "PRG, SID, BASIC text, ROM, BIT, HICKUP on the Device page; files onto the card in the SD Card Browser"),
        new("Transfer", "PRG autoload folder watch", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "UI", "Sends each changed .prg in a watched folder"),

        // SD card
        new("SD card", "Local SD card synchronisation (plan, back up, apply, verify)", FeatureStatus.Implemented, "Card reader", "SdSyncPlanner/Executor", "Never deletes; case-insensitive FAT32 matching"),
        new("SD card", "D81 contiguity check after copy", FeatureStatus.HardwareUnverified, "Card reader", "FSCTL_GET_RETRIEVAL_POINTERS", "Windows only"),
        new("SD card", "Remote SD manager (dir, get, put, del, rename, mkdir, mount)", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "Interactive mega65_ftp session", "SD Card Browser; browsing verified on an R6, uploads and deletes still to confirm"),
        new("SD card", "Local SD file manager and eject", FeatureStatus.Partial, "Card reader", "Native", "The SD Card Browser's PC pane covers files; eject not yet"),

        // Device control
        new("Control", "Reset", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "m65 -F / etherload -5", ""),
        new("Control", "C64 / MEGA65 mode switching", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "m65 -F -4 / etherload --c64mode --m65mode", ""),
        new("Control", "Start a core from a flash slot", FeatureStatus.HardwareUnverified, "USB/JTAG", "m65 -Z", "Nothing is written to flash"),
        new("Control", "PAL / NTSC", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "m65 -p/-n; etherload --pal/--ntsc", ""),
        new("Control", "Screenshot capture", FeatureStatus.HardwareUnverified, "USB/JTAG", "m65 -S<file>", "Not available over Ethernet"),
        new("Control", "Remote keyboard", FeatureStatus.HardwareUnverified, "USB/JTAG", "Serial monitor sffd3615", "Terminal page; key matrix from mega65-tools' documented layout"),
        new("Control", "Monitor terminal", FeatureStatus.HardwareUnverified, "USB/JTAG", "Serial monitor, 2 Mbaud", "Terminal page"),

        // Offline tools
        new("Disk images", "D81 create, list, add, extract, delete, rename", FeatureStatus.Implemented, "None", "Native", "Checked against a real public D81; tested round trips"),
        new("ROM", "ROM Configurator (speed, font, colours)", FeatureStatus.Implemented, "None", "Native ($17FF8-$17FFF)", "Edits a copy of the ROM; ROM 920252+"),
        new("ROM", "ROM patch apply/create (.rdf)", FeatureStatus.Partial, "None", "romdiff", "Apply via romdiff (not yet tried with a real patch); create not offered"),
        new("Cores", "COR/MCS build and verify", FeatureStatus.Planned, "None", "coretool.py / bit2core", "coretool needs Python"),
        new("Developer", "Memory dump, unit-test runner, breakpoints", FeatureStatus.Planned, "USB/JTAG", "m65 --memsave, -u, -B", "Not exposed by M65Connect"),
    ];
}
