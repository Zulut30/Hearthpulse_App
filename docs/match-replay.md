# Match replay ("разбор матча")

Click a game in History and its page opens: header, a turn-by-turn replay seen from either side, both decks and an
analysis. Modelled on what HSReplay / HDT / Firestone show; no code taken from them.

## The rule: nothing hidden

The replay shows only what the local player could see at that moment. This is enforced **when recording**
(`ReplayRecorder`, the same for live games and log imports), so a replay file never holds hidden information,
whatever reads it later.

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
  cannot be followed. Game entity ids are never written (the rule needs no assumption about how the server numbers
  them).
- Tags are written for face-up cards only, from a short allow-list (stats, keywords, mana). Choices are the local
  player's only (the parser reports nothing else).
- The filter depends on which player is local, so the recorder checks it: a game whose mulligan offer belongs to the
  player taken for the opponent, or whose "opponent" holds named starting cards while the local player holds none, is
  not recorded at all.
- After the game-over step only the result line is written; the last frame is what the game showed at its end.
- No new memory reads: the header uses what the tracker already has (our rank, the opponent's rank medal the client
  shows at game start, BG rating), read-only.

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
- Limits (`ReplayStore`): a game stops recording after 60 000 events (marked `truncated`); the folder keeps at most
  300 MB / 3 000 files (Settings → Data says so), oldest game first (by the start time in the name, else by the
  file's date). A log import trims once at the end. A deleted file just means "no replay".
- Format 1 (`HPR1`, with deck card ids) is never read: such a file is deleted when it is opened or saved over, and
  the first trim of a session deletes every one left (plus temp files a crash left behind).

### File format 2

```
HPR2
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

A card back in the opponent's hand carries `ORIGIN=...` instead of tags. The local mulligan is
`C MULLIGAN - <offered>`; what went back is read from the hand while the replacements are dealt.

Battlegrounds games are not recorded as events (combat floods the log); their file holds one JSON line after the
header: rounds (turn, tier, health, our board, the combat opponent's hero and board as fought, shop offers, result
and damage) and the lobby (heroes, final places).

## Model (`Core/Replay`)

- `ReplayRecorder : IPowerListener` - the projection above; `Text(header)` gives the file text.
- `ReplayStore` - path, save (gzip, atomic), load, delete, trim.
- `ReplayCapture` - saves a finished game's replay and puts its name on the history row.
- `MatchReplay.Parse(text)` - folds events into `ReplayFrame`s (one per step: game start, mulligan, turn start, play,
  hero power, location use, attack, choice, end) with both sides' hero, hero power, weapon, board, hand (face up or
  card back), secrets, deck count, mana; `ReplayTurn`s group the steps of a game turn.
- `MatchPage` - what the page shows (header, steps from either side, turn chips, actions, decks, rounds, lobby), in
  the interface language (`Strings/*/match.json`); the open page is built again when the language changes.
- `MatchAnalysis` - see below.
- Memory: only the open match is kept (`MatchViewModel.Clear` on Back, on leaving History, before another game).

## Screens (WPF + Avalonia)

History row (or a recent game on the Overview) -> match page (replaces the History list, "History" goes back):

1. Header card: both heroes, names, rank/rating, class, format/mode, duration, first/coin, result.
2. Tabs: **Replay** | **Decks** | **Analysis** (Battlegrounds: **Rounds** | **Lobby**).
3. Replay: turn strip, step buttons and slider, "My side / Opponent's side" (which side is at the bottom), both
   heroes, boards, hands (card backs for the opponent), secrets, mana, deck count, the turn's actions.
4. Decks: our list (deck code, else the cards we drew) with drawn/played marks; the cards the opponent played from
   hand with the turn, and its archetype guess.
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

Known limits: Battlegrounds games keep rounds, not a step-by-step replay; the opponent's rank shows only when the
client's memory was read during the game (live, not log imports); a reconnect's replay starts at the reconnect; games
played before this feature have no replay.
