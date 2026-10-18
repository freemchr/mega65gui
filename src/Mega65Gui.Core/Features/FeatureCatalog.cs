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
        new("Filehost", "Upload a new file or a new version of your own files", FeatureStatus.HardwareUnverified, "Internet", "savefileupload.php under your session (as My File Uploads)", "MEGA65 File Host Upload page. All website attributes, new or replace version; asks before every upload. Not yet tried against the live site"),
        new("Filehost", "Edit the attributes of your own files", FeatureStatus.HardwareUnverified, "Internet", "savefileupdate.php under your session", "Title, type, description, tags, short link, video mode, platform, access, core and ROM version, intro disk, highscore"),
        new("Filehost", "Upload new GitHub releases as Filehost versions", FeatureStatus.HardwareUnverified, "Internet", "GitHub REST releases API + savefileupload.php", "Per-file link with a release-file pattern; version from the tag; optional check at start-up, always confirmed"),
        new("Update", "Update My MEGA65 (12-step guided workflow)", FeatureStatus.Implemented, "Card reader, USB/JTAG, Ethernet", "UpdateEngine", "Approval before every write; backups; report. Run against a real card copy (R6, with ROM) 2026-10-16"),
        new("Update", "Update the SD card inside the MEGA65 (USB/JTAG or Ethernet)", FeatureStatus.Implemented, "USB/JTAG, Ethernet", "mega65_ftp dir/get/put", "Verified in-app over USB/JTAG on an R6 2026-10-16; Ethernet still to confirm"),
        new("Update", "Core flashing from the PC", FeatureStatus.NotOffered, "-", "MEGAFLASH on device (guided)", "Official tools refuse R4-R6; R2/R3 need Vivado"),
        new("Tools", "Install official mega65-tools from the Filehost", FeatureStatus.Implemented, "Internet", "Filehost release/dev packages", "GPL-3.0 tools run as separate processes"),

        // Connectivity
        new("Connection", "USB/JTAG serial connection", FeatureStatus.Implemented, "USB/JTAG", "m65 -l COMx -s 2000000", "Verified on an R6 (TE0790 adapter, COM3) 2026-10-16. The MEGA65 must be running its core, not the ROM selection screen"),
        new("Connection", "Device state readout (model, core, ROM)", FeatureStatus.Implemented, "USB/JTAG", "Serial monitor: h, mffd3629, m20016", "Verified on an R6 running core 0.96 2026-10-16; real responses kept as a test"),
        new("Connection", "Ethernet discovery and connection", FeatureStatus.HardwareUnverified, "Ethernet", "etherload -D", "Core 0.96+; SHIFT+£ on the MEGA65"),

        // Transfers
        new("Transfer", "PRG send and run", FeatureStatus.Implemented, "USB/JTAG, Ethernet", "m65 -F [-4] [-r] / etherload -i [-r]", "Verified by the hardware self-test on an R6 2026-10-16"),
        new("Transfer", "SID send", FeatureStatus.Implemented, "USB/JTAG", "m65 -F -4 -r file.sid", "Verified on an R6 2026-10-16 (PSID v2, The Last Ninja): plays in C64 mode, keys 1-9 pick the song"),
        new("Transfer", "BIT load (volatile)", FeatureStatus.HardwareUnverified, "USB/JTAG", "m65 -q", "Lost at power-off; uses m65's built-in JTAG loader. Needs WinUSB on the adapter's Interface 0 on Windows; the app explains this when it is missing"),
        new("Transfer", "HIC (HICKUP) load", FeatureStatus.Implemented, "USB/JTAG", "m65 -F -k", "Verified by the extended hardware self-test on an R6 2026-10-16"),
        new("Transfer", "ROM load (volatile)", FeatureStatus.Implemented, "USB/JTAG, Ethernet", "m65 -F -R / etherload -R", "Verified by the extended hardware self-test on an R6 2026-10-16"),
        new("Transfer", "BASIC program text transfer", FeatureStatus.Implemented, "USB/JTAG", "m65 -T - (stdin)", "Verified by the hardware self-test on an R6 2026-10-16"),
        new("Transfer", "Drag-and-drop routing by file type", FeatureStatus.HardwareUnverified, "Any", "UI", "PRG, SID, BASIC text, ROM, BIT, HICKUP on the Device page; files onto the card in the SD Card Browser"),
        new("Transfer", "PRG autoload folder watch", FeatureStatus.HardwareUnverified, "USB/JTAG, Ethernet", "UI", "Sends each changed .prg in a watched folder"),

        // SD card
        new("SD card", "Local SD card synchronisation (plan, back up, apply, verify)", FeatureStatus.Implemented, "Card reader", "SdSyncPlanner/Executor", "Never deletes; case-insensitive FAT32 matching"),
        new("SD card", "D81 contiguity check after copy", FeatureStatus.HardwareUnverified, "Card reader", "FSCTL_GET_RETRIEVAL_POINTERS", "Windows only"),
        new("SD card", "Remote SD manager (dir, get, put, del, rename, mkdir, mount)", FeatureStatus.Implemented, "USB/JTAG, Ethernet", "Interactive mega65_ftp session", "SD Card Browser. Browse, upload, download and delete verified on an R6; deleting folders needs the development tools build"),
        new("SD card", "Local SD file manager and eject", FeatureStatus.HardwareUnverified, "Card reader", "Native; lock, dismount and eject the volume", "The SD Card Browser's PC pane covers files; Eject on the Update and SD Card Sync pages (not yet tried with a card reader)"),

        // Device control
        new("Control", "Reset", FeatureStatus.Implemented, "USB/JTAG, Ethernet", "m65 -F / etherload -5", "Verified by the hardware self-test on an R6 2026-10-16"),
        new("Control", "C64 / MEGA65 mode switching", FeatureStatus.Implemented, "USB/JTAG, Ethernet", "m65 -F -4 / etherload --c64mode --m65mode", "Verified by the hardware self-test on an R6 2026-10-16"),
        new("Control", "Start a core from a flash slot", FeatureStatus.Implemented, "USB/JTAG", "m65 -Z", "Verified by the extended hardware self-test on an R6 2026-10-16"),
        new("Control", "PAL / NTSC", FeatureStatus.Implemented, "USB/JTAG, Ethernet", "m65 -p/-n; etherload --pal/--ntsc", "Verified over USB/JTAG on an R6 2026-10-16 ($D06F bit 7 set by -n, cleared by -p); Ethernet still to confirm"),
        new("Control", "Screenshot capture", FeatureStatus.Implemented, "USB/JTAG", "m65 -S<file>", "Verified by the extended hardware self-test on an R6 2026-10-16"),
        new("Control", "Remote keyboard", FeatureStatus.Implemented, "USB/JTAG", "Serial monitor sffd3615", "Verified by the hardware self-test on an R6 2026-10-16"),
        new("Control", "Monitor terminal", FeatureStatus.Implemented, "USB/JTAG", "Serial monitor, 2 Mbaud", "Verified by the extended hardware self-test on an R6 2026-10-16"),

        // Offline tools
        new("Disk images", "D81 create, list, add, extract, delete, rename", FeatureStatus.Implemented, "None", "Native", "Checked against a real public D81; tested round trips"),
        new("ROM", "ROM Configurator (speed, font, colours)", FeatureStatus.Implemented, "None", "Native ($17FF8-$17FFF)", "Edits a copy of the ROM; ROM 920252+"),
        new("ROM", "ROM patch apply/create (.rdf)", FeatureStatus.Partial, "None", "romdiff", "Apply via romdiff (not yet tried with a real patch); create not offered"),
        new("Cores", "COR build and verify", FeatureStatus.Implemented, "None", "bit2core; header and CRC32 check", "Disk & ROM Tools. Verified 2026-10-16: a core built from the 0.97.2 .bit has the same bitstream as the official .cor and both pass the CRC check"),
        new("Developer", "Memory dump, unit-test runner, breakpoints", FeatureStatus.Implemented, "USB/JTAG", "Monitor M/b/t0; m65 -u -w", "Terminal and Device pages. Verified on an R6 2026-10-16: an 8 KB dump matched MEGA65.ROM, a breakpoint stopped and resumed the CPU, a test program's PASS/FAIL were reported. m65 --memsave is avoided: on Windows it corrupts $0A bytes"),
    ];
}
