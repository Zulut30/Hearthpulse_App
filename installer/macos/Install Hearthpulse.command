#!/bin/bash
# Double-click to install Hearthpulse: copies it to Applications and puts a shortcut on your Desktop.
# (If macOS says it cannot be opened: right-click this file, choose Open, then Open again.)
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
APP="Hearthpulse.app"

DEST=/Applications
if [ ! -w "$DEST" ]; then
  DEST="$HOME/Applications"
  mkdir -p "$DEST"
fi

echo "Installing Hearthpulse into $DEST…"
osascript -e 'quit app "Hearthpulse"' >/dev/null 2>&1 || true
pkill -x Hearthpulse >/dev/null 2>&1 || true
rm -rf "$DEST/$APP"
ditto "$HERE/$APP" "$DEST/$APP"

# Downloaded apps are quarantined; Hearthpulse is not notarized, so clear the flag and sign it locally
# (Apple silicon only runs signed code; an ad-hoc signature made on this Mac is enough).
xattr -dr com.apple.quarantine "$DEST/$APP" >/dev/null 2>&1 || true
codesign --force --deep --sign - "$DEST/$APP" >/dev/null 2>&1 || true

# Desktop shortcut.
rm -rf "$HOME/Desktop/Hearthpulse"
ln -s "$DEST/$APP" "$HOME/Desktop/Hearthpulse"

# Keep the uninstaller somewhere it can be found again.
SUPPORT="$HOME/Library/Application Support/Hearthpulse"
mkdir -p "$SUPPORT"
cp "$HERE/Uninstall Hearthpulse.command" "$SUPPORT/Uninstall Hearthpulse.command" 2>/dev/null || true
chmod +x "$SUPPORT/Uninstall Hearthpulse.command" 2>/dev/null || true

echo "Done. Hearthpulse is in $DEST and on your Desktop. Starting it now…"
open "$DEST/$APP"
