#!/usr/bin/env bash
# Removes a Hearthpulse installed with install.sh (user or system-wide).
#   hearthpulse-uninstall            keeps your match history and settings
#   hearthpulse-uninstall --purge    also deletes them (~/.local/share/Hearthpulse)
set -euo pipefail

PURGE=0
[ "${1:-}" = "--purge" ] && PURGE=1

if [ "$(id -u)" -eq 0 ]; then
  PREFIX=/opt/hearthpulse
  BIN=/usr/local/bin
  SHARE=/usr/local/share
else
  PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}/hearthpulse"
  BIN="$HOME/.local/bin"
  SHARE="${XDG_DATA_HOME:-$HOME/.local/share}"
fi

if [ ! -d "$PREFIX" ]; then
  echo "Hearthpulse is not installed in $PREFIX (use sudo for a system-wide install)."
  exit 1
fi

echo "Removing Hearthpulse from $PREFIX"
pkill -x Hearthpulse 2>/dev/null || true
if [ -f "$PREFIX/desktop-shortcut" ]; then
  rm -f "$(cat "$PREFIX/desktop-shortcut")"
fi
rm -f "$BIN/hearthpulse" "$BIN/hearthpulse-uninstall" "$SHARE/applications/hearthpulse.desktop"
for size in 16 24 32 48 64 128 256 512; do
  rm -f "$SHARE/icons/hicolor/${size}x${size}/apps/hearthpulse.png"
done
rm -f "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/hearthpulse.desktop"
rm -rf "$PREFIX"
command -v update-desktop-database >/dev/null && update-desktop-database -q "$SHARE/applications" || true

if [ "$PURGE" -eq 1 ]; then
  rm -rf "${XDG_DATA_HOME:-$HOME/.local/share}/Hearthpulse"
  echo "Your match history and settings were deleted."
else
  echo "Your match history and settings were kept (~/.local/share/Hearthpulse). Use --purge to delete them."
fi
echo "Hearthpulse was removed."
