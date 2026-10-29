# Match replay ("разбор матча")

Click a game in History and its page opens: header, a turn-by-turn replay seen from either side, both decks and an
analysis. Modelled on what HSReplay / HDT / Firestone show; no code taken from them.

## The rule: during a game only what the player sees; after it, what the log revealed

**During a game - only what the player can see; after the match the review may show everything the log revealed by
the end of the game.** The overlay, the live trackers and everything shown while a game runs are unchanged by this
page. The page of a finished game shows, like HSReplay, the opponent's cards the game revealed later: a card that was
a card back in the opponent's hand and was played or shown later is face up on the earlier turns too, marked
"revealed on turn N"; the opponent's mulligan shows the kept and replaced cards that came to light; the Decks tab lists
every card the opponent revealed. A card the game never revealed (nowhere in the log) stays a card back.

**One exception during a game (owner's decision, 2026-10-29, "давай сделаем как в firestone"; legal report
`reference/reviews/LEGAL-HeartLens-bg-opponent-mmr.md`, L-M1):** Battlegrounds opponents' ratings from Blizzard's
public leaderboard, matched by the names the client shows on its leaderboard. The rating is open data outside the
client, not game state: the player could look the names up on Blizzard's site. It stays an off-by-default option with
the conditions in [bg-opponent-mmr.md](bg-opponent-mmr.md) (whole table downloaded, names never sent or saved, "?"
for a shared name, "<8000" below the table, no lobby average). Nothing else from outside the client is covered.

How the two parts stay apart:

- The **events** of the file are recorded as seen, under the rules below (`ReplayRecorder.Events`). They are the same
  during and after a game and never hold a later reveal.
- The **post-game part** (`N`, `K` lines, format 3) is appended only by `ReplayRecorder.Text()` when a game that
  ended is saved (an abandoned game - the client left mid-game - is saved without it: it may go on after a reconnect): the game entity behind each opponent card back and what the log revealed about each opponent entity
  that was hidden (hand, secret, deck) by the end. Only `MatchReplay`/`MatchPage` read it.
- Test: `ReplayRevealTests.The_live_game_gets_nothing_from_the_future` (the live view and the events at turn 5 hold
  nothing of the card played on turn 7, and stay as they were after it).

### Events: as the player saw them

The event lines show only what the local player could see at that moment. This is enforced **when recording**
(`ReplayRecorder`, the same for live games and log imports).

- A card's id is written only while the card is face up for the local player: anything in `PLAY` or `GRAVEYARD`, the
  local player's `HAND` and `SECRET` zones, an opponent's quest. The opponent's hand and secrets are card backs: no
  id, no tags, no creator (`CREATOR_DBID` is never read), only "entered hand on turn N" and how it got there as the
  player saw it (`ORIGIN`: start / drawn / created / returned / stolen / coin / unknown). An opponent's secret-zone
  card is a card back whatever order `ZONE=SECRET` and `SECRET=1` arrive in.
- **A deck is a number of cards, nothing more** (`D L 24`, `D O 25`). No card in a deck has a replay id: a card that
  goes into a deck (a mulligan, a trade, a shuffle) only ends its hand or table id (`X n`), and a drawn card is a new
  id in the hand. Which card was drawn therefore cannot be told from the file: the review of v1 found that per-card
  deck ids linked "sent back at the mulligan" with "drawn later and played" (6 of 20 games on 23.10).
- Any card crossing a hidden zone (a deck, the opponent's hand, an opponent's secret) gets a new replay id, so it
  cannot be followed in the events. Game entity ids are never written in the events (only in the post-game part).
- Tags are written for face-up cards only, from a short allow-list (stats, keywords, mana). Choices are the local
  player's only (the parser reports nothing else).
- The filter depends on which player is local, so the recorder checks it: a game whose mulligan offer belongs to the
  player taken for the opponent, or whose "opponent" holds named starting cards while the local player holds none, is
  not recorded at all.
- After the game-over step only the result line is written; the last frame is what the game showed at its end.
- No new memory reads: the header uses what the tracker already has (our rank, the opponent's rank medal the client
  shows at game start, BG rating), read-only.

