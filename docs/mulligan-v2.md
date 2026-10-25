# Mulligan guide v2 — research and design

Goal: an equivalent of HSReplay's newer mulligan overlay in Hearthpulse, fed by our own API
(`api.kolodahearthstone.com`). This page covers what HSReplay shows, what our data can honestly support, what is
missing, and which server endpoints would close the gap. Checked on 2026-10-23.

## 1. HSReplay "Mulligan G-V2"

The feature is called **Mulligan G-V2**. It shipped as a Premium feature in Hearthstone Deck Tracker v1.51.1
(2026-03-26), with a free trial of 5 games a week. There is no blog article. These facts come from the landing/FAQ page and the open-source
clients:

- HSReplay landing/FAQ page: https://hsreplay.net/mulligan-gv2/ (its webpack bundle `mulligan_gv2.*.js` holds the screenshots).
- HDT release: https://github.com/HearthSim/Hearthstone-Deck-Tracker/releases/tag/v1.51.1
- UI strings: https://github.com/HearthSim/HDT-Localization/blob/master/Strings.resx (keys `MulliganGV2_*`)
- HSTracker client models: `HSTracker/HSReplay/Data/MulliganV2Data.swift` and `MulliganV2Params.swift` (read only for the
  request/response *shape*; no code was copied).

What it shows:

| Element | Meaning |
|---|---|
| Decision arrow | A gauge per offered card that runs from "Replace" (red) to "Keep" (green). The marker sits at the **probability that top players keep this card** in exactly this context. **V2 shows no win rates.** |
| Shadow bar | The range between the lowest and the highest keep probability over all combinations of the other offered cards, i.e. how much the rest of the hand can move this card. |
| Contextual / dynamic keep rate | Tooltip. "Contextual" means the whole hand is kept. "Dynamic" means some cards are marked to be replaced, and the value says how much more or less often the card is kept in that case. |
| Synergy indicator | Icons above a card: another card's portrait, the opponent's class or first/coin, with 1..N green up or red down arrows. Example tooltip: "Top players keep A 21% more often when B is also offered (base 62% → adjusted 83%)". Tip types: `KEPT_MORE/LESS`, `..._2ND_COPY`, `..._VS_THIS_OPPONENT`, `..._GOING_FIRST/SECOND`. |
| Duplicates | Handled explicitly. The leftmost copy is the first copy. When one copy is toggled, the other becomes the first. Second copies have their own tips. |
| Mana curve | Not shown explicitly. It is presumably inside the conditional keep rates. |
| Data warnings | "Insufficient data", "Low data", and "thin data for this card combined with other offered cards". |

How it is computed: the client POSTs `/api/v1/mulligan_v2/overlay/` with the deckstring, `player_class`,
`opponent_class`, `player_initiative` (FIRST/COIN), region, star level, game/format type and the offered cards. The answer
has, per hand position, a list of **justifications**: `{"[dbf, dbf…]": keep_probability}`. Each key is one combination of the
*other* offered cards that you also keep, plus the tips. The client looks up the key that matches the cards currently
marked as kept, so the guide reacts to your toggles without another request. The model and the definition of "top
players" are not published. The guide covers Standard only.

