# MEGA65 Filehost: integration notes

Researched 2026-10-16 against https://files.mega65.org. The Filehost has **no documented public API**. The endpoints below are the ones the site's own pages call. They may change without notice, so the client parses every response defensively and caches the last good copy.

Notation: **verified** means a live response was observed; *inferred* means it was read from site JavaScript or deduced.

## Conventions (verified)

- **Base URL:** `https://files.mega65.org/php/`. Requests are `POST` with a form-encoded body, as the site sends them. A plain `GET` also works for the parameterless lists.
- **Content type:** responses are JSON but labelled `Content-Type: text/html`, with `Cache-Control: no-store`. A fresh `PHPSESSID` cookie is set every time, but no session is needed for read access.
- **Response shapes are inconsistent.** Some endpoints return arrays and others return objects with numeric keys plus a `state` field. `state: "EMPTY"` means not found, restricted, or no permission.
- **Field formats vary:**
  - dates appear as `2021/06/02`, `17 Jul 2022` and `2026-10-16 01:58:16`;
  - `os` has a leading space;
  - `downloads` is space-padded;
  - `rating` may be `null`, `""`, `"-"` or `"5.0 (2)"`.
- **No checksums are published.** The app records size and SHA-256 itself in a `manifest.json` next to each download.
- **Terms:** `robots.txt` returns 404, and the Terms of Use contain no clause on automated access. The app sends a descriptive `User-Agent`, caches the catalogue for 6 hours, and fetches details only when they are needed.

## Endpoints used by MEGA65 GUI

| Endpoint | Params | Use | Shape |
|---|---|---|---|
| `readfilespublic.php` | none | Main catalogue: 520 public files, latest version only | `[{fileid, title, category, type, os, rating, downloads, search, published, sortdate, versionid, filename, size, location, author, …}]` |
| `readfiledetail.php` | `fileid` (UUID or `linkid`) | Detail pane | `[{state:"OK", title, description, linkid, category, type, os, rating, ratingcount, downloads, pal, ntsc, versionid, filename, size, version, location, coretext, romtext, published, author}]` or `[{"state":"EMPTY"}]` |
| `readversionlistpublic.php` | `fileid` | Version history | `[{fileid, version, versionid, filename, size, comment, published, active:"Y"/"N", location}]`, newest first |

### Other read endpoints (not used yet)

| Endpoint | What it returns |
|---|---|
| `readgallerypublic.php` | Thumbnails |
| `readmedias.php` | Screenshots and videos, with `objectid` and `origin=F` |
| `readcomments.php` | Comments and ratings |
| `readpagesearch.php` | Search across all object types, with `search` |
| `readtopten.php` | Top-ten lists |
| `readcategorychart.php` | Category counts |
| `readarticledetail.php` | Articles |
| `readcontent.php` | Terms and privacy pages |

### Write and auth endpoints (never called)

`login.php`, `logout.php`, `request.php`, `redeem.php`, `saveupdatedownload.php`, `savecomment.php`, `savescore*.php`, `savemediaupload.php`, `saveupdateview.php`.

### Downloads (verified)

- **URL:** resolve `location` against `/php/`, so `../files/m/x_AbC123.7z` becomes `https://files.mega65.org/files/m/x_AbC123.7z`.
- **Served statically by Apache,** with `Content-Length`, `ETag`, `Last-Modified` and `Accept-Ranges: bytes`. The client resumes partial downloads with `Range`.
- **No `Content-Disposition` header,** and the stored name has a random suffix. The file is saved under the API's `filename` instead.

### The un-parameterised version list

`readversionlistpublic.php` without `fileid` returns 982 rows for only 325 files and omits the release packages. It is **not** suitable for release discovery. Always pass `fileid`.

## Identifying official releases (verified)

- **What counts as a stable core package:**
  - `author == "MEGA65"`
  - `category == "Firmware"`
  - `type == "Release Package"`
  - the title contains "Release Package" and not "Development"
