#!/usr/bin/env bash
# Builds a MEGA65 GUI release the same way every time.
#
#   scripts/release.sh 1.2             build, test and package; commit the version change
#   scripts/release.sh 1.2 --publish   then push main and create the GitHub release (latest)
#
# Versions go up by minor steps: 1.2, 1.3, 1.4 … Run from WSL or Git Bash on Windows with the
# .NET SDK installed (dotnet.exe from WSL, dotnet elsewhere). The release notes are written to
# publish/release-notes-<version>.md: edit them before publishing (see docs/RELEASING.md).
set -euo pipefail

VERSION="${1:-}"
PUBLISH="${2:-}"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+$ ]]; then
  echo "Usage: scripts/release.sh <major.minor> [--publish]   e.g. scripts/release.sh 1.2" >&2
  exit 1
fi
if [[ -n "$PUBLISH" && "$PUBLISH" != "--publish" ]]; then
  echo "Unknown option: $PUBLISH" >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
DOTNET="$(command -v dotnet.exe || command -v dotnet)"
CSPROJ=src/Mega65Gui.App/Mega65Gui.App.csproj
NAME="Mega65Gui-$VERSION-win-x64"
OUT="publish/$NAME"
ZIP="publish/$NAME.zip"
NOTES="publish/release-notes-$VERSION.md"

[[ "$(git branch --show-current)" == main ]] || { echo "Release from main." >&2; exit 1; }
[[ -z "$(git status --porcelain)" ]] || { echo "Commit or stash your changes first." >&2; exit 1; }
if git rev-parse -q --verify "refs/tags/v$VERSION" >/dev/null; then echo "v$VERSION is already tagged." >&2; exit 1; fi

echo "== Version $VERSION"
sed -i -E "s#<Version>[^<]+</Version>#<Version>$VERSION</Version>#" "$CSPROJ"

echo "== Build and test"
"$DOTNET" build Mega65Gui.slnx -c Release -v q
"$DOTNET" test Mega65Gui.slnx -c Release --no-build -v q

echo "== Publish (single self-contained, compressed exe)"
rm -rf "$OUT" "$ZIP"
"$DOTNET" publish "$CSPROJ" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT" -v q
rm -f "$OUT"/*.pdb
cp LICENSE "$OUT/LICENSE.txt"
sed "s/{VERSION}/$VERSION/" packaging/README.txt > "$OUT/README.txt"

echo "== Zip"
# The three files at the top level of the zip, as in every release so far.
python3 -I - "$OUT" "$ZIP" <<'PY'
import sys, zipfile, os
src, dst = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for f in ["Mega65Gui.exe", "README.txt", "LICENSE.txt"]:
        z.write(os.path.join(src, f), f)
PY
SHA="$(sha256sum "$ZIP" | cut -d' ' -f1)"

if command -v powershell.exe >/dev/null; then
  EXE_VERSION="$(powershell.exe -NoProfile -Command "(Get-Item '$(wslpath -w "$OUT/Mega65Gui.exe")').VersionInfo.ProductVersion" | tr -d '\r')"
  [[ "$EXE_VERSION" == "$VERSION"* ]] || { echo "The exe says version $EXE_VERSION, expected $VERSION." >&2; exit 1; }
  echo "Exe version: $EXE_VERSION"
fi

if [[ ! -f "$NOTES" ]]; then
  cat > "$NOTES" <<EOF
MEGA65 GUI $VERSION: <one line on what is new>

## Download
**$NAME.zip**: unzip and run \`Mega65Gui.exe\`. Nothing to install; needs Windows 10/11 64-bit.
Windows SmartScreen may warn about an unknown publisher because the app is not code-signed yet: choose **More info → Run anyway**.

SHA-256: \`$SHA\`

## New

## Fixed

Please report problems and ideas with **Report a problem** in the app, or in [Issues](https://github.com/freemchr/mega65gui/issues). If it helps you, you can [buy me a coffee](https://buymeacoffee.com/cjf077).

Free software under the GNU GPL v3. Not an official MEGA65 product.
EOF
else
  # Notes written earlier: keep them, but put this build's checksum in.
  sed -i -E "s#^SHA-256: \`[0-9a-f]*\`#SHA-256: \`$SHA\`#" "$NOTES"
fi

git add "$CSPROJ"
git diff --cached --quiet || git commit -q -m "Version $VERSION"

echo
echo "Package:  $ZIP"
echo "SHA-256:  $SHA"
echo "Notes:    $NOTES"

if [[ "$PUBLISH" == "--publish" ]]; then
  if grep -q '<one line on what is new>' "$NOTES"; then echo "Write the release notes first: $NOTES" >&2; exit 1; fi
  git push -q origin main
  gh release create "v$VERSION" "$ZIP" --target main --title "MEGA65 GUI $VERSION" --notes-file "$NOTES" --latest
  DIGEST="$(gh api "repos/freemchr/mega65gui/releases/tags/v$VERSION" --jq '.assets[0].digest')"
  [[ "$DIGEST" == "sha256:$SHA" ]] || { echo "GitHub has a different file: $DIGEST" >&2; exit 1; }
  echo "Published: https://github.com/freemchr/mega65gui/releases/tag/v$VERSION"
else
  echo
  echo "Next: run the checks in docs/RELEASING.md on Windows, edit the notes, then:"
  echo "  scripts/release.sh $VERSION --publish"
fi
