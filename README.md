# MEGA65 GUI

Created by **Chris Freeman**.

A Windows application for maintaining, updating and using a [MEGA65](https://mega65.org).

It brings together:
- the [MEGA65 Filehost](https://files.mega65.org);
- the official [mega65-tools](https://github.com/MEGA65/mega65-tools) (`m65`, `mega65_ftp`, `etherload`);
- a guided **Update My MEGA65** workflow;
- the working goal of feature parity with [M65Connect](https://github.com/MEGA65/m65connect).

> Status: **0.1 in development.** The Filehost, download, SD-sync and update logic is implemented and tested, including a live end-to-end run against the Filehost. Device features are built on documented tool flags but are **not yet verified on hardware**. The app's Features page shows each feature's status.

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
