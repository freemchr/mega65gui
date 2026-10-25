# M65Connect feature parity

Sources:
- **M65Connect 2.5 for Windows** (released 2025-11-10), the version people download. Studied 2026-10-25; this is what the M65Connect column describes.
- `github.com/MEGA65/m65connect` (master, Xojo, GPL-3.0), studied 2026-10-16 and 2026-10-25. Current logic lives in `Code/M65Connect.xojo_binary_project`; the text `.xojo_code` files are stale. Master has unreleased work beyond 2.5 (D64 and REL files, D81 file lock, type change and reordering); it is marked "master only" where it matters.
- The M65Connect manual on the MEGA65 Confluence (Functions, SD Card Manager, Terminal, Remote keyboard, Connect PC).
- `github.com/MEGA65/mega65-tools` (`development` @ 44c63d2): `m65.c`, `mega65_ftp.c`, `etherload.c`, `m65common.c`.

## How M65Connect works

- **Shells out to the official tools** for almost all hardware access: `m65`, `mega65_ftp`, `etherload`, `romdiff`, `bit2core` and `bit2mcs` (all still shipped in 2.5). It also uses `bsdiff`/`bspatch`, which are not MEGA65 tools, for `.bdf` ROM patches.
- **Implements natively:**
  - the serial terminal and remote keyboard (2,000,000 baud; keys injected via `sffd3615`);
  - D81 editing (D64 and REL on master only);
  - the ROM Configurator (bytes `$17FF8`–`$17FFF`);
  - PRG autoload;
  - FTDI listing (D2XX).
- **Connection choice:** once a LAN address is set (found with `etherload -D`), it is used for PRG send, ROM load, GO64/GO65, reset, PAL/NTSC and mount only. Everything else goes over USB/JTAG.

MEGA65 GUI takes the same tools-first approach. It does **not** launch M65Connect and copies none of its code (see [LICENSING.md](LICENSING.md)).

## Status legend

| Status | Meaning |
|---|---|
| Implemented | Works and has automated tests; needs no hardware |
| Hardware unverified | Built on documented tool flags or protocols; not yet confirmed on a physical MEGA65 |
| Partial | Some of the behaviour exists |
| Planned | Not started |
| Not offered | Deliberately excluded (unsafe, or impossible with official tools) |

The in-app **Features** page shows the same list from `src/Mega65Gui.Core/Features/FeatureCatalog.cs`. A unit test keeps this document and that catalogue in sync.

## Matrix

