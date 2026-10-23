HEARTHPULSE FOR LINUX

Hearthpulse is a Hearthstone companion: deck tracker, Battlegrounds advisor, combat
odds, in-game overlay and match history. On Linux, Hearthstone runs through Wine,
Lutris, Bottles or Steam (Proton); Hearthpulse finds it in those prefixes and reads
the game's own log files. It never changes the game.

INSTALL
  For your user only (no password):   ./install.sh
  For every user on this computer:     sudo ./install.sh
This adds Hearthpulse to your app menu and puts a shortcut on your desktop.
Prefer a package? Use the .deb (Ubuntu, Debian, Mint) or .rpm (Fedora, openSUSE).

UNINSTALL
  hearthpulse-uninstall            (or ./uninstall.sh from this folder)
  hearthpulse-uninstall --purge    also deletes your match history and settings
Packages: sudo apt remove hearthpulse   /   sudo dnf remove hearthpulse

FIRST START
Hearthpulse turns on the log sections it needs in Hearthstone's log.config inside
the Wine prefix. Restart Hearthstone once afterwards. If Hearthstone is installed
somewhere unusual, set "HearthstonePath" in ~/.local/share/Hearthpulse/settings.json.

OVERLAY
The in-game overlay works on X11 and on Wayland desktops through XWayland (the
default for Wine games). Your rating and the lobby tribes are read from game
memory on Windows only; on Linux those values stay empty.

Community stats: Hearthpulse API (HSReplay, HSGuru data). Card data and art: HearthstoneJSON.
Not affiliated with Blizzard Entertainment.
