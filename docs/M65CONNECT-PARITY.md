# M65Connect feature parity

Sources (studied 2026-10-16):
- `github.com/MEGA65/m65connect` (master, Xojo, GPL-3.0). Current logic lives in `Code/M65Connect.xojo_binary_project`; the text `.xojo_code` files are stale.
- The M65Connect manual on the MEGA65 Confluence (Functions, SD Card Manager, Terminal, Remote keyboard, Connect PC).
- `github.com/MEGA65/mega65-tools` (`development` @ 44c63d2): `m65.c`, `mega65_ftp.c`, `etherload.c`, `m65common.c`.

## How M65Connect works

- **Shells out to the official tools** for almost all hardware access: `m65`, `mega65_ftp`, `etherload`, `romdiff`, `bit2core` and `bit2mcs`.
- **Implements natively:**
  - the serial terminal and remote keyboard (2,000,000 baud; keys injected via `sffd3615`);
  - D81/D64 editing;
  - the ROM Configurator (bytes `$17FF8`–`$17FFF`);
  - PRG autoload;
  - FTDI listing (D2XX).
- **Connection choice:** it prefers Ethernet when available (`etherload -D`), otherwise USB/JTAG.

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
| Update | Update My MEGA65 (12-step guided workflow) | – | – | Implemented |
| Update | Core flashing from the PC | – | `mega65_ftp flash`: R2/R3 + Vivado only; refuses R4–R6 and Ethernet | Not offered: guided MEGAFLASH |
| Tools | Install official mega65-tools from the Filehost | Bundles binaries | – | Implemented |
| Connection | USB/JTAG serial connection | COM port, FTDI D2XX | `m65 -l COMx -s 2000000` (on Windows `-l` is required) | Hardware unverified |
| Connection | Device state readout (model, core, ROM) | Partial | Monitor `h`, `mffd3629 1`, `m20016` | Hardware unverified |
| Connection | Ethernet discovery and connection | `etherload -D` | `etherload -D`, `-i` | Hardware unverified |
| Transfer | PRG send and run | Yes (J, E) | `m65 -F [-4] [-r] f.prg`, `etherload -i ip [-4/-5] [-r] f.prg` | Hardware unverified |
| Transfer | SID send | Yes (J) | `m65 -F -4 -r f.sid` | Planned |
| Transfer | BIT load (volatile) | Yes (J) | `m65 -b` / `-q` | Planned |
| Transfer | HIC (HICKUP) load | Yes (J) | `m65 -k` | Planned |
| Transfer | ROM load (volatile) | Yes (J, E) | `m65 -R`, `etherload -R` | Planned |
| Transfer | BASIC program text transfer | Types text (J) | `m65 -T` / `-t` | Planned |
| Transfer | Drag-and-drop routing by file type | Yes | – | Partial (PRG) |
| Transfer | PRG autoload folder watch | Yes | – | Planned |
| SD card | Local SD card synchronisation (plan, back up, apply, verify) | – | – | Implemented |
| SD card | D81 contiguity check after copy | – | `mega65_ftp clusters` (remote) | Hardware unverified |
| SD card | Remote SD manager (dir, get, put, del, rename, mkdir, mount) | Yes (J, E) | `mega65_ftp -c …` | Partial (command console) |
| SD card | Local SD file manager and eject | Yes | `mega65_ftp -d` (raw) | Planned |
| Control | Reset | Yes (J, E) | `m65 -F`, `etherload -5` | Hardware unverified |
| Control | C64 / MEGA65 mode switching | Yes (J, E) | `m65 -F -4`, `etherload --c64mode/--m65mode` | Hardware unverified |
| Control | PAL / NTSC | Yes | `m65 -p/-n`, `etherload --pal/--ntsc` | Planned |
| Control | Screenshot capture | Yes (J) | `m65 -S<file>` | Hardware unverified |
| Control | Remote keyboard | Native | `m65 -T` (batch only) | Planned |
| Control | Monitor terminal | Native | `m65dbg` (not shipped for Windows) | Planned |
| Disk images | D81 create, list, add, extract, delete, rename | Native | None | Planned |
| ROM | ROM Configurator (speed, font, colours) | Native | None | Planned |
| ROM | ROM patch apply/create (.rdf) | `romdiff`, `bsdiff` | `romdiff` | Planned |
| Cores | COR/MCS build and verify | `bit2core`/`bit2mcs` (removed in 2.4) | `coretool.py` | Planned |
| Developer | Memory dump, unit-test runner, breakpoints | – | `m65 --memsave`, `-u`, `-B` | Planned |

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
- **Core flashing:**
  - `mega65_ftp` refuses R4–R6 ("Please use MEGAFLASH on the device instead").
  - R2/R3 need Vivado.
  - Ethernet flashing is refused.