The previous overlay (V1, still the free one) showed, per card: **Keep rate** (share of times kept when offered),
**Mulligan WR** (win rate of games where the card is in the opening hand after the mulligan, kept or drawn) and a hand
rank, with optional "vs class" and "going first / on coin" filters
(https://articles.hsreplay.net/2020/07/23/card-mulligan-data/, https://articles.hsreplay.net/2019/04/08/player-initiative-filter/).

## 2. What our API has

Server source: https://github.com/Manacost-Labs/api.kolodahearthstone.com (read at commit `0e78c71`). All mulligan
data comes from `app/hsreplay_archetypes_db.py`:

- HSReplay queries: `archetype_popularity_distribution_stats_v2`, `head_to_head_archetype_matchups_v2`,
  `list_decks_by_win_rate_v2`, and per archetype `single_archetype_mulligan_guide_v2` and
  `single_archetype_stats_over_time_v2`. Parameters: `RANKED_STANDARD`, `LEGEND`, `REGION_EU`. The summary covers 7 days;
  decks and mulligan cover 30 days.
- Only `series.data["ALL"]` of the mulligan answer is stored (lines 677–704). Raw answers are not kept
  (`archetype_snapshots.raw_json` only holds row counts).

Endpoints used by the app:

| Endpoint | What we take |
|---|---|
| `GET /api/db/archetypes?game_type=RANKED_STANDARD&rank_range=LEGEND` | `archetype_id`, `name`, `player_class`, `total_games`, `win_rate` |
| `GET /api/db/archetypes/{id}/decks?limit=200` | `raw_deck_list` = `[[dbf, count], …]`, `total_games` (for deck → archetype matching) |
| `GET /api/db/archetypes/{id}/mulligan?display_only=false&limit=250` | per card: `card_id`, `dbf_id`, `cost`, `times_presented_in_initial_cards`, `keep_percentage`, `times_in_opening_hand`, `opening_hand_winrate`, `winrate_when_drawn`, `winrate_when_played`, `avg_turn_played_on` |
| `GET /api/db/archetypes/{id}/matchups` | `opponent_class`, `total_games`, `win_rate` per opponent archetype |

The HSReplay mulligan rows (checked live 2026-10-25) also carry `times_kept`, `times_card_drawn`, `times_card_played`,
`avg_turns_in_hand` and the card names. `rank_range` / `game_type` are accepted, but the server only collects
`LEGEND` / `RANKED_STANDARD` (EU): any other value answers with the Legend snapshot. There is still no coin or class split here.

### 2.1 HSGuru mulligan slices (new, 2026-10-25)

`GET /v1/hsguru/mulligan?archetype=<HSGuru name>&format=standard&rank=legend&coin=on_coin&opponent_class=any` (also
`/slices` to list what is stored, `/history`, `/coverage`). Answer `{"data": {"rank", "period", "coin", "opponent_class",
"games", "winrate": null, "rows": [...]}, "meta": {...}}`, one row per card: `card_id`, `dbf_id`, `mulligan_count` /
`mulligan_impact` (offered), `kept_count` / `kept_impact`, `drawn_count` / `drawn_impact`, `not_drawn_count` /
`not_drawn_impact`. Impacts are win rate points against the deck's own win rate *in the same slice*; rows with tiny
counts are present (a card offered once shows +58.8), so the client needs minimum counts.

- Slices: `rank` all, legend, top_legend, top_5k, diamond_to_legend, diamond_4to1; `coin` any_player, going_first,
  on_coin; `opponent_class` any or a class. The server keeps the coin and the class **apart** (a slice sets one of them),
  collects the coin from 3,000 archetype games and classes from 10,000 (`/coverage` settings); `period` defaults to the
  current expansion. A slice not collected answers 404 "This slice is not collected yet".
- Derived: keep rate = kept / offered; win rate when replaced = (offered × offered impact − kept × kept impact) / (offered − kept).
- Caveat seen on real rows: kept / replaced numbers carry a strong selection bias for rarely kept cards (Zoolock on the
  coin: Vaultkeeper kept 16% of the time, +2.6 kept vs −6.8 replaced). "Kept" hands are good hands. That is why the guide
  does **not** decide from kept vs replaced; it uses the *offered* impact, which does not depend on the choice, for the
  coin and class adjustments, and shows kept / replaced / drawn only in the tooltip and the second line.
- Some decks have their own slices (`deck_id`); the app does not map decks to HSGuru deck ids yet.

Not used:

- `/api/db/cards/trends` needs a `card_name` and only returns popularity.
- `/v1/hsguru/archetypes/analysis` has `class_matchups` and per-card `mulligan_impact` / `kept_impact` (Legend, past
  week): the source of the HSGuru guide (Wild, archetypes HSReplay lacks); the slices above add the splits.

Searched and **not found** anywhere on the server: card pairs / "kept with", per-copy stats and HSReplay's G-V2 keep model.
The only card-to-card graph is Vicious Syndicate's radar (`app/vicious_syndicate.py`). Its edges come from deck
co-occurrence, not from mulligan results.

Observations on the real data (Quest Priest, archetype 56, 250 rows):

- The quest (`TLC_817`) starts in hand: kept 99.8%, in the opening hand 105,762 times. That is effectively the number of
  games, and its opening-hand win rate (51.06%) is the deck's win rate over the mulligan window.
- Σ `times_in_opening_hand` / games = 3.46 (≈ 3 cards going first, 4 on coin). So the opening-hand-weighted mean of
  `opening_hand_winrate` (50.65%) is a usable baseline from the same sample. The archetype's `win_rate` (53.59%) comes
  from a different window (7 days vs 30 days) and must not be mixed in.
- The counts do not say whether two copies count once or twice. 3.46 < 3.5 hints at "once per game", but this is not
  proven. The app treats each stat as "at least one copy in hand".

## 3. What Hearthpulse computes, and how honest each part is

Code: `src/Hearthpulse.Core/Tracking/MulliganGuide.cs` (maths), `MulliganGuideSource.cs` (background loading; disk cache
through `HearthpulseData`, 6 h), `Mulligan.cs` (entry point, Firestone fallback), `MulliganReplay.cs` (render checks).
UI: `Overlay/MulliganPanel.xaml` (WPF) / `MulliganPanel.axaml` (Avalonia), view model `OverlayViewModel.Mulligan.cs`.
Since 2026-10-25 a bar **over** each offered card in the HSReplay style (it replaced the badges under the cards): a large
Keep / Replace with its icon and colour, the edge in points ("+1.9", `MulliganCardStat.Edge`), a gauge from replace (left)
to keep (right) whose fill grows from the middle (4 points fill a half), coloured Negative / Accent (close call, under
half a point) / Positive, and a small second line "kept 61% · drawn −1.3" (keep rate, win rate when drawn against the
deck's). Small marks for a synergy, a 2nd copy, a late card; click = keep or replace. The summary line ("Expected impact
of your keeps: +2.1%", source and slice in small type) is its own panel `mulligan-summary` under the cards (top at
700 of 1080, above the Confirm button): over the cards it ran into the opponent's hand markers at a large overlay scale. Win rate,
sample, the coin / class adjustments, kept / replaced and curve notes are in the tooltips. The bars are a 1020-unit row
centred on the screen (3 card slots 340 apart, 4 slots 255 apart) whose **bottom** is at 300 of 1080, just over the
card row of the mulligan screen, so the panel grows upwards with the overlay scale. These are units of the game's
1080-high screen, not of the overlay: the player's overlay scale only sizes the bars (`MovablePanel.GameAnchored`,
`OverlayViewModel.MulliganRowWidth`). MovablePanel `mulligan-bars` (a new id, so an old placement of the strip does not
apply) and `mulligan-summary` can be moved and scaled in layout mode. It shows while the local MULLIGAN_STATE is INPUT or DEALING and goes at
WAITING, also without numbers (`MulliganView.NoStats`: guide still loading, deck unknown, no archetype); without
numbers every card starts kept, so no bar reads as "replace".

