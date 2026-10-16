# MEGA65 GUI

Created by **Chris Freeman**.

[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/cjf077)

**[Download the latest release](https://github.com/freemchr/mega65gui/releases/latest)**: unzip and run `Mega65Gui.exe` (Windows 10/11, 64-bit, nothing to install).

A Windows application for maintaining, updating and using a [MEGA65](https://mega65.org).

It brings together:
- the [MEGA65 Filehost](https://files.mega65.org);
- the official [mega65-tools](https://github.com/MEGA65/mega65-tools) (`m65`, `mega65_ftp`, `etherload`);
- a guided **Update My MEGA65** workflow;
- the working goal of feature parity with [M65Connect](https://github.com/MEGA65/m65connect).

> Status: **in development.** Tested on a real MEGA65 R6 over USB/JTAG: updates (including the closed ROM via your Filehost account), the SD card browser, PRG/SID/ROM/HICKUP loading, PAL/NTSC, screenshots, the terminal and remote keyboard. Ethernet and bitstream loading are not yet verified on hardware. The app's Features page shows each feature's status.

## Screenshots

| | |
|---|---|
| ![Update My MEGA65](docs/screenshots/update.png) | ![Filehost](docs/screenshots/filehost.png) |
| **Update My MEGA65:** the 12-step guided update | **Filehost:** browse, search and download |
| ![Device](docs/screenshots/device.png) | ![Features](docs/screenshots/features.png) |
| **Device:** connect, control and load files | **Features:** what is implemented and verified |

## What it does

### Update My MEGA65

1. Finds your model.
2. Picks the right official release from the Filehost.
3. Downloads and verifies it.
4. Compares it file by file with your SD card.
5. Backs up anything it will replace.
6. Writes only what you approve.
7. Stages the matching core for MEGAFLASH.
8. Verifies the result and saves a report.

### Filehost

- Search and browse all 520+ public files.
- Read details and version history.
- Download into a local cache that also works offline.
- Sign in with your own account to reach owner-only packages, such as releases that include the closed ROM.

### Device

- Connect over USB/JTAG or Ethernet.
- Reset, switch C64/MEGA65 mode, send PRGs (with drag-and-drop) and take screenshots.
- Run a `mega65_ftp` console for remote SD-card access.

### Tools

Installs the official mega65-tools builds from the Filehost.

## Safety

- **You approve every write.** Planning is read-only.
- **Nothing on your card is deleted.** Replaced files are backed up first.
- **FPGA flash is never written automatically.** The official tools cannot flash R4–R6 from a PC, so the app guides you through MEGAFLASH.
- **Owner-only Filehost downloads (the closed ROM) use your own Filehost account.** You sign in inside the app and the Filehost decides what you can download. Your password is never stored unless you ask, and then only DPAPI-encrypted.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Filehost integration notes](docs/FILEHOST-API.md)
- [M65Connect feature parity](docs/M65CONNECT-PARITY.md)
- [Licensing review](docs/LICENSING.md)
- [Roadmap](docs/ROADMAP.md)

## Build

Requires the .NET 10 SDK on Windows.

```
dotnet test Mega65Gui.slnx
dotnet run --project src/Mega65Gui.App
```

## Support

- **Report a problem or request a feature:** [GitHub Issues](https://github.com/freemchr/mega65gui/issues).
- **Source and documentation:** https://github.com/freemchr/mega65gui
- **Support the project:** if MEGA65 GUI helps you, you can [buy me a coffee](https://buymeacoffee.com/cjf077).

## Credits

- **The official [MEGA65 tools](https://github.com/MEGA65/mega65-tools)** (`m65`, `mega65_ftp`, `etherload`, `romdiff`) by the MEGA65 project, GPL-3.0. MEGA65 GUI downloads them from the Filehost and runs them as separate programs.
- **The [MEGA65 Filehost](https://files.mega65.org):** releases, ROMs and software.
- **[M65Connect](https://github.com/MEGA65/m65connect)'s documentation:** used as a feature reference. No M65Connect code is included.
- **Libraries:** SharpCompress, CommunityToolkit.Mvvm and .NET (MIT).

MEGA65 GUI is an independent project and is not an official MEGA65 product.

## Licence

MEGA65 GUI is free software under the [GNU General Public License v3.0](LICENSE). Copyright © 2026 Chris Freeman.
