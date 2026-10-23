# Match replay ("разбор матча")

Click a game in History and its page opens: header, a turn-by-turn replay seen from either side, both decks and an
analysis. Modelled on what HSReplay / HDT / Firestone show; no code taken from them.

## The rule: nothing hidden

The replay shows only what the local player could see at that moment. This is enforced **when recording**, so a
replay file never holds hidden information, whatever reads it later.

- A card's id is written only while the card is face up for the local player: anything in `PLAY` or `GRAVEYARD`,
  the local player's `HAND` and `SECRET` zones, an opponent's `SECRET`-zone card that is not a secret (quests,
  sigils are face up). Opponent hand and both decks are card backs: no id, no creator, no `CREATOR_DBID` (never
  read), only "entered hand on turn N" and how it got there (drawn / created / returned / mulligan / coin).
- An entity never keeps its identity across a hidden zone: whenever a card moves into or out of a zone where it is
  hidden (`DECK` of either side, the opponent's `HAND`, an opponent's secret), it gets a **new replay id**. A card
  shuffled into a deck, or the opponent's drawn card, cannot be followed. Game entity ids are never written (the
  server numbers deck cards in deck-list order, so they would leak the draw).
- Tags are written for face-up cards only, from a short allow-list (stats, keywords, mana). Choices are the local
  player's only (the parser reports nothing else).
- After the game ends nothing more is revealed: the last frame is what the log showed at the end.
- No new memory reads: the header uses what the tracker already has (our rank, the opponent's rank medal the client
  shows at game start, BG rating), read-only.

## Storage

- One file per game: `<data>/replays/<started yyyyMMddHHmmss>-<hash of game key>.hpr`. Gzip of UTF-8 text lines.
  The name is a pure function of the game key, so a re-import writes nothing new (`ReplayStore.Save` skips an
  existing file).
- `history.db`: new column `replay TEXT NULL` (file name), appended to `HistoryStore.AddedColumns`; filled live by
  `GameSession`, by `LogBackfill` for imported games, kept by `FillMissingFields`. Old rows: NULL -> "No replay
  for this game (recorded before replays existed)".
- Limits (`ReplayStore`): a game stops recording after 60 000 events (marked `truncated`); the folder keeps at most
  300 MB / 3 000 files, oldest deleted first after each save. A deleted file just means "no replay".

### File format v1

```
HPR1
{header JSON: mode, gameType, format, players {local, opponent} {name, hero, class, rank}, first, result, start, end}
<event lines>
```

Events (space-separated; `L`/`O` = local/opponent side; `n` = replay id; `-` = unknown/none):

| Line | Meaning |
|---|---|
| `T turn side secs` | Game turn starts, `side` is to act; `secs` since game start |
| `E n side zone pos card type [TAG=v ...]` | Card `n` now is in `zone` at `pos` (card `-` = card back); face-up tags follow |
| `X n` | Card `n` left view (moved to a hidden zone under a new id, or set aside) |
| `V n TAG v` | Face-up tag change (`n` may be `L`/`O` for the player: mana) |
| `P n target` | Card `n` played (target `n` or `-`) |
| `A n target` | `n` attacks `target` |
| `C kind source opt,opt,..` | Local choice offered (Discover etc.); `c card` = picked |
| `M side n,n..` | Opening hand (local: ids; opponent: count only via card backs) |
| `R result` | Local PLAYSTATE at the end |
| `H origin n` | Hand origin of card back `n` (drawn/created/returned/mulligan/coin) |

Battlegrounds games are not recorded as events (combat floods the log); their file holds one JSON line after the
header: rounds (turn, tier, health, our board, the combat opponent's hero and board as fought, shop offers, result
and damage) and the lobby (heroes, final places).

## Model (`Core/Replay`)

- `ReplayRecorder : IPowerListener` - the projection above; `Finish(header)` gives the text.
- `ReplayStore` - path, save (gzip, atomic), load, cleanup.
- `MatchReplay.Load(text)` - folds events into `ReplayFrame`s (one per step: turn start, play, attack, choice,
  end) with both sides' hero, hero power, weapon, board, hand (face up or card back), secrets, deck count, mana;
  `ReplayTurn`s group steps with their actions (play / attack / death / discover / hero power).
- `MatchAnalysis` - health and board power per turn, mana used/available per own turn, mulligan (offered, kept,
  replaced, received), opponent's shown cards with turn, "cards that decided the game" heuristics, archetype guess
  (`ArchetypeGuess` on our API meta) and meta comparison (archetype win rate, matchup, mulligan keep rates).
- `BattlegroundsReplay` data: rounds + lobby.

## Screens (WPF + Avalonia)

History row -> match page (replaces History, "Back to History"):

1. Header card: both heroes, names, rank/rating, class, format/mode, duration, first/coin, result.
2. Tabs: **Replay** | **Decks** | **Analysis** (Battlegrounds: **Rounds** | **Lobby**).
3. Replay: turn strip, step back/forward, "My side / Opponent's side" switch (which side is at the bottom and whose
   actions the list follows), board of both sides, hands (card backs for the opponent), mana, deck count, the
   step's action list.
4. Decks: our list (deck code, else cards seen) with drawn/played marks; the opponent's shown cards with turn, and
   its archetype guess.
5. Analysis: health/board chart, mana efficiency bars, mulligan, key cards, meta comparison.

## Stages

1. Recording and storage (recorder, store, history column, live + backfill wiring, limits).
2. Replay model + tests (synthetic hidden-info cases, real logs in `D:\Hearthstone\Logs`).
3. UI in both apps + render scenes.
4. Analysis.
