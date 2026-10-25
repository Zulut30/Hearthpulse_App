# Arena draft helper

Backlog A2 (data and card scores), A1 (draft helper on the overlay), W2 (community Arena numbers in the app). Both UIs.

## What the player sees

- **Draft screen, overlay panel `arena-picks`**: a badge over each of the three offered cards with its ArenaSmith
  score and tier colour; the highest score gets a gold border, a star and "Best pick". At the hero pick the badges show
  the class win rate (tier from the win rate) and the 7+ wins share; the best class is marked.
- **Overlay panel `arena-draft`** (left edge): run class and picks `n/30`, average ArenaSmith score of the deck so far
  with its tier, mana curve 0..7+, class win rates at the hero pick (the offered classes in gold), the cards picked by
  cost with their scores. Attribution line "Scores: Hearthpulse data".
- **Arena page** (main window, My Stats Arena): each run row shows "Deck score N" when the run saved a deck code; a
  "Deck score" card for the latest run (average, tier, curve, three strongest and weakest cards); a "Class win rates"
  card with the community numbers.
- Setting: `AppSettings.ShowArenaDraft`, **beta, off by default** until the log lines and the memory read below are
  checked on a real draft (review M1); Settings → Tracker in both apps, labelled "(beta)". While it is a beta the
  tracker logs to `hearthpulse.log` every Arena.log line it recognized with the resulting state
  (`Arena draft: recognized "..." -> hero ..., deck ..., N cards, mode ...`) and every change of the offer read from
  memory (`Arena draft: offer read from memory: A, B, C` / `no offer read from memory`): compare them with the screen.
  In the UI the numbers are credited to "Hearthpulse data" only. Both panels hide outside the
  `DRAFT` scene and take no clicks (`IsHitTestVisible="False"`, no `OverlayHit.Interactive`), so the cards stay
  clickable.

## Data (Hearthpulse data API, no token)

| Call | Used for | Cache |
|---|---|---|
| `GET datasets/hsreplay_arena_cards_advanced` | `data.structured.cards[]`: `id`, `dbfId`, `score` (ArenaSmith), `win_rate`, `pick_rate`, `times_played` | `cache/api/arena-cards.json`, 6 h; only those fields are kept (the answer is about 1.7 MB, the list is published twice) |
| `GET v1/arena/classes?limit=20` | `data[]`: `class`, `win_rate`, `pick_rate`, `pct_7_plus`, `num_drafts` | `cache/api/arena-classes.json`, 6 h |

- Checked live 2026-10-25: 1219 cards (147 without a score), 11 classes. The card list is one all-classes slice
  (`arena_class: ALL`); the server builds it from `ArenaGameTypeFilter=BGT_UNDERGROUND_ARENA`, `LAST_4_DAYS`.
- Tier letters follow the site (`server/hsreplayArenasmith.ts` of hearthpulse.net): S 53+, A 45+, B 35+, C 24+,
  D 10+, else E. Class win-rate tiers: S 54%+, A 50%+, B 46%+, C 42%+, D 38%+, else E (`ArenaDraft.TierOfWinRate`).
- Code: `HearthpulseData.LoadArenaMetaAsync` (`Api/HearthpulseData.Arena.cs`) -> `Arena/ArenaMeta`. Hosts load it with
  the community stats (`App.Arena.cs`, `AppHost.Arena.cs`) and pass it to `GameSession.SetArenaMeta` and
  `MainViewModel.SetArenaMeta`. Offline the stale cache is used; with nothing at all the panel says so and the deck
  and curve still work.

### Endpoints the server does not have yet

- **Per-class card scores**: a neutral card has one score for every class today. A class slice (e.g.
  `datasets/hsreplay_arena_cards_advanced?class=MAGE` or a `by_class` map of scores) would make the badges class-aware.
- **Normal Arena vs Underground**: the published slice is Underground only; a normal-Arena slice (or a `mode`
  parameter) is needed to pick the one matching the run.
- **A small cards endpoint** (`v1/arena/cards?fields=id,score`): the app trims the 1.7 MB dataset itself.

## Draft state

- **Arena.log** (`[Arena]` is already in `log.config`; `LogWatcher` now tails it): `ArenaDraftTracker` reads the
  client's DraftManager lines - `DraftManager.OnChosen(): hero=HERO_xx` (new run), `Draft Deck ID: n, Hero Card = X`
  (the deck is listed again after it), `Draft deck contains card X`, `Client chooses: Name (ID)` (a pick; a HERO_ id
  is the hero), `SetDraftMode - MODE` (`NO_ACTIVE_DRAFT` clears). **Not yet checked against a log of the current
  client**: no Arena.log was kept on the development machine (2026-10-25). Check it on the first real draft.
- **The offer is not in the logs**: `HearthstoneMemory.GetArenaDraftChoices` reads `DraftDisplay.s_instance.m_choices`
  and each choice's `m_cardID` (read only, only while the scene is `DRAFT`, every second; a choice without a card id
  means the offer is being dealt and nothing is shown). Field names are unverified on the current client, like the
  log lines. The offer is face up on the player's screen: no hidden information.
- After a pick the offer stays in memory for a moment: `GameSession.Arena.cs` hides an offer equal to the one on screen
  at the last pick until a different one arrives.
- Out of scope here: Underground redraft and packages (A4), Discover scoring in Arena games (A5), synergy (A3).

## Layout and licences

- The badge row is centred on the play area left of the client's deck list with the anchors of HdtArenaHelper's
  `ArenaOverlayWindow` (MIT, (c) 2026 Alessandro Colace): 4:3 safe area, play area its left 3.25/4.25, cards 0.26 of
  that width apart, heroes 0.29 and 0.05 of the height lower. Adapted in `OverlayViewModel.Arena.cs` of both UIs
  (licence in the file header and in `THIRD-PARTY-NOTICES.txt`); the panel's default margin `0,566,339,0` comes from
  the same numbers. Everything else is our own code; HDT and Firestone were not used.
- Render scenes: `overlay-arena-hero`, `overlay-arena-pick`, `overlay-arena-pick-scale160` (a made-up Warlock draft
  from the real scores, `Arena/ArenaDraftScene.cs`) and `main-arena-deck` (`DevRender.Arena.cs`,
  `OffscreenRenderer.Arena.cs`).
- Tests: `tests/Hearthpulse.Tests/ArenaDraftTests.cs` (fixtures `Fixtures/Arena`, trimmed live answers).
