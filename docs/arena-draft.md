# Arena draft helper

Backlog A2 (data and card scores), A1 (draft helper on the overlay), W2 (community Arena numbers in the app). Both UIs.

## What the player sees

- **Draft screen, overlay panel `arena-picks`**: a badge over each of the three offered cards with its ArenaSmith
  score and tier colour; the highest score gets a gold border, a star and "Best pick". Under the score the badge says
  where the card would sit in the deck: its cost and how many cards of the deck share that curve bucket
  ("3 mana · 4 in deck"), and its type with the deck's count of that type ("Spell · 5 in deck"). A legendary that
  brings a package (Underground) is scored as the average of the whole package and says "+2 cards with it". At the
  hero pick the badges show the class win rate (tier from the win rate), the class's place among all classes and the
  7+ wins share; the best class is marked. At the hero power pick there are no badges (no numbers exist for it).
- **Overlay panel `arena-draft`** (left edge): run class (or "Choose a hero" / "Choose a hero power"), the run's mode
  ("Underground Arena" / "Normal Arena") once the client told it, picks `n/30`, average ArenaSmith score of the deck so
  far with its tier, mana curve 0..7+ with the buckets of the offered cards lit, the deck's card types
  ("12 minions · 5 spells · 1 weapon"), class win rates at the hero pick (the offered classes in gold), the cards
  picked by cost with their scores. Attribution line "Scores: Hearthpulse data"; on a normal-Arena run a second line
  says the scores come from Underground Arena games (the only slice the server has, see Data).
- **Arena page** (main window, My Stats Arena): each run row shows "Deck score N" when the run saved a deck code; a
  "Deck score" card for the latest run (average, tier, curve, three strongest and weakest cards); a "Class win rates"
  card with the community numbers.
- Setting: `AppSettings.ShowArenaDraft`, **beta, off by default**; Settings → Tracker in both apps, labelled
  "(beta)". Off, the draft screen is not read from memory at all (`GameSession.SetArenaDraftEnabled`, hosts call it
  next to `SetCardPoolEnabled`); Arena.log is still followed so turning it on mid-draft knows the deck. While it is a
  beta the tracker logs to `hearthpulse.log` every Arena.log line it recognized with the resulting state
  (`Arena draft: recognized "..." -> hero ..., hero power ..., deck ..., N cards, mode ...`), every change of the
  offer read from memory (`Arena draft: offer read from memory: A, B (+P Q), C; Underground Arena` /
  `no offer read from memory`) and the package cards added after a pick: compare them with the screen.
  In the UI the numbers are credited to "Hearthpulse data" only. Both panels hide outside the `DRAFT` scene and take
  no clicks (`IsHitTestVisible="False"`, no `OverlayHit.Interactive`), so the cards stay clickable. Nothing is chosen
  or clicked for the player: the helper only shows numbers.

## Data (Hearthpulse data API, no token)

| Call | Used for | Cache |
|---|---|---|
| `GET datasets/hsreplay_arena_cards_advanced` | `data.structured.cards[]`: `id`, `dbfId`, `score` (ArenaSmith), `win_rate`, `pick_rate`, `times_played`; the slice's game type from `structured.source.api_url` (`ArenaGameTypeFilter=BGT_UNDERGROUND_ARENA`) | `cache/api/arena-cards.json`, 6 h; only those fields and `game_type` are kept (the answer is about 1.7 MB, the list is published twice) |
| `GET v1/arena/classes?limit=20` | `data[]`: `class`, `win_rate`, `pick_rate`, `pct_7_plus`, `num_drafts` | `cache/api/arena-classes.json`, 6 h |

- Checked live 2026-10-25 and again 2026-10-26: 1219 cards (147 without a score), 11 classes. The card list is one
  all-classes slice (`arena_class: ALL`); the server builds it from `ArenaGameTypeFilter=BGT_UNDERGROUND_ARENA`,
  `LAST_4_DAYS`. `structured.by_class` holds only card counts per class (DEATHKNIGHT 355, DRUID 336...), no scores.
