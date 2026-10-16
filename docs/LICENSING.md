# Licensing review

Not legal advice. This is a summary of the licence position as researched on 2026-10-16.

| Component | Licence | How MEGA65 GUI uses it |
|---|---|---|
| M65Connect | GPL-3.0 | Studied for behaviour only; **no code copied or translated** |
| mega65-tools (`m65`, `mega65_ftp`, `etherload`, `romdiff`, …) | GPL-3.0 (newer files GPL-3.0-or-later) | Downloaded at the user's request from the official Filehost and run as **separate processes** |
| SharpCompress | MIT | NuGet dependency |
| CommunityToolkit.Mvvm | MIT | NuGet dependency |
| System.IO.Ports | MIT | NuGet dependency |

## Conclusions

1. **Running the tools as separate programs** (process execution and parsing their output) is "mere aggregation" under the GPL FAQ. It places no GPL obligations on MEGA65 GUI's own code.
2. **The app does not redistribute the tools.** It downloads the official builds from the Filehost at runtime.
   - If a future installer *bundles* them, it must:
     - ship the GPL-3.0 text;
     - keep the copyright notices;
     - give the Corresponding Source for those exact binaries (link to mega65-tools at the commit in the archive name, e.g. `44c63d2`);
     - add no installer or EULA terms that restrict users' GPL rights over those tools.
3. **Copying or translating any M65Connect source would make MEGA65 GUI a GPL-3.0 derivative.** That includes its Xojo code, key-code tables, D81 code and SD-manager parsing.
   - Native features are written clean-room from facts, which are not copyrightable: register addresses, file formats, ROM offsets and CLI flags.
   - Planned work this applies to: the remote keyboard, D81 editing and the ROM Configurator.
4. **Do not redistribute:**
   - FTDI `ftd2xx.dll` (proprietary);
   - closed MEGA65/Commodore ROMs;
   - Vivado.
5. **Choosing a licence for MEGA65 GUI is still open** (the repository currently has none). GPL-3.0 would be the simplest choice if code reuse from either project is ever wanted later.