| Area | Feature | M65Connect | Official CLI | MEGA65 GUI |
|---|---|---|---|---|
| Filehost | Browse, search and filter the public catalogue | – | – | Implemented |
| Filehost | File detail and version history | – | – | Implemented |
| Filehost | Download with resume, size and SHA-256 manifest | – | `mega65_ftp fhget` (to device) | Implemented |
| Filehost | Sign in with your own Filehost account | – | – | Implemented (verified with a real account) |
| Filehost | Owner-restricted (closed ROM) downloads | – | – | Implemented (via your sign-in; verified with a real account) |
| Filehost | Upload a new file or a new version of your own files | – | – | Implemented, not yet tried on the live site |
| Filehost | Edit the attributes of your own files | – | – | Implemented, not yet tried on the live site |
| Filehost | Upload new GitHub releases as Filehost versions | – | – | Implemented, not yet tried on the live site |
| Update | Update My MEGA65 (12-step guided workflow) | – | – | Implemented |
| Update | Update the SD card inside the MEGA65 (USB/JTAG or Ethernet) | Manual, via SD Card Manager | `mega65_ftp` get/put | Implemented (verified over USB/JTAG on an R6; Ethernet unverified) |
| Update | Core flashing from the PC | – | `mega65_ftp flash`: R2/R3 + Vivado only; refuses R4–R6 and Ethernet | Not offered: guided MEGAFLASH |
| App | Check for a new MEGA65 GUI version | – | – | Implemented |
| App | Report a problem with the app's details | – | – | Implemented |
| Tools | Install official mega65-tools from the Filehost | Bundles binaries | – | Implemented |
| Connection | USB/JTAG serial connection | COM port, FTDI D2XX | `m65 -l COMx -s 2000000` (on Windows `-l` is required) | Implemented (verified on an R6) |
| Connection | Device state readout (model, core, ROM) | Partial | Monitor `h`, `mffd3629 1`, `m20016` | Implemented (verified on an R6) |
| Connection | Tell the MEGA65's USB/JTAG port from other COM ports | Connection Wizard (switch off and on to find the new FTDI port), List FTDI Devices | – | Hardware unverified (labels and picks by Windows device ID) |
| Connection | Ethernet discovery and connection | `etherload -D` | `etherload -D`, `-i` | Hardware unverified |
| Transfer | PRG send and run | Yes (J, E) | `m65 -F [-4] [-r] f.prg`, `etherload -i ip [-4/-5] [-r] f.prg` | Implemented (verified by the hardware self-test on an R6) |
| Transfer | PRG send options (no reset, load address from the file, PAL/NTSC, mount a D81 over Ethernet) | `-1` (with SYS auto-detect), `-n`/`-p`; Ethernet `etherload --run` | `m65 [-F] [-1] [-p/-n]`, `etherload [--pal/--ntsc] [-m d81]` | Hardware unverified |
| Transfer | Use a D81 on this PC as the MEGA65's drive 8 | Virtual D81 (`m65 -d`) | `m65 -d file.d81`, runs until stopped | Hardware unverified (backup copy first; disk virtualisation turned off with `sffd3659 00` afterwards) |
| Transfer | SID send | Yes (J) | `m65 -F -4 -r f.sid` | Implemented (verified on an R6) |
| Transfer | BIT load (volatile) | Yes (J) | `m65 -b` / `-q` | Hardware unverified |
| Transfer | HIC (HICKUP) load | Yes (J) | `m65 -k` | Implemented (verified by the hardware self-test on an R6) |
| Transfer | ROM load (volatile) | Yes (J, E) | `m65 -R`, `etherload -R` | Implemented (verified by the hardware self-test on an R6) |
| Transfer | BASIC program text transfer | Types text (J) | `m65 -T` / `-t` | Implemented (verified by the hardware self-test on an R6) |
| Transfer | BASIC editor (write, open, save .bas, type into the MEGA65) | BASIC editor | `m65 -T -` | Implemented |
| Transfer | Drag-and-drop routing by file type | Yes (over Ethernet only PRG and ROM) | – | Hardware unverified |
| Transfer | PRG autoload folder watch | Yes | – | Hardware unverified |
| SD card | Local SD card synchronisation (plan, back up, apply, verify) | – | – | Implemented |
| SD card | D81 contiguity check after copy | `mega65_ftp clusters` on card files | `mega65_ftp clusters` (remote) | Hardware unverified (Info in the SD Card Browser runs `clusters` too) |
| SD card | Remote SD manager (dir, get, put, del, rename, mkdir, mount) | Yes (J, E) | `mega65_ftp -c …` | Implemented (verified by the hardware self-test on an R6) |
| SD card | Copy whole folders to and from the card inside the MEGA65 | Yes | `mega65_ftp mkdir`/`cd`/`put`/`get` | Hardware unverified (progress bar; Stop between files) |
| SD card | Replace, skip, replace all or skip all for files already there | Yes | – | Implemented |
| SD card | Name check before copying to the card | Leading `$`, `, : = "`, empty files, long D81 names | – | Implemented (also `* ?`, non-ASCII and D81 size; optional capital names) |
| SD card | Card free space and the ROMs on the card | Card info | `mega65_ftp df`, `roms` | Hardware unverified |
| SD card | Local SD file manager and eject | Yes | Native lock/dismount/eject | Hardware unverified (card reader picker and Eject in the SD Card Browser) |
| Control | Reset | Yes (J, E) | `m65 -F`, `etherload -5` | Implemented (verified by the hardware self-test on an R6) |
| Control | C64 / MEGA65 mode switching | Yes (J, E) | `m65 -F -4`, `etherload --c64mode/--m65mode` | Implemented (verified by the hardware self-test on an R6) |
| Control | Start a core from a flash slot | Via MEGAFLASH | `m65 -Z` | Implemented (verified by the hardware self-test on an R6) |
| Control | PAL / NTSC | Yes | `m65 -p/-n`, `etherload --pal/--ntsc` | Implemented (verified over USB/JTAG on an R6) |
| Control | Screenshot capture | Yes (J) | `m65 -S<file>` | Implemented (verified by the hardware self-test on an R6) |
| Control | Remote keyboard | Native | `m65 -T` (batch only) | Implemented (verified by the hardware self-test on an R6) |
| Control | Full MEGA65 keyboard (ESC, TAB, ALT, HELP, NO SCROLL, F9-F14, CTRL and MEGA combinations) | Native, including 40/80 | Monitor `sffd3615`; matrix positions 64–71 | Hardware unverified (RESTORE, CAPS LOCK and 40/80 are outside the matrix) |
| Control | Go 64 and Go 65 without a reset | GO64 / GO65 | `m65 -4` without `-F`; `SYS 58552` typed | Hardware unverified |
| Control | Save the console output and terminal text | Console save | – | Implemented |
| Control | Screenshot preview on the Device page | Screenshot preview | `m65 -S<file>` | Implemented |
| Control | Monitor terminal | Native | `m65dbg` (not shipped for Windows) | Implemented (verified by the hardware self-test on an R6) |
| Disk images | D81 create, list, add, extract, delete, rename | Native, with disk name/ID, empty disk and free blocks (lock, type change, reorder on master only) | None | Implemented (with disk name/ID, free blocks) |
| Disk images | Copy files between D81 images | Image to image copy | None | Implemented |
| ROM | ROM Configurator (speed, font, colours) | Native | None | Implemented |
| ROM | ROM settings presets | `.rcg` presets | None | Implemented (own JSON format, not `.rcg`) |
| ROM | ROM patch apply/create (.rdf) | `.rdf` with `romdiff`; `.bdf` with `bsdiff`/`bspatch` | `romdiff` | Implemented (`.rdf` round trip checked with the official romdiff; `.bdf` not offered: bsdiff is not a MEGA65 tool) |
| Cores | COR build and verify | `bit2core` | `bit2core` + header/CRC32 check | Implemented (verified with the 0.97.2 core) |
| Cores | Save the .bit inside a .cor | Save .bit from .cor | Header and CRC check, then the embedded .bit | Implemented |
| Cores | Make an .mcs flash file for Vivado | Create MCS (`bit2mcs`) | `bit2mcs in out [offset]` | Implemented (checked with the official bit2mcs) |
| Cores | Core library (import, SHA-256 duplicates, favourites, categories, tags, notes, artwork, export) | – | CoreLibrary (index.json, verified copies) | Implemented |
| Cores | Hardware compatibility check for cores | – | CoreTargets: .cor target and model ID against the model read from the MEGA65 | Implemented |
| Cores | Discover and download cores from the Filehost | – | Catalogue category Firmware: Alternate Core, Arcade Core, Release Package | Implemented |
| Cores | Run a library core temporarily over JTAG | – | m65 -q; a .cor through the .bit stored in it | Implemented (Vixen-32 on an R6) |
| Cores | Copy a core's own SD-card files | – | Package folder copied as laid out; mega65_ftp mkdir/cd/put with read-back | Hardware unverified |
| Cores | Stage a core on the SD card for MEGAFLASH | – | SdSyncPlanner/Executor or mega65_ftp with read-back | Hardware unverified |
| Cores | List cores stored on the SD card | – | Folder scan with header check; mega65_ftp dir | Hardware unverified |
| Cores | Start a core directly from the SD card without flashing | – | - | Not offered |
| Developer | Memory dump, unit-test runner, breakpoints | – | Monitor `M`/`b`/`t0`; `m65 -u -w` | Implemented (verified on an R6) |

