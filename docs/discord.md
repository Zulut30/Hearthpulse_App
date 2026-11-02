# Discord status ("Статус в Discord")

Friends on Discord see what the player is playing (plan 3.4, `TASK-2026-11-02-discord`): Settings → Stream →
**Discord status** in both UIs, off by default. Hearthpulse talks only to the Discord app on the same PC (its local
RPC: the named pipe `discord-ipc-0…9` on Windows, the unix socket of that name in `XDG_RUNTIME_DIR` / `TMPDIR` / `/tmp`
elsewhere); Discord shows the status to the player's friends. Nothing else is sent anywhere.

Code: `src/Hearthpulse.Core/Discord/` (`DiscordIpc` the frames, `DiscordPresence` the sender), the text from a live
snapshot in `MainViewModel.Discord.cs` (Presentation). No third-party library.

## What the status shows

Two lines (`details`, `state`), the time since the game began and the app's picture, in the player's interface
language. Separator " · ", as on the live bar.

| Situation | Line 1 | Line 2 | Timer |
|---|---|---|---|
| Hearthstone not running | — (status cleared) | — | — |
| Menu, collection, finding an opponent | In the menu | — | no |
| Ranked / Casual / Friendly | Ranked · Standard | Priest vs Mage · turn 7 | from the game's start |
| Mulligan (turn 0) | as above | Priest vs Mage | from the game's start |
| Arena, Brawl, Practice (no format) | Arena | Hunter vs Mage · turn 5 | from the game's start |
| Arena draft screen | Arena | Drafting | no |
| Battlegrounds, choosing a hero | Battlegrounds | Choosing a hero | from the game's start |
| Battlegrounds / Duos | Battlegrounds (· Duos) | Reno Jackson · turn 7 · place 3 | from the game's start |
| Another mode | the mode's name, else "In a game" | — | from the game's start |

- Classes, never the deck's name (players name decks themselves; names can be long and personal).
- After a game ends: "In the menu" at once; the result is not shown.
- Lines are cut to Discord's 2–128 characters (127 + "…").
- **Never in the status**: the opponent's (or Duos partner's) name or BattleTag, ranks and MMR (anyone's), the
  predicted archetype or deck, cards in hand, deck or played, offered BG heroes, lobby tribes, combat odds. So streamer
  mode ([streaming.md](streaming.md)) does not change the status.
- Not yet: the Arena run's record (the snapshot has no cheap source), spectating (shown as a normal game).

## Sending

- Only changes are sent, and at most once every 15 s (Discord takes five updates in 20 s); the latest wanted status is
  sent when the time is up, none is lost.
- Without the Discord app: the status line says so and Hearthpulse looks again every 30 s; no errors for the player.
- Off: the status is cleared at once (`SET_ACTIVITY` with no activity) and the pipe closed.

## Registering the Discord app (owner)

The switch is disabled ("Not available yet") while `DiscordPresence.ApplicationId` is empty.

1. https://discord.com/developers/applications → New Application → name "Hearthpulse" (no "Hearthstone", no Blizzard
   art), accept the terms.
2. General Information → copy the Application ID → put it in `DiscordPresence.ApplicationId`
   (`src/Hearthpulse.Core/Discord/DiscordPresence.cs`).
3. Rich Presence → Art Assets → upload the app icon 512 × 512 PNG named `logo` → put `logo` in
   `DiscordPresence.LogoKey`. Discord shows a new asset after a few minutes.
4. App Icon in General Information: the same `hearthpulse.png`.

Before a release with the id set: `hearthpulse-legal` (Discord's API terms, publishing the player's status).

## Later

- Custom status templates (the player's own text) are not made: a premium candidate, gated by the data lane
  (`Accounts/Entitlements`).
