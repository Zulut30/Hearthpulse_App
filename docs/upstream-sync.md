# Upstream sync (HeartLens → Hearthpulse)

Hearthpulse grew from a snapshot of [Dev-NotAqua/HeartLens](https://github.com/Dev-NotAqua/HeartLens); the author
allowed porting their code. This file records what was taken from upstream, what was adapted or left out and why,
and how to repeat the sync.

## Base

- Our first commit `b0e6b1c` is upstream `d26cb30e` "Release 0.7.8: smoother, cleaner window". The trees match
  except for `README.md` and `installer/RELEASE_NOTES.md` (not kept here) and the executable bit of the installer
  scripts. `42615b9` (0.7.7) and `955c07d` (0.7.9) differ in 18 and 27 files, so `d26cb30e` is the base.
- Synced up to upstream `e8b33b74` (merge of PR #9; its last real commit is `84edebe`). Upstream merge commits add
  no changes of their own and were skipped.
- Upstream later rewrote its history (root `52b5502` = `e8b33b74` + the 0.12 redesign); since then synced up to
  `3e5c1bc`, see "Upstream history rewrite" below.

## Porting rules

- One Hearthpulse commit per upstream commit, in order, with the upstream author as author, the upstream message,
  a "Hearthpulse port:" paragraph naming every adaptation, and `(cherry picked from commit <sha>)`.
- Rebranding, applied to paths and contents exactly as in our rebrand commit `baf731f`: `Hearthlens` →
  `Hearthpulse`, `hearthlens` → `hearthpulse`, `HEARTHLENS` → `HEARTHPULSE`, `Dev-NotAqua/HeartLens` →
  `Zulut30/IceCrowV2`. Installers and release assets are therefore `Hearthpulse-*`.
- Our decisions win: the updater looks at `Zulut30/IceCrowV2`; the legacy `Hearthlens` data folder is copied, not
  moved; the design follows `.claude/skills/hearthpulse-design`; Constructed meta and accounts stay ours.
- Upstream does not keep release notes in this repository's layout: `installer/RELEASE_NOTES.md` stays absent, so
  release-note hunks are dropped. (Note: `release.yml` reads `body_path: installer/RELEASE_NOTES.md`; that gap
  predates this sync.)
- Versions follow upstream (0.7.9 … 0.11.4), since the code now corresponds to upstream 0.11.4 plus our work and
  nothing has been released from `Zulut30/IceCrowV2` yet. Upstream bumped to 0.11.4 in `84edebe`.

## Commits

| Upstream | Ours | Subject | Status |
|---|---|---|---|
| `955c07d` | `9cf5405` | Release 0.7.9: stop downloading Firestone's stats | Adapted: version + overlay guards only |
| `ce9314e` | `ea9e60e` | Portable Windows zip in every release | Ported |
| `861af2b` | `676c2ee` | Stats pool: opt-in sharing of Battlegrounds results | Adapted |
| `ede63c7` | `6fb8319` | Release 0.8.0 | Ported (version) |
| `3f63a88` | `a657d59` | Ignore wrangler's local state in server/pool | Ported |
| `7f288b1` | `f0e18ef` | Release 0.8.1: stats pool goes live | Adapted: version only, no live pool |
| `5ae88cc` | `1bb8be5` | Stats pool v2: share the whole game, with consent asked again | Adapted |
| `f6228f8` | `f9c7329` | Cleaner UI with animated controls | Adapted: animations only |
| `1e54a0a` | `6f0d305` | Portable macOS and Linux versions | Ported |
| `66bf860` | `f441266` | Release 0.9.0 | Ported (version) |
| `2e229b1` | `5ebd29b` | Share picks, plays and recent games; one-row pool | Ported (merged with counters) |
| `c79ad52` | `078e9b7` | Release 0.10.0 | Ported (version) |
| `4bd8902` | `df0638d` | Serve the full Battlegrounds stats from our own pool | Adapted: pool only when set up; base data and importer removed |
| `db17dbc` | `e8a124f` | Blend shared games into the base Battlegrounds stats | Adapted (attribution) |
| `2db0ae3` | `1825031` | Rename the base stats importer | Adapted: importer and data removed |
| `59ad6fd` | `8874da7` | Remove the base stats importer | Adapted: importer and data removed |
| `23a550e` | `e6a3271` | Release 0.11.2 | Ported (version) |
| `23ffc3f` | `2ebbce3` | Show the full comp list in game | Ported |
| `84edebe` | `8d6b850` | Merge shared stats into every MMR bracket | Adapted (attribution) |

After the port, `main` (`2e8d9be`: device-flow sign-in, My Stats, deck and hand panels, Discover pool, counters
panel, Mulligan V2, Constructed meta from the Hearthpulse API) was merged in (`983439a`) without textual
conflicts. Constructed loading is main's (API only) in both hosts; Battlegrounds loading is the port's.

## Third-party data removed from history

Upstream `4bd8902` added `server/pool/import-firestone.mjs` and filled `server/pool/migrations/0004_meta.sql` with a
1 MB export of Firestone's Battlegrounds stats; upstream itself later stopped using that data because there was no
permission for it. Before this branch was pushed, its history was rewritten (`git filter-branch --index-filter`
over `main..` the branch): in every commit the importer (`import-firestone.mjs`, later `import-base-stats.mjs`) is
gone and `0004_meta.sql` only creates the empty `meta` table. The commit messages of `df0638d`, `1825031` and
`8874da7` are upstream's and still mention the importer. No blob of the export or the importer is reachable from
the branch (`git rev-list --objects`); the upstream remote still has them, so never push upstream refs here.

## Conflicts and how they were resolved

- `AppPaths.Root` (`ce9314e`, `1e54a0a`): portable copies use their own data folder (`PortableRoot()`, next to the
  exe or beside `Hearthpulse.app`); only an installed copy runs our `MigrateLegacy` copy of the `Hearthlens` folder.
- `App.xaml.cs` / `AppHost.cs` `LoadMetaAsync` (`861af2b`, `5ae88cc`, `4bd8902`, `db17dbc`, `2db0ae3`): upstream
  replaced the Firestone download with the pool. Ours (`74f0e26`): Firestone with the Hearthpulse API fallback is
  always loaded; when a pool is set up, `PoolClient.OverlayAsync` blends the shared games into it by sample size
  (comps, brackets and percentiles stay), and the pool's own meta is used only when nothing else loaded.
- `BattlegroundsMeta.Attribution`: upstream renamed it to the pool. Ours keeps the Firestone text and adds
  `PoolAttribution`; a meta built by the pool alone has `Source = PoolAttribution`, a merged one keeps its base's source, and the "N shared games" note
  (`84edebe`) is appended to `meta.Source` in both view models and the advisor.
- `MainWindow.xaml` Battlegrounds tabs (`861af2b`, `5ae88cc`): upstream hid Comps/Trinkets/Cards and the bracket
  picker. Ours keeps them (the data is still loaded) and takes upstream's "hide a tab with no rows" bindings.
  A meta without comps (the pool alone) fills the Comps tab with tribe rows and relabels it "Tribes" (`CompTabLabel`,
  `CompCaption`); otherwise the comp rows stay.
- Hero tier filter in both view models: `BattlegroundsMeta.MinHeroGames` (20 for pool-only stats, 50 otherwise; API
  fallback rows with no count are kept).
- `BattlegroundsTracker` (`2e229b1`): our counters and upstream's picks/plays are both kept.
- Pool tests (`5ae88cc`, `2e229b1`): consent tests set `PoolEndpoint`, since no pool is built in; a new test checks
  that consent alone shares nothing.

## Stats pool

- Client (`src/Hearthpulse.Core/Pool/PoolClient.cs`) and server (`server/pool/`, Cloudflare Worker + D1) are in
  the repository with the rebrand, otherwise as upstream wrote them.
- **Off by default.** `PoolClient.DefaultEndpoint` is empty, so `Available`/`Sharing` are false: nothing is sent,
  no pool stats are read, the consent banner and the Settings switch stay hidden. Upstream's deployed worker is not
  referenced anywhere (`7f288b1`'s address and D1 database id were not taken; `wrangler.toml` keeps the
  placeholder).
- **Configurable.** `PoolEndpoint` in `settings.json` (must be `https://`) turns it on. The player's consent flow is
  upstream's: banner on first start, a Settings switch, consent versions 1–3, live games and the last 30 days of
  Battlegrounds history sent only after agreeing to the full list.
- Where to point it (decision for the owner):
  1. **Our own Cloudflare Worker** from `server/pool` (`wrangler d1 create`, migrations, deploy; README there).
     Same anonymous data model as upstream, no code change in the app: set the default endpoint. Its `/v1/meta`
     stays empty (503) unless filled from a source we may redistribute; the app then blends the shared games into
     its own downloaded meta.
  2. **HearthPulse site ingestion** `POST /api/v1/tracker/events/batch` (spec in the site repo,
     `docs/specs/tracker-ingestion.md`). It is account-bound (OAuth device flow, `tracker.write`, per-user rows,
     `battlegrounds_match` events, 1–50 per batch, idempotent `eventId`), unlike the anonymous pool. Mapping
     `PoolGame` to a `battlegrounds_match` payload would need a new sender after the device-flow login lands, and
     the meta side (`/v1/meta`) would need its own aggregate endpoint on the site.
- Avalonia: upstream has no pool UI or sharing in the cross-platform app; it only reads pool meta. Same here.

## Battlegrounds meta

Upstream removed every Firestone download in 0.7.9, stating the data was used without permission, and later served
its own pool (seeded once from Firestone, then blended with shared games). Hearthpulse keeps Firestone (with the
Hearthpulse API fallback) as the working source until a pool or our own API replaces it, so users lose no tier
lists, comps, trinkets or pick ratings. The pool code path is in place and takes over as soon as a pool endpoint is
configured. The permission question upstream raised applies to us too and should be settled by the owner.

## UI (`f6228f8`)

Taken: the animated switch (knob slides and grows, track fills, tick fades in, hover halo), the press shrink on
buttons and icon buttons, the primary button's hover fade, the fading selected plate on segments and tabs and the
tab underline growing from the middle. Not taken: the flattening (Gilded Tavern gradients, gold halo, primary
button rim, scallop `Ornament`, rating chart glow, hero art behind page titles). The switch track fills with
`PrimaryFill`. `ThemeEngine` now rebuilds the theme with every `Duration`/`BeginTime` at zero when Windows'
animation setting is off, so control animations follow the `ClientAreaAnimation` guard. Avalonia has no
animations (unchanged, see the design skill's known gaps).

## Tests

- `ChoiceTrackingTests.Picks_and_plays_are_recorded_from_a_real_game` (upstream, `2e229b1`) reads the newest
  Battlegrounds log under `D:\Hearthstone\Logs`. It failed here because that game ended before any Discover,
  triple reward or trinket. `6dd4516` adds a deterministic test on synthetic lines
  (`A_discover_pick_and_a_play_are_recorded`) and makes the live check expect a pick only when a choice was offered.
- `ParserTests.Rank_display` (culture) and `RealLogTests.Battlegrounds_view_tracks_lobby_boards_and_combats`
  (machine-dependent) predate this branch and are fixed on `claude/quality-hardening-431c90` (`6c43c3c`), not here.

## Upstream history rewrite (October 2026)

Upstream force-pushed a new history: its root `52b5502` "Initial commit" (23 Oct 2026) is the old head `e8b33b74`
plus the 0.12 redesign (Fraunces/Manrope fonts, theme and palette, `Scallop`/`Ornament`/`Wave` controls, main and
overlay views, version 0.12.1; 64 files). The old upstream commits (`d26cb30e` … `e8b33b74`) are no longer on
`upstream/main`; the hashes in the table above still name them. The 0.12 redesign in that root is not ported: our
design follows `.claude/skills/hearthpulse-design` and our 0.12.0 is our own release.

Ported from the new history (branch `upstream-port-ee5db74-3e5c1bc`, same rules as above):

| Upstream | Ours | Subject | Status |
|---|---|---|---|
| `ee5db74` | `e0a215a` | Simulator accuracy, 64-bit overflow guards, odds clamps | Adapted: core + tests |
| `bd478d4` | — | Custom WPF installer, landing site, design preview | Skipped (installer/site) |
| `8d3bbeb` | — | Release notes clean-up | Skipped (release notes) |
| `096c4fb` | — | Release notes clean-up | Skipped (release notes) |
| `6c02a7a` | — | Custom setup wizard on Windows | Skipped (installer) |
| `3e5c1bc` | `014fda9` | Find the game's memory values at runtime | Adapted: `HearthstoneMemory.cs` only, stricter |

- `ee5db74`: `CombatSimulator` (zero runs, exact 100 % / 0 %, rates clamped and summing to 1), `Fight`/`Side` (stat
  arithmetic in 64 bits, clamped to `int`) as upstream. Not taken: the `OverlayViewModel` hunks in both UIs (the
  overlay keeps ">99.9%" / "<0.1%") and the `ChoiceTrackingTests` hunk (`6dd4516` already fixed that test our way).
  Tests taken: `AccuracyAndOddsTests` (12), `FiftyCombatDeepAccuracyTests` (1) and the 5,000-setup test of
  `MultithreadedFiveThousandSimulationsTests` (~2 s); its 16M-fight stress test (~42 s) is left out. Follow-up
  `510a5b2` (ours) clamps the aura deltas in `Fight.RefreshSide` the same way (`StatOverflowTests`).
- `3e5c1bc`: only `HearthstoneMemory.cs`; the version bump (0.12.3), installer, workflow, release notes and `site/`
  hunks are dropped. Upstream's fallbacks matched fields by substring and any plausible int, which could read fields
  we never read; ours fall back only to the same field name respelled (`CanonicalFieldName`: `m_`/`s_` prefix,
  `<X>k__BackingField`, underscores, case), drop `DiscoverRatingValue`, read only the solo/Duos rating fields in
  `DiscoverRating`, and match `Player`/`ZoneGameModeButton` by exact name. Every field read is one read before;
  the only new place is `DiscoverRating`. It runs only when the client has no `NetCacheBaconRatingInfo` class
  (not for an unrated 0/0 player), at most every 3 minutes, and reads the same solo/Duos rating fields on the one
  NetCache object whose class name has "Rating" and "Bacon"/"Battlegrounds"; none or several give null
  (`MemoryRatingLookupTests`). The leaderboard hover still falls back to `m_entity` when `m_playerHeroEntity` is null. New
  metadata-only reads: class field tables (`AllFields`) for the respelled-name fallback.
  `GetHoveredCardId` (our `InputManager` read) is unchanged.

## Repeating the sync

```
git remote add upstream https://github.com/Dev-NotAqua/HeartLens.git   # once
git config remote.upstream.tagOpt --no-tags                           # upstream tags would clash with ours
git fetch upstream
git log --oneline --no-merges --reverse 3e5c1bc..upstream/main        # what is new
```

Last ported upstream commit: `3e5c1bc` (new history; the next sync counts from it). Before the rewrite it was
`84edebe` (head `e8b33b74`). To keep conflicts real, port from a rebranded copy of
upstream: check out the last ported upstream commit in a scratch worktree, apply the rebrand rules above to paths
and contents, commit, then for each new upstream commit apply its `git diff <c>^ <c>` with the same `sed`
substitutions (`git apply --index`) and commit with the upstream author. Cherry-pick those rebranded commits onto
Hearthpulse one by one (`git cherry-pick -n`), resolve with the rules above, and commit with the upstream author,
message and `(cherry picked from commit <upstream sha>)`.