- **Legal status**: the ArenaSmith scores are HSReplay numbers scraped by the server; the legal report
  `LEGAL-HeartLens-card-choices.md` (L-B1, L-M2) puts them under the same HearthSim ToS risk as the other HSReplay
  data. They stay until the owner decides; no further HSReplay source is added for Arena.
- Tier letters follow the site (`server/hsreplayArenasmith.ts` of hearthpulse.net): S 53+, A 45+, B 35+, C 24+,
  D 10+, else E. Class win-rate tiers: S 54%+, A 50%+, B 46%+, C 42%+, D 38%+, else E (`ArenaDraft.TierOfWinRate`).
- Code: `HearthpulseData.LoadArenaMetaAsync` (`Api/HearthpulseData.Arena.cs`) -> `Arena/ArenaMeta` (`Underground` =
  the slice's mode). Hosts load it with the community stats (`App.Arena.cs`, `AppHost.Arena.cs`) and pass it to
  `GameSession.SetArenaMeta` and `MainViewModel.SetArenaMeta`. Offline the stale cache is used; with nothing at all the
  panel says so and the deck and curve still work.

### What the server has (`GET v1/sources?category=arena`, 2026-10-26)

| Source | What it holds | Used |
|---|---|---|
| `hsreplay_arena`, `hsreplay_arena_class_pages_firecrawl` | class win rate, pick rate, 7+ share, drafts (what `v1/arena/classes` serves; `source_id` picks one of them) | yes, via `v1/arena/classes` |
| `hsreplay_arena_cards_advanced` | ArenaSmith score per card, Underground, all classes | yes |
| `hsreplay_arena_legendaries` | 134 legendary packages (`groups[]`: `key_card`, `cards[]`), Underground | no: the client gives the package of each offer itself |
| `hsreplay_arena_winning_decks` | winning decks (12 MB) | no |
| `heartharena_tierlist` | **per-class card scores**: 11 classes + `any`, about 355 cards each, `score` 1..145, `tier_id` terrible..great; 178 of 316 neutral cards score differently by class | no, waiting for the owner's decision: a scrape of a commercial site whose terms of use were not checked; the same legal question as the HSReplay data (`reference/reviews/LEGAL-HeartLens-card-choices.md`, L-B1/L-M1). Check HearthArena's ToS before wiring it |
| `firestone_arena_cards_normal`, `firestone_arena_cards_underground`, `firestone_arena_legendaries_*` | the only **normal-Arena** card slice, and Underground | never: Firestone data has no permission (hearthpulse-data-sources) |

### Endpoints the server does not have yet

- **Per-class card scores from a source we may show**: a neutral card has one score for every class today
  (HearthArena has them, see above).
- **Normal Arena vs Underground** from a permitted source: the published HSReplay slice is Underground only; a
  normal-Arena slice (or a `mode` parameter) would let the badges match the run. The panel says which slice it shows.
- **A small cards endpoint** (`v1/arena/cards?fields=id,score`): the app trims the 1.7 MB dataset itself.

## Draft state

- **Arena.log** (`[Arena]` is in `log.config`; `LogWatcher` tails it, also when the file appears mid-session):
  `ArenaDraftTracker` reads the client's DraftManager lines: `DraftManager.OnChosen(): hero=` (new run), `DraftManager.OnChosen(): heroPower=` (the hero power pick, new in the
  current client), `DraftManager.OnChoicesAndContents - Draft Deck ID: {0}, Hero Card = {1}` (the deck is listed again
  after it), `DraftManager.OnChoicesAndContents - Draft deck contains card {0}`, `Client chooses: {0} ({1})` (a pick)
  and `SetDraftMode - ` with the modes `NO_ACTIVE_DRAFT` (clears), `DRAFTING`, `ACTIVE_DRAFT_DECK`, `IN_REWARDS`,
  `REDRAFTING`. **Not yet seen in a written Arena.log**: no draft was played on the development machine.