## Hardware facts used

All taken from mega65-tools source.

- **Model byte at `$FFD3629`:**

  | Byte | Hardware |
  |---|---|
  | 1–6 | R1–R6 |
  | `$21`, `$22` | MEGAphone |
  | `$40`–`$42` | Nexys |
  | `$FD` | Wukong |
  | `$FE` | Simulation |

  `m65common.c` has no names for R4–R6, so the app maps the byte itself.
- **Core build:** the line after `build GIT: ` in the monitor `h` output. Release packages embed the same commit hash in their file names.
- **ROM version:** 7 ASCII bytes at `$20016`.
- **Real R6 responses (core 0.96, 2026-10-16):**
  - `h` → `build GIT: release-0.96,20240224.00,3c10488`
  - `mffd3629 1` → `:0FFD3629:06FF3F…` (model byte `$06` = R6)
  - `m20016 7` → `:00020016:56393230333935…` (`V920395`)
- **Note:** the monitor does not answer while the MEGA65 shows the ROM selection screen. It must be running its core normally.
- **Running core, not slot contents:** `h` reports the core running now. That capture was the factory core in slot 0; slot 1 held a newer core. MEGAFLASH shows what each slot contains.
- **Core flashing:**
  - `mega65_ftp` refuses R4–R6 ("Please use MEGAFLASH on the device instead").
  - R2/R3 need Vivado.
  - Ethernet flashing is refused.

## Not offered

- **Vivado bitstream loading (`m65 -b` with `-v`):** needs Xilinx Vivado; `m65 -q` loads a .bit without it.
- **`.bdf` ROM patches:** they need `bsdiff`/`bspatch`, which are not part of the official MEGA65 tools.
- **Connection Wizard (switch off and on to find the port):** the COM port lists name the MEGA65's adapter from its Windows device ID instead.
