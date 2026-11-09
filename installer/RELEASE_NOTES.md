## Hearthpulse 0.15.0

### What's new
- **Safer updates.** Updates are checked against the project's signature before they install. If an install fails, the app puts the old version back and tells you on the next start. Settings → About has **Check now** and **What's new**.
- **Replay library.** History keeps your games as replays in Recent, Saved and Imported tabs: star a game, share it, import one, or upload it to hearthpulse.net to share a link. Export any replay step as a picture.
- **Move over from HDT or Firestone.** Bring your past games and decks from Hearthstone Deck Tracker or Firestone into History, My Stats and your deck library.
- **New overlay panels.** The opponent's likely deck, a graveyard for both sides, how much damage each board can deal to the enemy hero, and related cards next to the card you hover. Unlock the panels to move them with the mouse during a game.
- **Battlegrounds.** A clearer bar with turn, tavern tiers and fights, one-row combat odds with damage ranges, an opponent popup at the top, the Duos partner's board and the final board after the game.
- **Arena and Brawl.** An Arena draft helper (beta, off by default) and rewards for each run; Arena and Underground Arena counted apart (Arena is marked BETA). A new Tavern Brawl page with the best classes of the week.

### More
- Meta and mulligan advice for your rank, not only Legend.
- A new look: a gold frame, a new logo, a theme gallery with live preview, more themes, accent, font and density settings, and settings search.
- Session recap and deck pictures to post, YouTube timestamps, an OBS session window, streamer mode, and a Discord status (off by default).
- Your account in one place: sign-in, your profile and what your plan includes.
- Your games help build Hearthpulse statistics. Only visible game facts are sent, never your name or BattleTag, and you can turn it off in Settings → Data. Crash reports are sent only when you agree.

### Faster
- Pages open faster and the app starts faster from a warm start; replay steps are 15–20 % quicker.
- An option to reduce motion in the interface.
- From 0.15.0 on, the Windows app can update by downloading only the files that changed.

### Fixes
- Battlegrounds: fight results, odds and the final board show correctly in more cases.
- Overlay panels keep their place at the screen edges and stay readable at low opacity.
- Share pictures fill their space and long names no longer get cut.
- Many smaller fixes in History, the replay player, settings and the card pool.

### Known issues
- The first update that downloads only changed files is 0.15.0 → 0.16.0; updating to 0.15.0 uses the full setup.
- The Windows setup is not code-signed yet, so Windows SmartScreen may warn when you download it by hand. Choose "More info" → "Run anyway".
- macOS packages are not notarized: open the app with right-click → Open the first time.
- Linux packages do not update themselves: download the new package and install it as before.
- Release notes are in English only.

### Other
- Data: Hearthpulse data, HearthstoneJSON (card data and art), Blizzard's public Battlegrounds leaderboard (opponents' MMR, off by default), hs-manacost.ru (the Brawl article), icons from game-icons.net (CC BY 3.0). The update signature check is ported from RFC 8032. Full list in THIRD-PARTY-NOTICES.txt next to the app.
- Hearthpulse is proprietary software (All rights reserved).
- Hearthpulse is not affiliated with or endorsed by Blizzard Entertainment. Hearthstone is a trademark of Blizzard Entertainment, Inc. Use it at your own risk.
