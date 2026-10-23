#!/bin/bash
# Double-click to remove Hearthlens: the app, its Desktop shortcut and its login item.
# You choose whether to keep your match history and settings.
osascript -e 'quit app "Hearthlens"' >/dev/null 2>&1 || true
pkill -x Hearthlens >/dev/null 2>&1 || true

rm -rf "/Applications/Hearthlens.app" "$HOME/Applications/Hearthlens.app" 2>/dev/null
if [ -e "/Applications/Hearthlens.app" ]; then
  echo "Removing /Applications/Hearthlens.app needs your password:"
  sudo rm -rf "/Applications/Hearthlens.app"
fi
rm -f "$HOME/Desktop/Hearthlens"

AGENT="$HOME/Library/LaunchAgents/app.hearthlens.desktop.plist"
if [ -f "$AGENT" ]; then
  launchctl unload "$AGENT" >/dev/null 2>&1 || true
  rm -f "$AGENT"
fi

SUPPORT="$HOME/Library/Application Support/Hearthlens"
read -r -p "Also delete your match history and settings? [y/N] " answer
if [[ "$answer" =~ ^[Yy] ]]; then
  rm -rf "$SUPPORT"
  echo "Your match history and settings were deleted."
else
  rm -f "$SUPPORT/Uninstall Hearthlens.command"
  echo "Your match history and settings were kept."
fi
echo "Hearthlens was removed."
