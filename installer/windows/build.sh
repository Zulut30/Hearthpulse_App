#!/usr/bin/env bash
# Windows packages, buildable on Windows (Git Bash), Linux or macOS with the .NET 10 SDK and NSIS (makensis):
#   dist/Hearthlens-Setup-<version>-win-x64.exe        setup wizard for the Windows app
#   dist/Hearthlens-Preview-<version>-win-x64.zip      portable build of the cross-platform app, to try on Windows
set -euo pipefail

VERSION="${1:-0.7.8}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DIST="$ROOT/dist"
WORK="$DIST/work/windows"
rm -rf "$WORK"
mkdir -p "$WORK" "$DIST"

echo "== Publishing the Windows app (win-x64, self-contained)"
dotnet publish "$ROOT/src/Hearthlens.App/Hearthlens.App.csproj" -c Release -r win-x64 --self-contained true \
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
SETUP="$DIST/Hearthlens-Setup-$VERSION-win-x64.exe"
makensis -V2 -DVERSION="$VERSION" -DSOURCE_DIR="$WORK/app" -DOUT_FILE="$SETUP" \
  -DUNINSTALL_LIST="$WORK/uninstall-files.nsh" "$ROOT/installer/windows/hearthlens.nsi"
echo "wrote $SETUP"

echo "== Publishing the cross-platform app preview (win-x64)"
dotnet publish "$ROOT/src/Hearthlens.Desktop/Hearthlens.Desktop.csproj" -c Release -r win-x64 --self-contained true \
  -p:Version="$VERSION" -o "$WORK/preview/Hearthlens" -nologo -v q
cp "$ROOT/installer/windows/notice.txt" "$WORK/preview/Hearthlens/README.txt"
PREVIEW="$DIST/Hearthlens-Preview-$VERSION-win-x64.zip"
rm -f "$PREVIEW"
(cd "$WORK/preview" && zip -qr -9 "$PREVIEW" Hearthlens)
echo "wrote $PREVIEW"
