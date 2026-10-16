# Architecture

```
Mega65Gui.slnx
├─ src/Mega65Gui.Core      net10.0 class library: all logic, no UI, unit tested
│   ├─ Filehost/            FilehostClient, CatalogueService (cache), DownloadManager, ReleaseInfo
│   ├─ Packages/            PackageExtractor (7z/zip, zip-slip safe), ReleasePackage (layout)
│   ├─ Sd/                  SdSyncPlanner, SdSyncExecutor, WindowsContiguityChecker
│   ├─ Tools/               ToolManager (installs official mega65-tools from the Filehost)
│   ├─ Device/              Mega65Commands (m65/mega65_ftp/etherload args), SerialMonitorProbe
│   ├─ Update/              UpdateEngine ("Update My MEGA65"), report model
│   └─ Features/            FeatureCatalog (parity tracker)
├─ src/Mega65Gui.App       net10.0-windows WPF (Fluent theme, MVVM via CommunityToolkit.Mvvm)
└─ tests/Mega65Gui.Core.Tests   xUnit; fixtures are trimmed real Filehost responses
```

## Data locations

Everything is stored under `%LOCALAPPDATA%\Mega65Gui\`:

| Folder | Contents |
|---|---|
| `cache\catalogue` | Catalogue JSON (6 h TTL), plus per-file version and detail JSON |
| `cache\downloads\<versionid>\` | The download and its `manifest.json` (size, SHA-256, URL, ETag) |
| `cache\extracted\` | Unpacked packages |
| `tools\<versionid>\` | Installed tool builds, with `installed.json` |
| `backups\<timestamp>\` | Copies of every SD-card file replaced, in the original folder structure |
| `reports\update-*.md` and `.json` | Reports from Update My MEGA65 runs |

## Update My MEGA65

| # | Step | How |
|---|---|---|
| 1 | Connection and capabilities | Device page connection. Serial gives read-back; Ethernet does not |
| 2 | Hardware and software versions | Serial monitor probe; otherwise the user picks the model. Each value is tagged with its source |
| 3 | Compatible releases | Official stable package for the model's target, highest active release, no ROM |
| 4 | Change summary | Running core compared with the release commit hash. **Approval gate** |
| 5 | Download and validate | Resume, size check, SHA-256 manifest, extraction, package layout check |
| 6 | Compare with the card | Size + SHA-256 per file, case-insensitive (FAT32) |
| 7 | Back up | Every file to be replaced is copied first |
| 8 | Apply | **Approval gate** with per-file checkboxes. Each file is written to a temp file, hash-verified, then moved into place. Never deletes. D81 contiguity is checked |
| 9 | Stage core | `.cor` for the exact model copied to the card root. **Approval gate** |
| 10 | Install core | Guided MEGAFLASH steps: official tools cannot flash R4–R6, and R2/R3 need Vivado |
| 11 | Verify | The card is re-compared and every written file must match |
| 12 | Report | Markdown + JSON, saved even after cancellation or failure |

## Safety rules

- **Nothing is written without an approval gate.** Planning is read-only.
- **Nothing is ever deleted from the card.** Files that exist only on the card are reported but left untouched.
- **No FPGA flash writes, and no destructive device commands, during an update.**
- **Unverified data is labelled.** Device values carry a `Provenance` (`ReadFromDevice` / `UserSupplied` / `NotKnown`), and features carry a `FeatureStatus`.
- **Owner-restricted Filehost content is only requested while you are signed in with your own account, and only after the server confirms your access.** It is never written to the metadata cache.

## Building

Requires the .NET 10 SDK. To build from WSL, use the Windows `dotnet.exe`.

```
dotnet build Mega65Gui.slnx
dotnet test Mega65Gui.slnx
dotnet run --project src/Mega65Gui.App
```

`Mega65Gui.exe --render-pages <folder>` renders every page to PNG and exits. It is used for UI smoke tests.
