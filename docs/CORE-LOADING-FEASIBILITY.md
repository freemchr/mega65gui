# Loading MEGA65 cores beyond the eight flash slots: feasibility

**Question.** Could the MEGA65 start an FPGA core stored on its internal SD card, without Paul Gardner-Stephen's external Pico-based loader and without installing it permanently in a flash slot?

**Short answer.**

| Approach | Verdict |
|---|---|
| (a) Start a core that is already in a flash slot | **Works today.** No writes. `m65 -Z` from a PC, or MEGAFLASH on the machine. |
| (b) Write a core temporarily to a scratch slot, then start it | **Feasible with existing mechanisms**, but each swap writes flash (minutes) and needs the hypervisor or DIP switch 3. A tool to automate it does not exist yet. |
| (c) Stream a bitstream from the internal SD card straight into the FPGA | **Not feasible on the MEGA65 alone.** Full reconfiguration erases the logic that reads the SD card, and nothing else on R4–R6 boards can drive the FPGA's configuration port. |

The Pico-based loader solves (c) by being that "something else": external hardware on the JTAG header, with its own SD card.

Research date: 2026-10-19. Nothing in this study changes the factory core, the flash layout or the hardware configuration.

## Sources

| Source | Version |
|---|---|
| [MEGA65/mega65-core](https://github.com/MEGA65/mega65-core) | `development` at bc7a3ec (2026-09-23); `release-0.97.2` checked where it matters |
| [MEGA65/mega65-tools](https://github.com/MEGA65/mega65-tools) | HEAD, 2026-10 |
| [MEGA65/mega65-expansion-board-integrated-jtag](https://github.com/MEGA65/mega65-expansion-board-integrated-jtag) | f23abb9 (2026-10-11): the Pico loader |
| [MEGA65/mega65-user-guide](https://github.com/MEGA65/mega65-user-guide) | `appendix-target-specific.tex`, `cores-and-flashing.tex` |
| [MEGA65/mega65-r2-max10](https://github.com/MEGA65/mega65-r2-max10) | `top.vhd` |
| AMD UG908, UG909 | Vivado programming and Dynamic Function eXchange (partial reconfiguration) |
| Infineon S25FL512S data sheet | Flash endurance |

- Line numbers are approximate and refer to the `development` branch.
- Anything marked **Unverified** could not be confirmed from a primary source.

## 1. How a MEGA65 configures its FPGA

**FPGA and configuration mode**
- R3, R4, R5 and R6 use a Xilinx Artix-7 XC7A200T.
  - `bit2core.c` maps `mega65r1|r3|r4|r5|r6` to part `7a200tfbg484`.
  - `m65.c` `load_bitstream()` maps IDCODE `x3636093` to `xc7a200t`.
- They boot in Master SPI x4 mode from the on-board QSPI flash. The `.xdc` files set:
  - `CONFIG_MODE SPIx4`;
  - `SPI_32BIT_ADDR YES`;
  - `BITSTREAM.GENERAL.COMPRESS TRUE`.

  See `src/vhdl/mega65r3.xdc` L470–476, `mega65r4.xdc` L612–617 and `mega65r5.xdc` L611–617.
- Bitstreams are compressed, so their size varies. A core must fit in 8 MB minus the 4 KB header (`bit2core.c check_bitstream_file`).
- The mega65-core Makefile builds the `mega65r6` target for R5 boards too ("there is not really difference", ~L1337).

**Slots**
- The flash is divided into slots of 8 MB on R1 and R3–R6, and 4 MB on R2, MEGAphone, Nexys and A100T. Sources:
  - the `mega65_target` table in `src/utilities/megaflash/megaflash.scr` L120–145;
  - `mf_utility.c` L45–50;
  - `mf_hlflash.c` L190 (`slot_count = flash_size_MB / slot_mb`).
- Per the user guide (`appendix-target-specific.tex`):
  - the R3 has 4 slots (0–3);
  - the R3A has a larger flash with 8 slots (0–7);
  - R4–R6 have 8 slots.
- The exact R4–R6 flash part is **unverified**. The testbench models an S25FL512S (`test_qspi.vhdl` L84).

**Slot 0 and fallback**
- Slot 0 holds the factory ("golden") core that the FPGA loads at power-on.
- The user guide (`cores-and-flashing.tex` ~L280) describes a fallback. If a core fails to configure, the FPGA "will automatically search through the flash memory until it finds a valid core. If it gets to the end of the flash memory, it will start the MEGA65 Factory Core from slot 0 again."
- This is Xilinx MultiBoot fallback. A bad image in another slot costs time but does not brick the machine.
- Only an official MEGA65 core can be flashed to slot 0 (MEGA+comma; `confirm_slot0_flash()` checks slot 1).

**Reconfiguring at run time**

`src/vhdl/reconfig.vhdl` instantiates the 7-series `ICAPE2` primitive (L91) and feeds it this sequence (L32–50):

| Word | Meaning |
|---|---|
| dummy words | Padding |
| `AA995566` | Sync word |
| NOOPs | |
| `30020001` | Write to WBSTAR |
| warm-boot address | Where the next configuration starts |
| `30008001` | Write to CMD |
| `0000000F` | IPROG |

- IPROG makes the FPGA reload itself from flash at that address.
- The registers live in `src/vhdl/sdcardio.vhdl`:

  | Register | Use |
  |---|---|
  | `$D6C4` | Select an ICAPE2 register to read back (BOOTSTS, IDCODE, WBSTAR…) |
  | `$D6C8`–`$D6CB` | Boot address. The flash address shifted right by 8, because of 32-bit SPI addressing |
  | `$D6CF` | Write `$42` to trigger reconfiguration |

- No hypervisor-mode check was found on the reconfiguration registers.
- **Flash program and erase are gated:** `hypervisor_mode = '1' or dipsw2 = '1'` (DIP switch 3).
  - Development: `qspi_flash_s25flxxxs.vhdl` L315–338.
  - 0.97.2: `sdcardio.vhdl` L3098–3279.
- MEGAFLASH starts a slot at `slot × slot_size + 4096`, skipping the `.cor` header (`megaflash.c` L560, L725; `mf_utility.c mfut_reconfig_fpga` ~L66–97).

**Boot sequence**
1. HYPPO (`src/hyppo/main.asm`) runs MEGAFLASH from BRAM on first boot (`launch_flash_menu`, ~L626–653, L2766).
2. With no key held, MEGAFLASH picks a slot (`megaflash.c` ~L640–735; `scan_core_information()` L215–325):
   - first, a slot flagged for the inserted cartridge;
   - otherwise the default-flagged slot;
   - otherwise slot 1 (slot 2 if DIP switch 4 is on).
3. MEGAFLASH reconfigures to that slot.
4. NO SCROLL opens the interactive menu instead.

**No SD-to-FPGA path exists**
- Neither HYPPO nor MEGAFLASH has code that loads a bitstream from the SD card into the FPGA.
- The SD card is used only to read a `.cor` into Attic RAM and write it into flash:
  - `mf_hlflash.c mfhf_load_core` L264;
  - `mfhf_flash_core` L719, which verifies and skips identical sectors, then erases and programs the rest.
- Release notes for 0.96, 0.97.0 and 0.97.2 mention no core loader.

**JTAG-loaded cores cannot launch other cores**

After a core is loaded over JTAG, BOOTSTS reads `$FF`. MEGAFLASH then refuses to launch other cores: "You have started this bitstream via JTAG, launching other cores has been disabled" (L766–775).

## 2. The `.cor` format and JTAG loading

The `.cor` header is 4096 bytes (`bit2core.c` `header_info`, L183–215; read the same way by `mf_selectcore.h` L40–54):

| Offset | Field |
|---|---|
| `0x00` | `"MEGA65BITSTREAM0"` |
| `0x10` | Core name (32 bytes) |
| `0x30` | Version (32 bytes) |
| `0x50` | Target name (32 bytes) |
| `0x70` | Model ID |
| `0x71` | Banner present |
| `0x72` | Embedded file count |
| `0x73` | Embedded file offset (u32) |
| `0x7B` | Boot capabilities |
| `0x7C` | Boot flags |
| `0x7D` | Install flags |
| `0x80` | Core size |
| `0x84` | CRC32 (computed with this field set to `F0F0F0F0`) |
| `0xF0` | Erase list |

- **The body is the Vivado `.bit` file copied verbatim, `.bit` header included.** `build_core_file()` (~L605–660) does `memcpy(&core_file[4096], bitstream_data, bit_size)`, with no stripping or bit-swapping.
- Embedded files, if any, follow the `.bit`.
- The official `coretool.py --extract` takes the bytes after the header, up to the embedded files, as the `.bit` (L919, L1458–1530).
- `m65 -q` (`--bitonly`) loads the file through `fpgajtag`. `read_inputfile()` (`fpgajtag/util.c` L626–720) accepts:
  - an ELF;
  - gzip;
  - a `.bit`, skipping its header up to field `e`;
  - a raw file, sent as-is.
- It has **no `.cor` support**. A `.cor` would be sent raw with 4 KB of header in front, which is **unverified** over JTAG.
- `m65 -Z <slot>` (L344, ~L2171–2195) writes `$D6C8`–`$D6CB` and `$D6CF=$42` through the serial monitor at `slot × 0x800000`. It therefore needs the MEGA65 core's serial monitor, so it cannot be used while an alternate core, or a JTAG-loaded core, is running.

**What MEGA65 GUI does with this**
- To run a `.cor` over JTAG, the Core Manager extracts the `.bit` exactly as `coretool --extract` does.
- It then checks that the bytes parse as a complete Vivado `.bit` (header, sync word, length) before passing them to `m65 -q`.
- A core that does not contain a valid `.bit` is refused. The app never assumes that removing the header gives a usable file.

## 3. The Pico-based loader

**Repository:** [MEGA65/mega65-expansion-board-integrated-jtag](https://github.com/MEGA65/mega65-expansion-board-integrated-jtag). Created 2026-07-16; status "v0.1 experimental"; pins and protocol subject to change.

**Hardware** (`README.md`, `PINOUT.md`, `include/config.h`)
- A Raspberry Pi Pico (RP2040; Pico W by default; RP2350 code present).
- It is wired to the TE0790/XMOD JTAG socket (J2):

  | Pico pins | Function |
  |---|---|
  | GP0/1 | UART to the MEGA65, 2 Mbit/s |
  | GP2–5 | **Its own** microSD card over SPI |
  | GP6–9 | TCK/TMS/TDI/TDO, through a bus switch |
  | GP10 | JTAG hijack control |
  | GP11 | Physical write-enable switch |

- It bit-bangs JTAG and assumes a single-device chain.

**Protocol:** AT-style text commands over USB CDC and the UART.
- Commands: `AT+CORELIST`, `AT+COREINFO`, `AT+JTAGLOAD=file|n`, `AT+JTAGSTREAM=len idcode`, `AT+JTAGID?`, `AT+JTAGSTATUS?`, `AT+FILEWRITE`, `AT+FETCH=url`.
- It also has HTTP endpoints, and a host tool, `tools/m65j.py`.
- It accepts:
  - `.bit` files;
  - `.cor` files (magic checked, `.bit` expected at `0x1000`; `src/core_file.c` L205–231);
  - its own `.m65j` format: `"M65J"`, length, expected IDCODE, payload at +16.
- From the MEGA65 side over the UART, cores can be listed and launched but not written or streamed. That needs USB and the physical write-enable.

**Why it is external:** a write-up of Paul's video (theoasisbbs.com, 2026-07-17; secondary source) quotes the reason: "during FPGA reconfiguration, the MEGA65 has no active core to access its onboard SD card". It reports about 13 cores, each starting in under 10 seconds, without rewriting flash.

## 4. Why a software-only SD loader does not work

1. **Full reconfiguration erases user logic.**
   - Configuring the FPGA, whether by power-on, PROG_B, IPROG or JTAG, replaces every flip-flop and LUT. The SD controller, the CPU and HYPPO go with it.
   - So an in-FPGA loader cannot stream the rest of a bitstream into the configuration port while the port is being replaced.
   - UG470's exact wording could not be retrieved. This is standard 7-series behaviour, consistent with the Pico project's own explanation.
2. **The only configuration sources are the flash and JTAG.**
   - The mode pins select Master SPI, so the FPGA itself reads the flash. Slave Serial and SelectMAP would need an external master and are not wired.
   - On R4–R6 the MAX10 helper FPGA "has been completely removed from the board" (user guide, `appendix-target-specific.tex` L52).
   - The keyboard CPLD and J21 are not on the configuration path. This is inferred, not checked against schematics: **unverified**.
3. **The R2/R3 MAX10 is no help.**
   - On R2 the MAX10 sits in the JTAG path and sees PROG_B, INIT and DONE (`mega65-r2-max10 top.vhd` L60–64, L221–223).
   - It has no SD access and far too little storage for a bitstream.
   - Its R3 wiring is **unverified**.
4. **ICAP cannot rescue a full bitstream.**
   - ICAPE2 is how a running design triggers IPROG or reads configuration registers.
   - Feeding it a full-device bitstream would overwrite the logic driving ICAP mid-stream, so ICAP cannot load one. (Whether the silicon accepts it at all is **unverified**, and moot.)
5. **Partial reconfiguration (DFX) is possible in principle, but not for existing cores.**
   - UG909 supports Artix-7 parts other than the 7A12T/25T.
   - A static "shell" could keep the SD controller, clocks (MMCM/PLL/BUFG) and I/O, and load *partial* bitstreams through ICAP into reconfigurable regions, which contain only CLBs, BRAM and DSP.
   - But every core would have to be rebuilt against that shell. None of today's `.cor` files, MEGA65 or MiSTer2MEGA65, could be used.
   - Each port would be a large FPGA project. DFX licensing was not checked.

## 5. Options compared

| | (a) Start a slot | (b) Scratch slot + IPROG | (c) Pico/JTAG loader | DFX shell |
|---|---|---|---|---|
| Extra hardware | None | None | Pico board on the JTAG header | None |
| Writes flash | No | Yes, about 8 MB per swap | No | No |
| Time to switch | Seconds | Minutes (erase and program) | About 10 s (reported) | Seconds (estimated) |
| Number of cores | 8 (R3: 4) | Unlimited on SD | Unlimited on its SD | Unlimited, but only ported ones |
| Works with existing `.cor` files | Yes | Yes | Yes | **No** |
| Needs hypervisor or DIP 3 | No | Yes | No | Design-dependent |
| Risk | None | Flash wear (low); a bad slot falls back to slot 0 | Experimental hardware and protocol | Large engineering effort |
| Status | Implemented in MEGA65 GUI | Manual via MEGAFLASH | v0.1 experimental | Not started anywhere |

**Flash wear for (b)**
- The S25FL512S is rated for 100,000 program/erase cycles per sector, with 20-year retention.
- An 8 MB slot is 32 uniform 256 KB sectors, and MEGAFLASH skips sectors that are already identical.
- Swapping cores even daily is negligible wear.
- That R4–R6 use exactly this part is **unverified**.

**Existing projects**
- No MEGA65 project was found that uses a scratch slot.
- MiSTer2MEGA65 has no ICAPE2, WBSTAR or IPROG code. Its "reconfig" hits are MMCM clock reconfiguration. So M2M cores cannot jump back to another slot; a power cycle is needed (inferred).

## 6. Engineering complexity and risks

**Option (b)**
- **Effort:** low to moderate. The flashing code already exists in MEGAFLASH (`mf_hlflash.c`). What is missing is an entry point that says "flash `X.COR` to slot 7, then start slot 7" without the interactive menu.
- **Risks:**
  - writing the wrong slot (mitigated by never touching slots 0 and 1);
  - power loss mid-write (mitigated by fallback to slot 0);
  - flash wear (negligible);
  - the user experience of a multi-minute wait.

**Option (c) with the Pico**
- **Effort for MEGA65 GUI:** low, once the protocol is stable.
- **Risks:**
  - the protocol is explicitly unstable (v0.1);
  - the hardware is not generally available;
  - the JTAG-hijack switch must not fight a PC adapter on the same header.

**DFX shell**
- **Effort:** very high. A new platform; every core ported.
- **Risks:** timing closure, resource limits, and incompatibility with all existing cores.

## 7. Recommended proof of concept

The smallest reversible steps, in order. None needs firmware changes.

1. **Prove (a) from the PC.** With the MEGA65 core running and connected over USB/JTAG, start slot 2 from the Core Manager's Flash slots tab (`m65 -Z 2`).
   - Nothing is written.
   - Power-cycling returns to the normal boot.
   - Already supported.
2. **Prove (b) by hand.** In MEGAFLASH (NO SCROLL at power-on), flash a known-good `.cor` into slot 7 and confirm it. Then start slot 7 from the Core Manager.
   - To revert, erase slot 7 in MEGAFLASH.
   - The Core Manager already stages the `.cor` on the card and shows these steps.
3. **Gateway to automating (b).** With DIP switch 3 ON, run the standalone `mflash.prg` to show the write can be triggered without the boot menu.
   - **Upstream change to propose:** a non-interactive MEGAFLASH entry, for example a `FLASHTO7.CFG` file or a HYPPO trap, that:
     - writes a named `.cor` to a designated scratch slot;
     - refuses slots 0 and 1;
     - verifies it;
     - then IPROGs to it.
   - MEGA65 GUI would then only need to stage the file and press reset.
4. **(c) only through external hardware.** Extract the `.bit` with `coretool -X` (the Core Manager does the same internally) and load it from a PC with `m65 -q`. This is the Core Manager's Run now.
   - With the expansion board, the same file can be loaded with `AT+JTAGLOAD` / `m65j.py push`.

## 8. How the Core Manager is built for these options

- `ICoreLoader` (`src/Mega65Gui.Core/Cores/CoreLoaders.cs`) is the seam. `CoreLoaderKind` names three backends:

  | Kind | Backend | Status |
  |---|---|---|
  | `PcJtag` | `m65 -q` | Implemented |
  | `ExternalLoader` | The Pico board | Not implemented |
  | `OnDevice` | A future native loader | Not implemented, and not known to be possible |

- Checks, wording and the UI go through the interface, so a new loader needs only a new class.
- **The Pico protocol is not implemented.** It is v0.1 and explicitly subject to change. An `ExternalLoader` backend should be written against a tagged release of that repository, after testing with the hardware. It would:
  - list cores (`AT+CORELIST`);
  - load one by index or name (`AT+JTAGLOAD`);
  - read back status (`AT+JTAGSTATUS?`).
- No production code depends on any speculative FPGA change.