### Battlegrounds: as seen

A Battlegrounds game (format 4) is recorded by `BattlegroundsReplayRecorder`, which `ReplayRecorder` hands the game's
events once the game is Battlegrounds and the local player is known (live games and log imports alike), on the same
rule:

- **Bob's offer only in the tavern.** When a combat starts every offer card leaves view (`X`), and a card of the offer
  left in play during the fight (a frozen one) is never taken for the opponent's; it shows again in the next tavern. A
  recording that starts during a combat (a reconnect) takes a frozen minion in Bob's play for his offer.
- **The combat opponent only during the combat with it**: its hero (a hero in the innkeeper's slot with a leaderboard
  slot, `PLAYER_ID`; Bob has none), hero powers, trinkets and board in play. They leave view at the combat's end; the
  page keeps no "last known board" between combats.
- **No hand but ours, no other player's tavern**; no secret but our quest; nothing set aside except the options of our
  own choices, as card ids on the `C` line. Cards of any other controller (a Duos partner) are not written.
- **No names**: no player name, BattleTag, account id, rating or MMR of anybody (the bg-mmr rule); the leaderboard is
  hero card ids with place, health, armor and tavern tier, as the game shows them. The file's summary has our hero,
  place and rating (`ReplaySummary`), never a name.
- Enchantments, the game, the players, buttons and empty trinket slots (`BG30_Trinket_1st/2nd`) are never cards on
  screen.

## Storage

- One file per game: `<data>/replays/<started yyyyMMddHHmmss>-<hash of game key>.hpr`. Gzip of UTF-8 text lines,
  written to a temp file of its own and moved into place. The name is a pure function of the game key, so a re-import
  writes nothing new (`ReplayStore.Save` skips an existing file of the current format).
- `history.db`: new column `replay TEXT NULL` (file name), appended to `HistoryStore.AddedColumns`; filled live by
  `GameSession`, by `LogBackfill` for imported games, kept by `FillMissingFields`. Old rows: NULL -> "No replay for
  this game".
- Reconnects: the part of a game played before the client went away is a separate (abandoned) row that the
  reconnect's row replaces. Its replay goes with it (`HistoryStore.Replays`): the reconnect takes the file over when
  it has none of its own, else the file is deleted; an abandoned part saved after its reconnect deletes its own file.
  A reconnect's replay starts from the table at the moment of the reconnect.
