#!/bin/bash
# Double-click to remove Hearthpulse: the app, its Desktop shortcut and its login item.
# You choose whether to keep your match history and settings.
osascript -e 'quit app "Hearthpulse"' >/dev/null 2>&1 || true
pkill -x Hearthpulse >/dev/null 2>&1 || true

rm -rf "/Applications/Hearthpulse.app" "$HOME/Applications/Hearthpulse.app" 2>/dev/null
if [ -e "/Applications/Hearthpulse.app" ]; then
  echo "Removing /Applications/Hearthpulse.app needs your password:"
  sudo rm -rf "/Applications/Hearthpulse.app"
fi
rm -f "$HOME/Desktop/Hearthpulse"

AGENT="$HOME/Library/LaunchAgents/app.hearthpulse.desktop.plist"
if [ -f "$AGENT" ]; then
  launchctl unload "$AGENT" >/dev/null 2>&1 || true
  rm -f "$AGENT"
fi

SUPPORT="$HOME/Library/Application Support/Hearthpulse"
read -r -p "Also delete your match history and settings? [y/N] " answer
if [[ "$answer" =~ ^[Yy] ]]; then
  rm -rf "$SUPPORT"
  echo "Your match history and settings were deleted."
else
  rm -f "$SUPPORT/Uninstall Hearthpulse.command"
  echo "Your match history and settings were kept."
fi
echo "Hearthpulse was removed."