| Feature | Status | How |
|---|---|---|
| Archetype for the player's deck | **data** | Each archetype of the class gets two scores: (a) the best overlap Σ min(count) of the player's 30 cards with one of its HSReplay lists, which must reach 50%; (b) the deck's *presence* in its mulligan rows (each card weighted by offers relative to the archetype's 10 most offered cards), which must reach 40%. HSReplay's own lists score 0.55–0.6 in their archetype and 0.1–0.2 elsewhere. (b) exists because some archetypes have **no lists** on the server (Herald Death Knight: 51% of DK games, 0 decks; Herald DH). The widest margin over its bar wins. |
| Keep rate, win rate in opening hand | **data** | `keep_percentage`, `opening_hand_winrate` (HSReplay V1 metrics) |
| Delta per card | **data** | Δ = opening-hand WR − baseline. The baseline is the opening-hand-weighted mean of the same sample (see above). |
| Keep / Mulligan label | **data + model** | Keep when Δ beats the expected Δ of a replacement card (the copy-weighted mean Δ of the cards still in the deck), ties included: the edge Δ − Δ(replacement) is the bar's number and the call follows its sign. Until 2026-10-25 a 0.5-point margin favoured keeping, so a card worse than its replacement could read "Keep −0.6" (a real Coin Rogue hand on the coin: Whip −0.65 and Preparation −0.56 were kept next to a −0.68 replacement, and the badge showed the delta against the deck's average, not against the replacement). Gaps under half a point are now only drawn neutral (`MulliganGuide.CloseCall`). Cards without data count as Δ = 0. |
| Opponent class | **data (HSGuru slices) + baseline** | The *baseline* moves by the matchups of this archetype grouped by `opponent_class`. Each card's Δ moves by its offered impact against that class minus its offered impact in all games, from the HSGuru slice of the same rank (Legend, else all ranks; slices under 2,000 games and cards offered under 200 times are skipped). |
| Going first / coin | **data (HSGuru slices)** | The same shift from the `going_first` / `on_coin` slice. Both shifts add up (the server keeps the two splits apart). The Firestone fallback also moves the baseline with `FirstWinRate` / `CoinWinRate`. |
| Keep rate, kept / replaced / drawn | **data (HSGuru slices)** | From the most specific slice that has the card (class, then coin, then all games; Legend first): keep rate, win rate when kept and when replaced, win rate when drawn, all against the deck's. Without a slice: HSReplay's keep rate and `winrate_when_drawn` − baseline. Shown, not used for the call (selection bias, section 2.1). |
| Duplicates | **heuristic (labelled)** | The stat describes holding the card, so a second copy adds nothing measurable. In the estimate it counts 0 and blocks a replacement. Recommendation: keep the second copy only for an early play (expected turn ≤ 3) that players keep ≥ 50% of the time. |
| Mana curve | **heuristic (labelled), warnings only** | Early = cost ≤ 2 or `avg_turn_played_on` ≤ 3. Late = cost ≥ 5, or cost 4 and usually played on turn 6+. The average turn alone is misleading: HSReplay counts late plays too, so 1-mana Void Soul averages turn 4.4. Warnings: no early play kept; top-heavy (≥ 2 late cards kept); both copies of a non-early card kept. A late card gets a note on its plate. The numbers do not change. |
| Synergies | **no data → off** | `GuidePair` is ready for a future endpoint (interaction = pair WR − additive estimate). With no pairs the panel says so, and no synergy is ever invented. |
| Impact of your keeps | **model + heuristic shrink** | E[WR \| keep K] ≈ baseline + (Σ Δ(first copies kept) + (cards replaced) × Δ(replacement) + kept pair interactions) / √(hand size). A plain sum overshoots, because the deltas are marginal and share causes: a real Void DH hand of three strong cards came out at 71%. The √ shrink brings that to 63% and leaves every comparison between keep sets unchanged. The panel compares the estimate with the recommended keeps and with a full mulligan. Clicking a plate toggles that card, and the line updates. |
| Slices loading | **data** | `MulliganGuideSource` matches the deck to an HSGuru archetype too (by its lists), loads the all-games slices with the guide, and the coin and class slices once the mulligan knows them (`For(..., opponentClass, goingFirst)`, background, `Changed` again). Legend and all ranks, cached 6 h like every answer; 404s are remembered. |
| Sources in order | **data** | 1. HSReplay archetype the deck matches (Standard). 2. HSGuru archetype whose lists the deck matches, with its HSGuru analysis (Legend, past week; Standard and Wild): kept games stand for the opening hand, the class results give the baseline and the class shift. 3. `ConstructedMeta` (docs/constructed-meta-api.md): its archetype by core cards, else the class pool. A guide that cannot compute falls through to the next. The cost replaces the average turn when the source has none. |
| Offered cards | **data** | Cards in the local hand during `MULLIGAN_STATE` INPUT/DEALING, minus the coin. The coin has cosmetic ids, such as `JAIL_COIN3` in a real log, so it is recognised by the name it shares with `GAME_005`. |