- **Target board:** taken from the parentheses in the title, e.g. `MEGA65 Core Release Package (mega65r6) no ROM`.
- **The Filehost `version` field is a per-file revision counter, not the MEGA65 release number.** The release number is parsed from the package file name:
  - `mega65r6-release-0.97.2-03b24c6-norom.7z` gives target, release, commit hash and a no-ROM flag;
  - legacy names look like `MEGA65-R3-Rel_0_9-norom.7z`;
  - the version comment ("Release 0.97 …") is the fallback.
- **The R6 package also contains the R5 (beta PCB) core.** There is no public R4 package.
- **Package layout** (0.97.2, verified by extracting it):
  - `<name>/sdcard-files/` holds the SD-card files (FREEZER.M65 etc.);
  - `<name>/*.cor` and `*.bit` are the cores;
  - `beta-pcb/` holds the R5 core;
  - `extra/HICKUP.M65` and `flasher/mflash.prg` are **not** copied to the card.

| Package | fileid |
|---|---|
| Release package (mega65r3) no ROM | `8d295d12-4422-4dc4-9553-64bf424e860e` |
| Release package (mega65r6) no ROM | `764642f7-83bc-4162-9911-99a0a6283d08` |
| mega65-tools Windows release | `06c55815-7826-4ad6-be0e-b8dc5e721b6d` |
| mega65-tools Windows development | `658322fd-e586-4b4f-a991-89470b269b4a` |

The release packages themselves are discovered dynamically. Only the two tools fileids are pinned.

## Restricted (owner-only) content and sign-in

- **What is restricted:** only the closed MEGA65 ROM, and packages that include it, need more than anonymous access (Filehost article "How to download closed ROM files").
- **How you get access:** a registered user redeems the paper ownership code shipped with the machine, once, on the website (`redeem.php`).
- **How restriction shows in the data:** restricted files are absent from `readfilespublic`. Anonymously, `readfiledetail` returns `EMPTY` for them. The public no-ROM package descriptions contain a "private link" (`https://files.mega65.org?id=<fileid>`) to the package with the ROM.

### Sign-in (as the website does it)

- **Request:** `POST login.php` with form fields `logindata[username]` and `logindata[password]`. This is jQuery's encoding of `{"logindata": {...}}`.
- **Response:** `{"state":"OK"}`, or `{"state":"NOTOK","message":…}` for bad credentials.
- **Session:** held in the `PHPSESSID` cookie. `logout.php` ends it.
- **Expiry:** session-aware endpoints may answer `RELOGIN`, `TIMEOUT` or `LOGINERROR`. The app treats these as "signed out".
- **No API token or OAuth exists.**

### What MEGA65 GUI does

- **You sign in with your own account,** in the app's Filehost page. The password goes only to `files.mega65.org` over HTTPS, and is never logged.
  - By default only the user name is remembered.
  - "Remember my password" stores it encrypted with Windows DPAPI, readable only by your Windows account on that PC.
  - Signing out deletes the saved password.
- **The server decides access.** A non-public file ID is only queried when you are signed in, and the app only lists its versions after `readfiledetail` returns `OK` for your session. `CatalogueService` throws otherwise.
- **Owner-only metadata is never written to the disk cache.** Downloaded packages are stored in your local download cache like any other download.
- **Update My MEGA65 → "Include the closed ROM":**
  1. Follows the private link from the public package.
  2. Uses the ROM package only if your account can see it.
  3. Otherwise falls back to the no-ROM package and says why.
- **The redeem flow is not automated.** Redeem your code on the website.
- **Not yet verified:** sign-in has been tested against a simulated Filehost, not a real account. The file-name format of the with-ROM packages is also unconfirmed. The parser accepts `…-<hash>.7z` or any suffix other than `-norom`.

## Open questions

- Would the Filehost operators welcome a desktop client? Should it call `saveupdatedownload.php` so download counts stay accurate? (Not called today; worth asking the operators.)
- Do signed-in sessions return additional rows from `readfilespublic`?
- Which endpoints back the Projects and Compatibility index pages?
