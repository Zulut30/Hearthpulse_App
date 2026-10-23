HEARTHLENS FOR LINUX

Hearthlens is a Hearthstone companion: deck tracker, Battlegrounds advisor, combat
odds, in-game overlay and match history. On Linux, Hearthstone runs through Wine,
Lutris, Bottles or Steam (Proton); Hearthlens finds it in those prefixes and reads
the game's own log files. It never changes the game.

INSTALL
  For your user only (no password):   ./install.sh
  For every user on this computer:     sudo ./install.sh
This adds Hearthlens to your app menu and puts a shortcut on your desktop.
Prefer a package? Use the .deb (Ubuntu, Debian, Mint) or .rpm (Fedora, openSUSE).

UNINSTALL
  hearthlens-uninstall            (or ./uninstall.sh from this folder)
  hearthlens-uninstall --purge    also deletes your match history and settings
Packages: sudo apt remove hearthlens   /   sudo dnf remove hearthlens

FIRST START
Hearthlens turns on the log sections it needs in Hearthstone's log.config inside
the Wine prefix. Restart Hearthstone once afterwards. If Hearthstone is installed
somewhere unusual, set "HearthstonePath" in ~/.local/share/Hearthlens/settings.json.

OVERLAY
The in-game overlay works on X11 and on Wayland desktops through XWayland (the
default for Wine games). Your rating and the lobby tribes are read from game
memory on Windows only; on Linux those values stay empty.

Community stats: Firestone (zerotoheroes.com). Card data and art: HearthstoneJSON.
Not affiliated with Blizzard Entertainment.
