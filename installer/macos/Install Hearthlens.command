#!/bin/bash
# Double-click to install Hearthlens: copies it to Applications and puts a shortcut on your Desktop.
# (If macOS says it cannot be opened: right-click this file, choose Open, then Open again.)
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
APP="Hearthlens.app"

DEST=/Applications
if [ ! -w "$DEST" ]; then
  DEST="$HOME/Applications"
  mkdir -p "$DEST"
fi

echo "Installing Hearthlens into $DEST…"
osascript -e 'quit app "Hearthlens"' >/dev/null 2>&1 || true
pkill -x Hearthlens >/dev/null 2>&1 || true
rm -rf "$DEST/$APP"
ditto "$HERE/$APP" "$DEST/$APP"

# Downloaded apps are quarantined; Hearthlens is not notarized, so clear the flag and sign it locally
# (Apple silicon only runs signed code; an ad-hoc signature made on this Mac is enough).
xattr -dr com.apple.quarantine "$DEST/$APP" >/dev/null 2>&1 || true
codesign --force --deep --sign - "$DEST/$APP" >/dev/null 2>&1 || true

# Desktop shortcut.
rm -rf "$HOME/Desktop/Hearthlens"
ln -s "$DEST/$APP" "$HOME/Desktop/Hearthlens"

# Keep the uninstaller somewhere it can be found again.
SUPPORT="$HOME/Library/Application Support/Hearthlens"
mkdir -p "$SUPPORT"
cp "$HERE/Uninstall Hearthlens.command" "$SUPPORT/Uninstall Hearthlens.command" 2>/dev/null || true
chmod +x "$SUPPORT/Uninstall Hearthlens.command" 2>/dev/null || true

echo "Done. Hearthlens is in $DEST and on your Desktop. Starting it now…"
open "$DEST/$APP"
