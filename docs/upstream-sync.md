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
| `4bd8902` | `a7967cd` | Serve the full Battlegrounds stats from our own pool | Adapted: pool only when set up |
| `db17dbc` | `871fa88` | Blend shared games into the base Battlegrounds stats | Adapted (attribution) |
| `2db0ae3` | `5ed72a4` | Rename the base stats importer | Ported (our doc comments kept) |
| `59ad6fd` | `bff46fb` | Remove the base stats importer | Ported |
| `23a550e` | `16b65f8` | Release 0.11.2 | Ported (version) |
| `23ffc3f` | `788fd41` | Show the full comp list in game | Ported |
| `84edebe` | `57a92d2` | Merge shared stats into every MMR bracket | Adapted (attribution) |

## Conflicts and how they were resolved

- `AppPaths.Root` (`ce9314e`, `1e54a0a`): portable copies use their own data folder (`PortableRoot()`, next to the
  exe or beside `Hearthpulse.app`); only an installed copy runs our `MigrateLegacy` copy of the `Hearthlens` folder.
- `App.xaml.cs` / `AppHost.cs` `LoadMetaAsync` (`861af2b`, `5ae88cc`, `4bd8902`, `db17dbc`, `2db0ae3`): upstream
  replaced the Firestone download with the pool. Ours: pool first when one is set up, otherwise Firestone with the
  Hearthpulse API fallback, as before. The Avalonia host keeps loading Constructed stats for the mulligan panel.
- `BattlegroundsMeta.Attribution`: upstream renamed it to the pool. Ours keeps the Firestone text and adds
  `PoolAttribution`; every meta built by `PoolClient` sets `Source = PoolAttribution`, and the "N shared games" note
  (`84edebe`) is appended to `meta.Source` in both view models and the advisor.
- `MainWindow.xaml` Battlegrounds tabs (`861af2b`, `5ae88cc`): upstream hid Comps/Trinkets/Cards and the bracket
  picker. Ours keeps them (the data is still loaded) and takes upstream's "hide a tab with no rows" bindings.
  Pool meta fills the Comps tab with tribe rows and relabels it "Tribes" (`CompTabLabel`, `CompCaption`); downloaded
  stats keep their comp rows.
- `MainViewModel` hero tier filter: `DataPoints is 0 or >= PoolClient.MinGames` (API fallback rows have no count).
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
     Same anonymous data model as upstream, no code change in the app: set the default endpoint.
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
  Battlegrounds log under `D:\Hearthstone\Logs`. On the development machine it fails for upstream's own code too
  (checked on a clean upstream checkout); without that folder (CI) it is a no-op. Same class as `RealLogTests`.

## Repeating the sync

```
git remote add upstream https://github.com/Dev-NotAqua/HeartLens.git   # once
git config remote.upstream.tagOpt --no-tags                           # upstream tags would clash with ours
git fetch upstream
git log --oneline --no-merges --reverse 84edebe..upstream/main        # what is new
```

Last ported upstream commit: `84edebe` (head `e8b33b74`). To keep conflicts real, port from a rebranded copy of
upstream: check out the last ported upstream commit in a scratch worktree, apply the rebrand rules above to paths
and contents, commit, then for each new upstream commit apply its `git diff <c>^ <c>` with the same `sed`
substitutions (`git apply --index`) and commit with the upstream author. Cherry-pick those rebranded commits onto
Hearthpulse one by one (`git cherry-pick -n`), resolve with the rules above, and commit with the upstream author,
message and `(cherry picked from commit <upstream sha>)`.
