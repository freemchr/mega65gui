# Releasing

Versions go up by minor steps: 1.2, 1.3, 1.4 … even for a small fix. The tag is `v1.2` and the zip `Mega65Gui-1.2-win-x64.zip`.

## 1. Build the package

```
scripts/release.sh 1.2
```

This sets the version, builds, runs the tests, publishes one self-contained compressed `Mega65Gui.exe`, zips it with `README.txt` (from `packaging/README.txt`) and `LICENSE.txt`, checks the exe's version, writes the checksum and commits "Version 1.2". Nothing leaves the PC.

## 2. Check it on Windows

Run `publish\Mega65Gui-1.2-win-x64\Mega65Gui.exe`. Most users start with **no MEGA65 connected**, so check that first:

- [ ] **No MEGA65, card in a PC card reader.** Update My MEGA65 opens on "In a PC card reader". With Model on Auto, Run says to choose the model and never asks for a COM port. With a model chosen, the update starts.
- [ ] **A COM port that is not a MEGA65** (Bluetooth, or a USB serial adapter). The port lists label it, e.g. "COM3 – Bluetooth (not a MEGA65)", and do not pick it by themselves.
- [ ] **SD Card Browser with nothing connected** shows "No MEGA65 connected" with Device page and SD Card Sync buttons.
- [ ] **Report a problem** (sidebar) shows the details and opens a filled-in GitHub issue. Close it without posting.
- [ ] **About → Check now** reports the newest version, or that this one is the newest.

Then with the MEGA65 on and its USB cable in:

- [ ] The port lists show "COMx – MEGA65 USB/JTAG" and pick it. Connect reads the model, core and ROM.
- [ ] Whatever the release changed, tried on the hardware.

## 3. Write the notes and publish

Edit `publish/release-notes-1.2.md` (what is new, what is fixed, anything not yet verified on hardware), then:

```
scripts/release.sh 1.2 --publish
```

This rebuilds, pushes `main`, creates the GitHub release as the latest with the zip, and checks that GitHub's copy has the same SHA-256. The README's download link always points at the latest release, and the app's start-up check tells users about it.

## 4. Tell people

The start-up check only exists from 1.3 on (there was no 1.2). Anyone on 1.1.1 or older never hears about a new version from the app, so announce each release yourself: reply to whoever reported the problem it fixes, update the Filehost page, and post on the forums. The 1.3 notes say that later versions will announce themselves.
