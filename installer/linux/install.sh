#!/usr/bin/env bash
# Installs Hearthpulse from this folder.
#   ./install.sh            for your user only (no password): ~/.local/share/hearthpulse
#   sudo ./install.sh       for everyone: /opt/hearthpulse
# Adds a menu entry and a desktop shortcut. Remove it again with the "hearthpulse-uninstall" command
# (or run uninstall.sh from this folder).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ "$(id -u)" -eq 0 ]; then
  PREFIX=/opt/hearthpulse
  BIN=/usr/local/bin
  SHARE=/usr/local/share
  TARGET_USER="${SUDO_USER:-}"
else
  PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}/hearthpulse"
  BIN="$HOME/.local/bin"
  SHARE="${XDG_DATA_HOME:-$HOME/.local/share}"
  TARGET_USER="$(id -un)"
fi

echo "Installing Hearthpulse into $PREFIX"
pkill -x Hearthpulse 2>/dev/null || true
rm -rf "$PREFIX/app"
mkdir -p "$PREFIX/app" "$BIN" "$SHARE/applications"
cp -a "$HERE/app/." "$PREFIX/app/"
chmod +x "$PREFIX/app/Hearthpulse"
cp "$HERE/uninstall.sh" "$PREFIX/uninstall.sh"
chmod +x "$PREFIX/uninstall.sh"
ln -sf "$PREFIX/app/Hearthpulse" "$BIN/hearthpulse"
ln -sf "$PREFIX/uninstall.sh" "$BIN/hearthpulse-uninstall"

for size in 16 24 32 48 64 128 256 512; do
  mkdir -p "$SHARE/icons/hicolor/${size}x${size}/apps"
  cp "$HERE/icons/hearthpulse-$size.png" "$SHARE/icons/hicolor/${size}x${size}/apps/hearthpulse.png"
done

# Menu entry with the full path, so it works even when ~/.local/bin is not on PATH.
sed "s#^Exec=.*#Exec=\"$PREFIX/app/Hearthpulse\"#" "$HERE/hearthpulse.desktop" > "$SHARE/applications/hearthpulse.desktop"
chmod 644 "$SHARE/applications/hearthpulse.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database -q "$SHARE/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$SHARE/icons/hicolor" 2>/dev/null || true

# Runs a command as the user who started the installer (directly when not root).
as_user() {
  if [ "$(id -u)" -eq 0 ]; then sudo -u "$TARGET_USER" "$@"; else "$@"; fi
}

# Desktop shortcut for the user who ran the installer.
if [ -n "$TARGET_USER" ]; then
  USER_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
  DESKTOP_DIR="$(as_user xdg-user-dir DESKTOP 2>/dev/null || true)"
  [ -n "$DESKTOP_DIR" ] && [ "$DESKTOP_DIR" != "$USER_HOME" ] || DESKTOP_DIR="$USER_HOME/Desktop"
  if [ -d "$DESKTOP_DIR" ]; then
    install -m 755 "$SHARE/applications/hearthpulse.desktop" "$DESKTOP_DIR/hearthpulse.desktop"
    [ "$(id -u)" -eq 0 ] && chown "$TARGET_USER" "$DESKTOP_DIR/hearthpulse.desktop"
    # GNOME asks before launching untrusted desktop files; mark ours as trusted.
    as_user gio set "$DESKTOP_DIR/hearthpulse.desktop" metadata::trusted true 2>/dev/null || true
    echo "$DESKTOP_DIR/hearthpulse.desktop" > "$PREFIX/desktop-shortcut"
    echo "Desktop shortcut: $DESKTOP_DIR/hearthpulse.desktop"
  fi
fi

echo "Done. Start Hearthpulse from your app menu, the desktop shortcut, or by running: hearthpulse"
case ":$PATH:" in *":$BIN:"*) ;; *) echo "(Add $BIN to your PATH to use the 'hearthpulse' command.)" ;; esac
