#!/usr/bin/env bash
# Windows packages, buildable on Windows (Git Bash), Linux or macOS with the .NET 10 SDK and NSIS (makensis):
#   dist/Hearthpulse-Setup-<version>-win-x64.exe        setup wizard for the Windows app
#   dist/Hearthpulse-Portable-<version>-win-x64.zip     the Windows app as a zip: no setup, data kept next to it
#   dist/Hearthpulse-Preview-<version>-win-x64.zip      portable build of the cross-platform app, to try on Windows
set -euo pipefail

VERSION="${1:-0.9.0}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIST="$ROOT/dist"
WORK="$DIST/work/windows"
rm -rf "$WORK"
mkdir -p "$WORK" "$DIST"

echo "== Publishing the Windows app (win-x64, self-contained)"
dotnet publish "$ROOT/src/Hearthpulse.App/Hearthpulse.App.csproj" -c Release -r win-x64 --self-contained true \
  -p:EnableWindowsTargeting=true -p:Version="$VERSION" -o "$WORK/app" -nologo -v q

# The uninstaller removes exactly the files the setup installed (deepest folders last), never a whole folder.
python3 - "$WORK/app" "$WORK/uninstall-files.nsh" <<'PY'
import os, sys
root, out = sys.argv[1], sys.argv[2]
files, dirs = [], []
for base, subdirs, names in os.walk(root):
    rel = os.path.relpath(base, root)
    for n in sorted(names):
        path = n if rel == "." else os.path.join(rel, n)
        files.append(path.replace("/", "\\"))
    if rel != ".":
        dirs.append(rel.replace("/", "\\"))
with open(out, "w", encoding="utf-8") as f:
    for p in files:
        f.write(f'  Delete "$INSTDIR\\{p}"\n')
    for d in sorted(dirs, key=lambda d: d.count("\\"), reverse=True):
        f.write(f'  RMDir "$INSTDIR\\{d}"\n')
print(f"uninstall list: {len(files)} files, {len(dirs)} folders")
PY

echo "== Building the setup wizard"
SETUP="$DIST/Hearthpulse-Setup-$VERSION-win-x64.exe"
makensis -V2 -DVERSION="$VERSION" -DSOURCE_DIR="$WORK/app" -DOUT_FILE="$SETUP" \
  -DUNINSTALL_LIST="$WORK/uninstall-files.nsh" "$ROOT/installer/windows/hearthpulse.nsi"
echo "wrote $SETUP"

echo "== Packing the portable Windows app"
rm -rf "$WORK/portable"
mkdir -p "$WORK/portable"
cp -r "$WORK/app" "$WORK/portable/Hearthpulse"
# portable.txt makes Hearthpulse keep its settings and history in a "data" folder next to Hearthpulse.exe.
printf 'Portable Hearthpulse: settings and match history are kept in the "data" folder next to Hearthpulse.exe.\r\nIt tells you when a new version is out but does not update itself.\r\n' > "$WORK/portable/Hearthpulse/portable.txt"
PORTABLE="$DIST/Hearthpulse-Portable-$VERSION-win-x64.zip"
rm -f "$PORTABLE"
(cd "$WORK/portable" && zip -qr -9 "$PORTABLE" Hearthpulse)
echo "wrote $PORTABLE"

echo "== Publishing the cross-platform app preview (win-x64)"
dotnet publish "$ROOT/src/Hearthpulse.Desktop/Hearthpulse.Desktop.csproj" -c Release -r win-x64 --self-contained true \
  -p:Version="$VERSION" -o "$WORK/preview/Hearthpulse" -nologo -v q
cp "$ROOT/installer/windows/notice.txt" "$WORK/preview/Hearthpulse/README.txt"
PREVIEW="$DIST/Hearthpulse-Preview-$VERSION-win-x64.zip"
rm -f "$PREVIEW"
(cd "$WORK/preview" && zip -qr -9 "$PREVIEW" Hearthpulse)
echo "wrote $PREVIEW"