The additive model is consistent with how the deltas are defined. If a hand's win rate is β₀ + Σ β(card) over a hand of fixed size, then the
observed Δ of a card is about β(card) − mean β, and the baseline is β₀ + E[Σ β]. Summing Δ over the kept cards and Δ(replacement)
over the replaced ones gives back the model's expectation. Its weakness is correlation: cards that are good in the same
matchups are counted twice. That is why the overlay presents it as an estimate.

### Checks (2026-10-23)

- 2026-10-25: `MulliganGuideTests` gained the bug case (a card 0.3 under its replacement is replaced, edge = call on real
  hands), coin / class shifts on synthetic slices and the slice parser on trimmed live Zoolock answers
  (`Fixtures/Mulligan/hsguru-mulligan-zoolock-*.json`); `ApiLoadTests` loads the coin and class slices lazily over a
  fake server and reads them back from the disk cache. Renders: `overlay-mulligan(-4)(-scale60|-scale160).png` and
  `overlay-mulligan-nodata.png` in both apps.
- Tests: `tests/Hearthpulse.Tests/MulliganGuideTests.cs` (12) on fixtures trimmed from real answers
  (`Fixtures/Mulligan`: Demon Hunter and Priest archetypes, lists, mulligan rows, matchups). The suite has 540 tests;
  only the 2 known failures remain.