- Limits (`ReplayStore`): a game stops recording after 60 000 events (marked `truncated`); a Battlegrounds game after
  100 000 events, and a combat stops adding steps after 800 (its state lines go on, the end still shows, marked
  `truncated`); the folder keeps at most
  300 MB / 3 000 files (Settings → Data says so), oldest game first (by the start time in the name, else by the
  file's date). A log import trims once at the end. A deleted file just means "no replay".
- Format 1 (`HPR1`, with deck card ids in the events) is never read: such a file is deleted when it is opened or saved
  over, and the first trim of a session deletes every one left (plus temp files a crash left behind).
- Format 2 (`HPR2`, events only) is still read: its games show as seen, without later reveals. Format 3 (`HPR3`) is
  read too: its Battlegrounds games have rounds and lobby but no steps (they cannot be rebuilt without the log). A log
  import of the same game replaces either with format 4 (`ReplayStore.Save` overwrites any file that is not of the
  current format).
- Size on a real game (28.10, 12 turns, 12 combats): 2 935 event lines, 368 steps, 67 KB of text, 14 KB gzip.

### File format 3

```
HPR3
{"kind":"constructed"|"battlegrounds","localRank":..,"opponentRank":..,"truncated":..,"events":..}
<event lines>
```

The header holds only what the history row lacks (both ranks), whether the game was cut at the event cap and how
many lines follow; no names. Events (space-separated; `L`/`O` = local/opponent side; `n` = replay id; `-` = none):

| Line | Meaning |
|---|---|
| `T turn side secs` | Game turn starts, `side` is to act; `secs` since game start |
| `D side count` | The side's deck now holds `count` cards |
| `E n side zone pos card type [TAG=v ...]` | Card `n` now is in `zone` (`PLAY`, `GRAVEYARD`, `HAND`, `SECRET`) at `pos` (card `-` = card back); face-up tags follow |
| `X n` | Card `n` left view (into a deck, a hidden zone under a new id, or set aside) |
| `V n TAG v` | Face-up tag change (`n` may be `L`/`O` for the player: mana, the local mulligan state and result) |
| `B` | A card is about to be played: a step boundary (its `P` follows once the card left the hand) |
| `P n target` | Card `n` played from hand (target `n` or `-`) |
| `U n target` | Card `n` already on the table used: hero power, location |
| `A n target` | `n` attacks `target` |
| `C kind source opt,opt,..` | Local choice offered (Discover etc.); `c card` = picked |
| `Z n pos` | Card `n` moved within its zone |
| `S step` | Game step `BEGIN_MULLIGAN`, `MAIN_READY`, `FINAL_GAMEOVER` |
| `R result` | Local PLAYSTATE at the end |

Post-game part, after the events (written once the game is over, read only by the match page):

| Line | Meaning |
|---|---|
| `N n entity` | Opponent card back `n` (hand or secret) was game entity `entity` |
| `K entity card turn how created [from]` | What the log revealed about an opponent entity that was hidden: the first card id it was given (later ids are transformations, as `initial_card_id` in python-hearthstone), the game turn of that, how it came to light (`played` from hand, `summoned` to the table, `burned` - into the graveyard without being played, `shown` - anything else), `1` when created during the game (not a deck card; the Coin too), and `TRANSFORMED_FROM_CARD` (dbf id) when set - the page then names the card it was in hand |

The opponent's mulligan is read from the events with `N`: its hand at `S BEGIN_MULLIGAN` against its hand at
`S MAIN_READY` (at most four cards; the Coin, hidden as a fifth opening card in real logs, is left out).

A card back in the opponent's hand carries `ORIGIN=...` instead of tags. The local mulligan is
`C MULLIGAN - <offered>`; what went back is read from the hand while the replacements are dealt.

In format 3 Battlegrounds games had no events; their file held one JSON line after the header: rounds (turn, tier,
health, our board, the combat opponent's hero and board as fought, shop offers, result and damage) and the lobby
(heroes, final places). Format 4 keeps that line as its `G` line.

### File format 4

```
HPR4
{"kind":"constructed"|"battlegrounds","summary":{...},"localRank":..,"opponentRank":..,"truncated":..,"events":N}
<N event lines>
<post-game part>      Constructed: N/K lines as in format 3; Battlegrounds: one line G {json BattlegroundsReplayData}
```

The header adds `summary` (`ReplaySummary`): mode, game type, format, start (UTC), seconds, our hero and class, the
opponent's hero, class and archetype (Constructed), result, turns, first/coin, deck code, place, rating before and
after, tavern tier, anomaly, tribes, and for Battlegrounds the hero powers, trinkets, final board (stats, golden,
keywords) and hand at the end. No names, BattleTags, account ids or deck name. Constructed events are the grammar of
format 3, unchanged.

Battlegrounds events, one space between tokens, nothing else on the line. Tokens: `n`, `t` - a replay id
`[1-9][0-9]{0,6}`, `-` where a line allows none; `card` - a card id `[A-Za-z0-9_]{1,64}`; numbers (`secs`, `taken`,
`dealt`, `v`, `health`, `armor`) - `0` or `[1-9][0-9]{0,8}`, never negative; small numbers (`pos`, `place`, `slot`,
`tier`) - `0`-`99`; `turn` - `1`-`999`. A step line closes the step before it; the step's table is the state when the
next step line comes. `BattlegroundsReplayGrammar` (internal, Core) holds this table as one regular expression: every
line the recorder writes matches it (tests run it over the 28.10 game and the fixture), and a stricter check of a
foreign file can start from it.

| Line | Meaning |
|---|---|
| `T turn secs` | The tavern of Battlegrounds turn `turn` ((TURN + 1) / 2) opens; `secs` since game start. Step |
| `F turn secs` | The combat of `turn` starts (BOARD_VISUAL_STATE 2 or BACON_IN_COMBAT_PHASE 1, whichever first). Step |
| `f result taken dealt` | The combat ends (BOARD_VISUAL_STATE 1, BACON_IN_COMBAT_PHASE 0, the next odd TURN or our end): `WIN`, `LOSS` or `TIE` and the hero damage of the attack after the fight. Step: the boards as the fight left them; the lines after it (the game's clean-up) show with the next step |
| `M kind n` | A step about one card or none: `BUY n`, `SELL n`, `MOVE n`, `TRIGGER n` (a card's trigger); `n` may be `-` (no `CARD_TARGET`, a source never shown) |
| `M kind n t` | A step with a target: `PLAY n t`, `POWER n t` (hero power), `USE n t` (an activated power of our minion on the table), `ATTACK n t`; either may be `-` |
| `M kind -` | A step about no card: `REFRESH -`, `UPGRADE -`, `FREEZE -` (freeze or unfreeze), `DEATHS -` |
| `C kind source opt,opt,..` | Our choice, written at the pick with the options as they were then (a hero reroll changes one): `HERO`, `DISCOVER`, `TRINKET`, `QUEST`; `source` a card id or `-`; 1-16 option card ids. Step (the hero choice opens the game). A choice the log never shows picked (the turn ran out) is written without `c` when the next choice comes, the combat starts or the game ends |
| `c card` | Our pick, one of the options of the `C` before it (several lines for several picks) |
| `R place` | The game is over for us: our hero's leaderboard place when it ended (`0` when the game gave none). Step |
| `E n side zone pos card type [TAG=v ...]` | Card `n` is shown at `zone` slot `pos` (`0` for a hero, a hero power, a trinket, a card just put in hand). `side` + `zone` + `type`: `L PLAY` `HERO HERO_POWER MINION BATTLEGROUND_TRINKET BATTLEGROUND_QUEST_REWARD`; `L HAND` `MINION BATTLEGROUND_SPELL SPELL`; `L SECRET` `SPELL` (our quest); `B PLAY` (Bob's offer) `MINION BATTLEGROUND_SPELL SPELL`; `O PLAY` (the combat opponent) the types of `L PLAY`. Nothing else. Tags from `ATK HEALTH DAMAGE ARMOR PREMIUM TAUNT DIVINE_SHIELD REBORN POISONOUS VENOMOUS WINDFURY MEGA_WINDFURY STEALTH FROZEN TECH_LEVEL`, written when not 0 |
| `X n` | Card `n` left view (sold, refreshed away, removed after a fight) |
| `Y n` | Card `n` died (from play to the graveyard): shown dying in this step, gone after it |
| `Z n pos` | Card `n` moved to slot `pos` |
| `V n TAG v` | A tag of card `n` (the list above), the value the block left |
| `V L TAG v` | Our player: `RESOURCES`, `RESOURCES_USED`, `TEMP_RESOURCES` (gold), `PLAYER_TECH_LEVEL` |
| `L slot side hero place health armor tier` | A leaderboard row changed: `slot` = the game's player slot, `side` `L` ours or `-`, the hero card shown, place (`0` before it is known), health left (`0` for a dead hero, never negative), armor, tavern tier. The rows before `f` and `R` are the ones of that moment |

Rules a reader (and the import sanitizer) can check: `B` cards never appear between `F` and `f`; `O` cards only there
(all of them leave view by the next `T`); `HAND` and `SECRET` only on side `L`; a `V`/`Z`/`X`/`Y` line only for a card
shown. Steps: a tavern action is one top-level block of ours (`TB_BaconShop_DragBuy(_Spell)` buy, `TB_BaconShop_DragSell`
sell, `TB_BaconShop_8p_Reroll_Button` refresh, `TB_BaconShopTechUp0N_Button` upgrade, `TB_BaconShopLockAll_Button`
freeze, a PLAY from our hand, a hero power, a PLAY of our minion on the table (its activated power: `USE`),
`MOVE_MINION` or a slot change of ours outside a block, a TRIGGER of a card
of ours in play); in combat an ATTACK, a DEATHS block where a shown card died, or a TRIGGER of a minion, hero, hero
power, trinket or quest reward of either side. Blocks of the game, a player or an enchantment are no step; nested
blocks belong to the step around them; a step writes the values its block left, one line per card and tag. Real logs
leave some blocks without their `BLOCK_END` (a play with a choice, a minion's activated power): a player's PLAY or
MOVE_MINION and every game STEP but `MAIN_END` are printed at the top level, so they end such a step.

## Model (`Core/Replay`)

- `ReplayRecorder : IPowerListener` - the projection above; `Text(header)` gives the file text. A Battlegrounds game
  goes to `BattlegroundsReplayRecorder` (`Battlegrounds.Text(header, data)`).
- `ReplaySummary` - the header's game summary (format 4), built by `ReplayCapture` from the history row and the end of
  the recording.
- `BattlegroundsMatchReplay` - folds Battlegrounds events into `BgReplayFrame`s: the hero choice, each tavern action,
  each combat step, the end of each combat (`Result`, damage) and of the game (`Place`), with every card shown, gold,
  tier and the leaderboard.
- `ReplayStore` - path, save (gzip, atomic), load, delete, trim.
- `ReplayCapture` - saves a finished game's replay and puts its name on the history row.
- `MatchReplay.Parse(text)` - folds events into `ReplayFrame`s (one per step: game start, mulligan, turn start, play,
  hero power, location use, attack, choice, end) with both sides' hero, hero power, weapon, board, hand (face up or
  card back), secrets, deck count, mana; `ReplayTurn`s group the steps of a game turn. With the post-game part a card
  back carries `ReplayCard.Reveal`; `Reveals` and `OpponentMulligan` list what came to light.
- `MatchPage` - what the page shows (header, steps from either side, turn chips, actions, decks, rounds, lobby), in
  the interface language (`Strings/*/match.json`); the open page is built again when the language changes.
  `MatchPage.Battlegrounds.cs`: `HasBattlegroundsSteps`, `BattlegroundsStep(frame)` (`MatchBgStepView`, contract in
  `MatchPlayerContract.cs`), the marks and spans of the timeline (texts `Strings/*/bgreplay.json`); `FrameCount`
  counts Battlegrounds steps too. The last step of each combat carries `ResultKey`.
- `MatchAnalysis` - see below.
- Memory: only the open match is kept (`MatchViewModel.Clear` on Back, on leaving History, before another game).

## Screens (WPF + Avalonia)

History row (or a recent game on the Overview) -> match page (replaces the History list, "History" goes back):

1. Header card: both heroes, names, rank/rating, class, format/mode, duration, first/coin, result.
2. Tabs: **Replay** | **Decks** | **Analysis** (Battlegrounds: **Rounds** | **Lobby**).
3. Replay (Constructed, and Battlegrounds games with a format 4 file, where it opens first): a player card - the
   whole-game timeline (`MatchPage.Timeline`: mulligan and turns, or tavern and combats coloured by the combat's
   result; event ticks from `MatchPage.Marks`, merged into at most 300 on long games; position by step, not time;
   click a tick or stretch, or drag to scrub; `MatchTimeline` panel in each UI) over the bar: turn / event / step
   back and forward around play-pause, speed x1/x2/x4 (a step a second / speed, pauses on the last step, stops on
   another tab, another game or Back), the position, "?" with the legend and keys. Keys on the tab (caught before the
   focused control): Space play/pause, ←/→ event (`EventFrom`; turn starts, combat deaths and triggers are steps
   only), Ctrl+←/→ turn (`TurnFrom`: the next stretch that is not a combat), Enter/Backspace step, +/− speed, Esc back.
   Battlegrounds: the step's table from `MatchBgStepView` (Bob's offer or the combat opponent on top, ours at the
   bottom, the step bar, choices, hand; leaderboard and the tavern's or combat's events on the right); a format 3
   file keeps Rounds / Lobby with a note. Constructed: "My side / Opponent's side" (which side is at the bottom), both
   heroes, boards, hands, secrets, mana, deck count, the turn's actions. Hands, boards and choices are full card
   renders from HearthstoneJSON (`render/latest/{locale}/256x/{id}.png`, the card-data locale) through `CardArt.Card`
   (disk cache per locale, decoded at 256 px, memory cache bounded in bytes); the current attack and health sit over
   the printed ones; a card back is drawn in the theme. A step with a choice shows its row: the Discover options with
   the picked one framed, or at the mulligan both players' cards kept and replaced. Hover shows the full card.
4. Decks: deck-tracker tiles (cost, tile art, copies, as the overlay's deck) with a hover preview. Our list (deck code,
   else the cards we drew) with drawn/played marks; "Opponent's cards": every card the log revealed, with counts, how
   and the first turn (created cards last); its archetype guess; "Likely deck": the most played meta list of its
   archetype (the clear call, else the closest by shown core cards), marked as a guess, the shown cards highlighted.
5. Analysis: tiles, health and board charts, mana bars, key cards, mulligan vs the meta, meta standing, turn table.

## Analysis heuristics (`MatchAnalysis`)

- Health and board: at the last step of each game turn, hero health + armor and the attack + health of each side's
  minions.
- Mana: on each completed own turn, (RESOURCES_USED - OVERLOAD_LOCKED) / (RESOURCES + TEMP_RESOURCES -
  OVERLOAD_LOCKED); the turn the game ended in is left out.
- Cards that decided the game: per play, hero power, location use or attack, the acting side's board gain minus the
  other side's, plus face damage (+100 for lethal); a card's best action counts, top three per player.
- Mulligan: offered cards kept or replaced, against the keep rate and in-hand win rate of the deck's meta archetype
  (`ConstructedMeta.Match` on the deck code); a choice "agrees" when it follows a majority above 60% / below 40%.
- Meta: both archetypes' win rates, first/coin win rate of ours, which deck was stronger on paper.

## Stages

1. Recording and storage (recorder, store, history column, live + backfill wiring, limits). Done.
2. Replay model + tests (synthetic hidden-info cases, real logs in `D:\Hearthstone\Logs`). Done.
3. UI in both apps + render scenes (`main-match*`, Avalonia also `main-match-compact`). Done.
4. Analysis. Done.
5. Review fixes: deck as a count (format 2), reconnect files, local player checks, interface languages. Done.
6. Post-game reveals (format 3), full card renders, deck tiles, choice rows, likely deck. Done.
7. Format 4: Battlegrounds step by step, the header's summary (TASK-2026-10-29-replay-v2, P1). Done; the player bar,
   timeline and BG table (P2) and the library (P3) build on it.

Ideas checked (only ideas, no code taken): python-hearthstone / python-hslog (MIT) - first card id of an entity kept
as `initial_card_id`, `TRANSFORMED_FROM_CARD`; HSReplay's Joust viewer (AGPL, ideas only)
 - an entity id to card id map
built from the whole game and applied to earlier turns; HearthstoneJSON - card renders; RTantrumR/HS_ReplayWatcher
(Apache-2.0, ideas only, no code: its code was not read) - a Battlegrounds replay by shop action and combat block,
playback speeds, a library of replays.


Known limits: Battlegrounds games saved before format 4 keep rounds, not a step-by-step replay; the opponent's rank
shows only when the client's memory was read during the game (live, not log imports); a reconnect's replay starts at
the reconnect; games played before this feature have no replay. Battlegrounds: the combat is the server's (GameState
lines, the whole fight within a second), not the client's animation; in Duos only our board and the opponent's are
written (the partner's board in combat is not) and no real Duos log was checked; the result and damage of a combat come
from the hero attack after the fight (none: a tie, unless our hero lost health otherwise); a recording that starts
during a combat would hide an opponent's minion frozen at that moment (it is taken for Bob's frozen offer).
