# Roadmap

## Release 0.1: first release (this milestone)

- [x] Filehost provider: catalogue, search, categories, details, version history, downloads, local cache, offline fallback
- [x] Release discovery from Filehost metadata and package names (not the revision counter)
- [x] Tool management: install official mega65-tools (release or dev) from the Filehost, or use an existing folder
- [x] Device connection: USB/JTAG serial probe, Ethernet discovery *(hardware-unverified)*
- [x] SD-card synchronisation: plan, approve, back up, apply, verify; D81 contiguity check
- [x] Update My MEGA65: the 12-step workflow with reports
- [x] Filehost sign-in with your own account; owner-only (closed ROM) packages when your account has access *(sign-in verified with a real account; ROM package access still to confirm)*
- [x] Basic control: reset, C64/MEGA65 mode, PRG send (drag-drop), screenshot, mega65_ftp console *(hardware-unverified)*

## Hardware validation (before tagging 0.1)

Each item needs a real MEGA65, and its findings go back into `FeatureCatalog`.

1. ~~Confirm the serial monitor responses on an R6~~ (done 2026-10-16). Still to confirm on an R3.
2. Confirm that m65 PRG send, reset, mode switch and screenshot work with `-l COMx -s 2000000` on Windows.
3. Confirm `etherload -D` and the mega65_ftp `-e` console on core 0.97.x.
4. Confirm the contiguity checker on a freshly formatted card and on a fragmented one.
5. Run the full Update My MEGA65 flow on a real card, then flash in MEGAFLASH.
6. ~~Sign in with a real Filehost account~~ and ~~check the with-ROM package layout~~ (done). A with-ROM update on a copy of a real card also passed (2026-10-16). Still to do: the same on the physical card, then MEGAFLASH.

## Release 0.2: M65Connect parity

- Remote SD manager UI: two-pane browser over `mega65_ftp` (dir, get, put, del, rename, mkdir, mount D81, clusters)
- SID, BIT, HIC and ROM (volatile) loading; PAL/NTSC; BASIC text transfer
- Drag-and-drop routing for every supported file type; PRG autoload folder watch
- Local SD-card file manager and safe eject

## Release 0.3: native tools (clean-room)

- D81 editor: create, list, add, extract, delete, rename, set title/ID; D64 read
- ROM Configurator (`$17FF8`–`$17FFF`) and `.rdf` patching via `romdiff`
- Serial monitor terminal and remote keyboard (2 Mbaud, `sffd3615` key injection)

## Release 0.4: developer and advanced

- Memory dump/inject, unit-test runner (`m65 -u`), breakpoints, HYPPO status
- `coretool` integration (build, verify, extract COR/MCS)
- R2/R3 PC-side flashing via `mega65_ftp flash` + Vivado, offered only after it is verified on hardware and behind explicit confirmation

## Out of scope

- Bypassing Filehost login or ownership checks
- Redistributing closed ROMs, FTDI drivers or Vivado
- Launching M65Connect as the app's interface
