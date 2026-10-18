[![Buy me a coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-cjf077-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/cjf077)

**If you enjoy MegaZXNext, you can [buy me a coffee](https://buymeacoffee.com/cjf077). Thank you!**

# MegaZXNext V1.0

The first full release of **MegaZXNext**, a ZX Next core for the [MEGA65](https://mega65.org/),
ported from [ZXNext_MiSTer](https://github.com/MiSTer-devel/ZXNext_MISTer) (ZX Next core
3.02.00, matches System/Next 24.11) with [MiSTer2MEGA65](https://github.com/sy2002/MiSTer2MEGA65)
V2.0.1.

The ZX Next is an enhanced ZX Spectrum: a Z80N CPU at 3.5, 7, 14 or 28 MHz, Layer 2 bitmap
graphics, tilemap, sprites, copper, DMA, three AY sound chips, DivMMC and Multiface, and 2 MB of
RAM. It runs NextZXOS and classic Spectrum 48K, 128K and +3 software.

> [!NOTE]
> MegaZXNext is not a ZX Spectrum Next and is not made or endorsed by SpecNext Ltd. The
> System/Next licence does not allow systems built from the ZX Next's VHDL to be called
> "ZX Spectrum Next"; "Sinclair" and "ZX Spectrum" are trademarks of their owners.

## Please test it

V1.0 is ready for wider testing. It has been tested on a MEGA65 **R6**. The **R4** and **R5**
builds are included, but nobody has tried them yet. Whatever board you have, please tell us how
it went: open an [issue](https://github.com/freemchr/MegaZXNext/issues) with your board
revision, the image you used and what you did, or report it on the MEGA65 Discord.

## Downloads

Download **`megazxnext_v1.0_r4_r5_r6.zip`** (below, under Assets). It holds the core for each
board and their SHA-256 checksums (`SHA256SUMS.txt`):

| Board | File in the zip | Status |
|---|---|---|
| **R6** | `megazxnext_r6.cor` | Tested |
| R5 | `megazxnext_r5.cor` | **Untested**: please report |
| R4 | `megazxnext_r4.cor` | **Untested**: please report |
| R3 / R3A | – | **Not supported** (see below) |

**Why there is no R3 / R3A version:** the ZX Next needs 2 MB of fast RAM. On the R4, R5 and R6
that RAM is the board's SDRAM chip, which the R3 and R3A do not have. Their other memory, the
HyperRAM, is too slow for a CPU running at up to 28 MHz, and the MEGA65 framework already uses it
for the HDMI picture. An R3 version with 1 MB (like an unexpanded ZX Next) held in the FPGA's own
memory may be possible later; it has not been built or tested.

## What's new since V0.2 alpha

* **The grey screen is fixed.** V0.2 sometimes stopped at a grey screen after a reset. The cause
  was the SDRAM read timing: at 112 MHz the read data was sampled just after the end of its valid
  window (hold slack -0.9 ns against the SDRAM datasheet), so reads could return wrong bytes,
  depending on the chip and its temperature. The SDRAM clock pin now runs 1.4 ns later (a
  separate PLL), and the SDRAM I/O timing is now constrained, so Vivado checks the margins on
  every build. With this build the grey screen has not come back on the R6.
* **New name: MegaZXNext.** Welcome screen, help, options menu and core name changed.
* **R4 and R5 builds** are released for the first time (untested).
* **Real ZX Next SD cards** can now be used directly (new, not yet tested on hardware): put the
  card from a real ZX Spectrum Next (FAT32) into the MEGA65 and the ZX Next boots from it. An
  image file still works as before and is the recommended way.

## Tested on an R6

* NextZXOS boots from the SD card image; its menus, 48K BASIC and the Browser work.
* Music plays; Next games (Warhawk Next, Nextoid) run with keyboard control.
* A tape game (Dizzy, .TAP) loads.
* A joystick in port 2 reads correctly as Kempston (all directions and fire).
* No more grey screen after resets.

**Not tested yet** (reports welcome): a real ZX Next SD card (direct mode), VGA output, F13 (CPU speed), the NMI keys F9/F11, +3 disk
images (.DSK), the **Reset ZX Next** menu item, R4, R5.

## Features

* The ZX Next's SD card is an **image file** on the MEGA65's SD card (`/zxnext/boot.vhd` or
  `/zxnext/boot.img`). A MiSTer `boot.vhd` works as is. The file may be fragmented (up to 512
  pieces). The ZX Next reads and writes it like its own SD card.
* Or a **real ZX Next SD card** (FAT32), used directly, as on a real Next.
* 2 MB of ZX Next RAM in the MEGA65's SDRAM.
* All CPU speeds: 3.5, 7, 14 and 28 MHz.
* The MEGA65 keyboard mapped to the ZX Next keyboard, including the Next's extra keys.
* HDMI (720p, 576p, 640x480, 720x480, 800x600) and VGA (standard or 15 kHz retro), HDMI CRT
  emulation, audio filters.
* Joysticks in both ports (can be swapped in the options menu).
* Real-time clock from the MEGA65's RTC.
* **Reset ZX Next** in the options menu.

## What you need

1. A MEGA65 **R6** (or an R4/R5 if you would like to test).
2. A ZX Next SD card, in one of two forms:
   * **An image file** (recommended): `/zxnext/boot.vhd` or `/zxnext/boot.img` on the MEGA65's
     SD card (FAT32), an image (a byte-for-byte copy) of a complete ZX Next SD card with the
     **System/Next distribution** in it. It lives next to your other MEGA65 files, and the ZX
     Next only ever touches that file.
   * **A real ZX Next SD card** (new, not yet tested on hardware): a FAT32 card prepared for the
     ZX Next, e.g. the card from a real ZX Spectrum Next. The core uses it directly when the
     card has no image on it but has `/TBBLUE.FW` in its root. **FAT16 cards are not
     supported** (the MEGA65 has to read the card at startup); copy the files onto a FAT32
     card.

### Which SD card is used

The core uses **one** SD card, chosen once when it starts:

1. The card in the **back slot**, if one is inserted.
2. Otherwise the card in the **bottom tray**.

On that card it looks first for `/zxnext/boot.vhd`, then `/zxnext/boot.img`. If neither is
there but `/TBBLUE.FW` is, it uses the card itself as a real ZX Next card. The other slot is not
used at all.

| Back slot | Bottom tray | What the ZX Next uses |
|---|---|---|
| empty | MEGA65 card with `/zxnext/boot.img` | the image on the bottom tray card |
| MEGA65 card with `/zxnext/boot.img` | anything | the image on the back slot card |
| real ZX Next card | MEGA65 card | the real ZX Next card (the tray card is not used) |
| empty | real ZX Next card | the real ZX Next card |

Don't swap or remove cards while the core is running: switch the MEGA65 off first.

Inside the image (or on the real card) the ZX Next expects the System/Next distribution, exactly as on a real ZX
Spectrum Next. At a minimum:

| Path inside the image / on the card | What it is |
|---|---|
| `/TBBLUE.FW` | the ZX Next firmware (boot menu, loads the ROMs) |
| `/machines/next/` | the ROMs (`enNextZX.rom`, `enNxtmmc.rom`, `48.rom`, `128.rom`, `plus3.rom`, ...), keymaps and `config.ini` |
| `/nextzxos/`, `/sys/`, `/dot/` | NextZXOS, its system files and dot commands |

Everything else (`/games`, `/apps`, `/demos`, `/docs`, ...) is optional but recommended.

> [!IMPORTANT]
> **No ROMs, firmware or operating system are provided here.** They are copyrighted by their
> owners (Sinclair ROMs © Amstrad / Sky plc, NextZXOS and NextBASIC © Garry Lancaster, TBBlue ©
> Victor Trucco and Fabio Belavenuto, System/Next © SpecNext Ltd). This release contains only
> the FPGA core. Get the files from their official sources:
> * the free **System/Next distribution** from https://www.specnext.com, or
> * the ready-made **`boot.vhd`** of the [MiSTer core](https://github.com/MiSTer-devel/ZXNext_MISTer)
>   (in its `releases` folder; an older distribution).

**Making an image with the current distribution:** prepare a ZX Next SD card the official way (a
FAT32 card with the distribution unzipped onto it), then copy the whole card into an image file
(`dd` on Linux/macOS, Win32 Disk Imager on Windows). Tested: System/Next 24.11 in a 2 GB image.
More in the [README](https://github.com/freemchr/MegaZXNext#what-goes-on-the-sd-card).

## Installation

1. Copy the ZX Next SD card image to the MEGA65's SD card as `/zxnext/boot.vhd` or
   `/zxnext/boot.img`, **or** put a real ZX Next SD card into the back slot. Remember: a card in
   the back slot is used before the card in the bottom tray.
2. Unzip the download and flash the `.cor` file for your board: hold **No Scroll** while switching on the MEGA65 and
   flash the core into a free slot (not slot 0), as described in the MEGA65 user guide.
3. Start the core. The welcome screen shows the most important keys; press **Space** and the
   ZX Next boots.

If the core finds neither an image nor a real ZX Next card it says so on screen: check which
card is in use (back slot first) and that the file is called `/zxnext/boot.vhd` or
`/zxnext/boot.img`, or that the real card is FAT32 with `/TBBLUE.FW` in its root.

## Keys

| MEGA65 key | ZX Next |
|---|---|
| Shift | Caps Shift |
| Ctrl, MEGA | Symbol Shift |
| Alt | Extend Mode |
| Caps Lock | Caps Lock |
| No Scroll | Graph |
| Tab / Clr Home | True Video / Inv Video |
| Esc, Run/Stop | Break |
| ← (left of 1) | Edit |
| Inst/Del | Delete |
| Cursor keys | Cursor keys |
| `: + - * / = @ £ ^` | the matching Symbol Shift combinations |
| F3 | 50 Hz / 60 Hz |
| F9 | Multiface NMI |
| F11 | DivMMC NMI (drive button) |
| F13 | Next CPU speed (3.5 → 7 → 14 → 28 MHz) |
| Help | Options menu |

**Reset:** a short press of the reset button (or **Reset ZX Next** in the options menu) is a
soft reset, back to the NextZXOS menu. A long press is a hard reset that reloads the firmware.

## Joysticks

MEGA65 port 1 is the ZX Next's left joystick, port 2 its right joystick (**Flip joystick ports**
in the options menu swaps them). What a joystick does is a ZX Next setting stored in the image,
not a core setting: depending on your image it may act as **Cursor** keys or **Sinclair** keys.
Most games want **Kempston**. To change it: press **Space** while the firmware boots, select the
machine, press **E** to edit its settings (the joysticks are on the second page), set **Right
Joy** (port 2) or **Left Joy** (port 1) to **Kempston 1**, and save.

## Known issues

* R4 and R5 are untested.
* Real ZX Next SD cards (direct mode) are untested on hardware; FAT16 cards are not supported.
* No mouse: the ZX Next's mouse is not connected to the MEGA65's mouse port yet.
* One joystick fire button only.
* The ZX Next firmware's boot screen says "MiSTer Next" (the firmware picks the text by the
  machine ID the core reports).
* The options menu settings are not saved: the ZX Next owns the SD card while it runs.
* There is no HDMI zoom-in option.

## Credits

ZX Spectrum Next core by Alvin Albrecht, Victor Trucco, Fabio Belavenuto et al.; MiSTer port by
Sorgelig and the MiSTer team; MiSTer2MEGA65 by sy2002 and MJoergen; SDRAM I/O scheme after
MJoergen's MEGA65 SDRAM controller. MEGA65 port by Chris Freeman. See
[AUTHORS](https://github.com/freemchr/MegaZXNext/blob/master/AUTHORS).

Licensed under the GNU GPL v3. Full changelog: [VERSIONS.md](https://github.com/freemchr/MegaZXNext/blob/master/VERSIONS.md).