- Heroes, hero powers and cards are told apart by the card data (`HERO` not collectible, `HERO_POWER`, anything else;
  a collectible hero card is a card), else by the id (`HERO_08`, `HERO_08bp`): `ArenaDraftTracker.KindOf`. A hero
  power is never counted as a deck card. A hero chosen after cards were taken, or another hero than the current one,
  starts a new run even when its `OnChosen` line is missing (review M1: the old deck carried over).
- **The offer is not in the logs**: `HearthstoneMemory.GetArenaDraftChoices` reads `DraftDisplay.s_instance.m_choices`,
  each choice's `m_cardID` and `m_packageCardIds`, and `m_choicesAreUnderground` (read only, only while the scene is
  `DRAFT` and the helper is on, every second; a choice without a card id means the offer is being dealt and nothing
  is shown). The values are not yet seen on a live draft. The offer is face up on the player's screen: no hidden
  information - to be confirmed for a legendary's package (below).
- After a pick the offer stays in memory for a moment: `GameSession.Arena.cs` hides the offer that **holds the chosen
  card** until a different one arrives. When the memory read already has the next offer as the log line arrives, that
  offer stays (no blank pick); when no offer was known yet, the first one read that holds the chosen card is the
  stale one. The cards of a picked package are added to the deck right away (the log names only the clicked card).
- The run's mode comes from the offer and is kept while the draft screen is open.
- Out of scope here: Underground redraft (A4: `REDRAFTING` is followed only as a mode; the deck is right again once
  the client lists it), Discover scoring in Arena games (A5), synergy (A3).

## Before the beta label goes

1. One real draft of each mode with the helper on: `hearthpulse.log` lines match the screen (hero, hero power,
   each offer, the package of a legendary, the mode), picks reach 30, the deck matches the client's list.
2. **The package cards are on screen before the pick** (legal report `LEGAL-HeartLens-claude-arena-draft-v1.md`,
   L-M1): the badge scores the whole package and says "+N cards with it". If the client shows the package only after
   the pick (or only part of it), use `m_packageCardIds` only once the pick is made, never on the badge.
3. Keep that Arena.log as a test fixture in place of the synthetic lines; name the log, not anything else, as the
   source of the line formats.
4. The owner decides on the HSReplay and HearthArena data (legal reports, L-M2) and on turning the helper on by default.

## Layout and licences

- The badge row is centred on the play area left of the client's deck list with the anchors of HdtArenaHelper's
  `ArenaOverlayWindow` (MIT, (c) 2026 Alessandro Colace): 4:3 safe area, play area its left 3.25/4.25, cards 0.26 of
  that width apart, heroes 0.29 and 0.05 of the height lower. Adapted in `OverlayViewModel.Arena.cs` of both UIs
  (licence in the file header and in `THIRD-PARTY-NOTICES.txt`); the panel's default margin `0,566,339,0` comes from
  the same numbers. Everything else is our own code; HDT and Firestone were not used.
- Render scenes: `overlay-arena-hero`, `overlay-arena-heropower`, `overlay-arena-pick` (a normal-Arena run, so the
  slice note shows), `overlay-arena-pick-scale160`, `overlay-arena-package` (an Underground legendary with its package)
  from a made-up Warlock draft with the real scores (`Arena/ArenaDraftScene.cs`), and `main-arena-deck`
  (`DevRender.Arena.cs`, `OffscreenRenderer.Arena.cs`).
- Tests: `tests/Hearthpulse.Tests/ArenaDraftTests.cs` (fixtures `Fixtures/Arena`, trimmed live answers) and the
  two-host contract in `tests/Hearthpulse.Presentation.Tests/ViewModelContractTests.cs`.