- Live run: the newest four Standard games in `D:\Hearthstone\Logs` (copies), against the real API:
  - Rogue matched Coin Rogue.
  - Two Demon Hunter decks matched Void Demon Hunter.
  - The newest game, a Death Knight homebrew, matched nothing: at best 40% of an Egg DK list, presence 36%. It
    fell back to Firestone, as designed.
- Renders: `--render-ui` writes `overlay-mulligan.png` (3 offered cards) and `overlay-mulligan-4.png` (on the coin)
  in both apps, from the newest kept logs.

## 4. Proposed server endpoints

All derive from HSReplay data the server already downloads or can download with one more parameter. Response shapes
follow the existing bare `/api/db/*` style.

### 4.1 Mulligan by opponent class and initiative (cheapest)

Since 2026-10-25 the HSGuru side has this (section 2.1) and the guide uses it. For the HSReplay numbers it is still open:

`single_archetype_mulligan_guide_v2` answers with one series per filter. Today only `ALL` is kept. Proposal: store every
series, and add the HSReplay `PlayerInitiative` parameter (FIRST/COIN) as two more fetches.

```
GET /api/db/archetypes/{id}/mulligan?opponent_class=WARLOCK&initiative=COIN&display_only=false&limit=250
→ { "snapshot": {...}, "opponent_class": "WARLOCK", "initiative": "COIN", "mulligan": [ ...same row fields... ] }
```

Table change: `archetype_mulligan` gets `opponent_class TEXT NOT NULL DEFAULT 'ALL'` and
`initiative TEXT NOT NULL DEFAULT 'ALL'`, with PK `(snapshot_id, dbf_id, opponent_class, initiative)`.
Client: `MulliganGuideSource` would request the opponent's class once the opponent hero is known, and fall back to `ALL`.

### 4.2 Card pairs in the opening hand (synergies)

HSReplay does not publish pair stats for archetypes. They must come from replays the server can see (for example the
Hearthpulse users' own games, which `docs/accounts-api.md` already plans to upload), or from a bulk export.

```
GET /api/db/archetypes/{id}/mulligan/pairs?min_games=200&limit=500
→ { "snapshot": {...}, "pairs": [
      { "card_a": "CATA_308", "card_b": "JAIL_912",
        "games_together": 1830,            // both in the opening hand after the mulligan
        "winrate_together": 58.4,
        "kept_together": 0.71 }            // share of hands offering both where both were kept
  ] }
```

The client computes interaction = `winrate_together` − (baseline + Δa + Δb) and highlights a pair when |interaction| ≥ 1.5
points with ≥ 200 games (`MulliganGuide.MinPairGames`).

### 4.3 Per-copy stats

```
GET /api/db/archetypes/{id}/mulligan?copies=true
→ rows gain "copy": 1 | 2  (2 = both copies in the opening hand), each with times_in_opening_hand and opening_hand_winrate
```

This would replace the duplicate heuristic with data.

### 4.4 Keep model like G-V2 (optional, large)

HSReplay's justifications (keep probability for each combination of other kept cards) need replay-level mulligan data.
If the server ever has it, `POST /v1/mulligan/hand` with `{deck_code, opponent_class, initiative, offered: [dbf…]}` →
`{positions: [{dbf, justifications: {"[dbf,…]": p}, tips: [...]}]}` would let the client mirror G-V2 directly.
