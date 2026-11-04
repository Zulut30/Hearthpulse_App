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

Community stats: Hearthpulse data (api.kolodahearthstone.com). Russian archetype names: the Hearthpulse archetype
dictionary (api.blizzcore.ru). Card data and art: HearthstoneJSON. The Brawl page's article: hs-manacost.ru (its
"Потасовка" posts and cover pictures, opened in the browser).
Not affiliated with Blizzard Entertainment.

CRASH REPORTS
- Off by default. A report of an unexpected error goes to hearthpulse.net only
  when you press "Send report" in the error window, or when you turn on
  "Send crash reports automatically" in Settings > Data (turning it off deletes
  reports waiting to go).
- A report holds the app, system and .NET versions, the interface language,
  the error type and its text, and where in the code it happened. Names,
  BattleTags, e-mail addresses, paths, quoted values and long numbers are cut
  out of the text before it is sent. It is not tied to your account, and your
  IP address is not kept. Reports are kept for 90 days.
