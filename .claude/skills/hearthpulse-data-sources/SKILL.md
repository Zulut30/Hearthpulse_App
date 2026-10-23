---
name: hearthpulse-data-sources
description: Use when Hearthpulse code talks to anything outside the machine or borrows from another project - the Hearthpulse data API api.kolodahearthstone.com (HSGuru/HSReplay meta, mulligan, card pool, HearthpulseData cache), the hearthpulse.net account (device flow client manacost-tracker, /me, entitlements/paywall, tracker/events/batch game upload), HearthstoneJSON cards and art, hsbg.cards, Firestone BG stats, GitHub updates - and the licence rules for HDT, Firestone, HdtArenaHelper, bgtracker, tavern-ledger and upstream HeartLens. Triggers - "API", "endpoint", "кэш", "войти через сайт", "подписка", "paywall", "upload games", "можно ли взять код из ...", "attribution".
---

# External data and what we may borrow

Endpoint-by-endpoint detail: [reference/endpoints.md](reference/endpoints.md). Field-level notes on the data API:
[hearthstone-context reference/kolodahearthstone-api.md](../hearthstone-context/reference/kolodahearthstone-api.md).
Tests use fake handlers, never the network: [hearthpulse-testing](../hearthpulse-testing/SKILL.md).

## Sources at a glance

| Source | Used for | Code | Override / cache |
|---|---|---|---|
| **Hearthpulse data API** `https://api.kolodahearthstone.com/` (owner's server) | Constructed meta (HSGuru meta/archetypes/analysis, HSReplay archetypes/decks/mulligan/matchups), **Battlegrounds** heroes (top 50/20/5/1%) and minions (all/50/20%), card pool | `Core/Api/HearthpulseApi.cs` (transport), `Core/Api/HearthpulseData.cs` (typed calls), `Core/Constructed/ApiConstructedMeta.cs`, `Core/Battlegrounds/BattlegroundsMeta.cs`, `Core/Tracking/MulliganGuideSource.cs`, `Core/CardPool/CardPoolCatalog.cs` | `HEARTHPULSE_API`; `cache/api/*.json` 6 h (card pool 1 day) |
| **hearthpulse.net site** `https://hearthpulse.net/api/v1/` | account sign-in (OAuth device flow), `/me`, subscription | `Core/Accounts/` (`AccountClient`, `AccountService`, `TokenStore`, `Entitlements`) | `HEARTHPULSE_SITE_API` (https, or http on loopback); session in `account.dat` |
| **HearthstoneJSON** | all card data (`cards.json` per locale), card art (`art.hearthstonejson.com`), card pool fallback | `Core/Cards/CardDb.cs`, `App|UI/Infrastructure/CardArt.cs`, `CardRender.cs` | `cache/cards.<locale>.json` 2 days, `cache/images/` |
| **hsbg.cards** | BG tiers, text, current pool | `Core/Cards/BgCardSource.cs` (`BgCardDb`) | `cache/bgcards.hsbg.json` 12 h; 120 req/min anonymous |
| **Hearthpulse archetype dictionary** `https://api.blizzcore.ru/archetypes?limit=1000` (owner's service, deckview on our server; no separate attribution) | Russian archetype names (`name_en` -> `name_ru`) when the interface is Russian | `Core/Constructed/ArchetypeTranslations.cs` (hosts start `LoadAsync` beside the meta, never before it, and reload the meta when the names arrive; `RussianLabel` in `ApiConstructedMeta`, `Display` for names shown later) | `cache/api/blizzcore-archetypes.json` 12 h, stale copy offline; own 5 s timeout; failures back off 1-6 h (`blizzcore-archetypes.failed`) |
| **Hearthpulse stats pool** (`PoolClient.Endpoint`, off by default) | BG comps, guides, trinkets, tribes, card turn results, MMR floors; shared games | `Core/Pool/PoolClient.cs` (`OverlayAsync` lays it over the API meta) | none |
| **GitHub releases** `Zulut30/Hearthpulse_App` | updates | `Core/Updates/UpdateService.cs` | see [hearthpulse-release](../hearthpulse-release/SKILL.md) |

Firestone (`static.zerotoheroes.com`) is **not used at all**: there is no permission for its data, and upstream HeartLens
dropped it for the same reason. Its loaders were deleted - do not add any request to it. What each Battlegrounds number
now comes from, and what stays empty until the pool runs: `docs/bg-meta-sources.md`. All shared HTTP goes through one `HttpClient` built in `App.xaml.cs` / `UI/AppHost.cs`
(`SocketsHttpHandler`, decompression, 40 s timeout, User-Agent `Hearthpulse/<version>`).

## Rules for any network code

1. **Never throw into the UI.** `HearthpulseApi` turns transport errors into `ApiError.Offline` and maps statuses
   (404/405 `NOT_AVAILABLE`, 401 `INVALID_TOKEN`, 429 `RATE_LIMITED`, 5xx `SERVER_ERROR`); `HearthpulseData`
   "never throws". Note that `TaskCanceledException` is not `HttpRequestException` - a review found a "never throws"
   method leaking it.
2. **Cache to disk, atomically**: write `file.<guid>.tmp`, then `File.Move(overwrite: true)`. Serve a stale file
   when the fetch fails. Cache negative answers too (404 -> `{"data":null}` for the cache age) so a missing archetype
   does not cause a request storm. Bound fan-out (per-archetype requests) and disk growth.
3. **Validate what goes into URLs and file names** (`SafeKey` regex `^[a-z0-9_.]{1,64}$` in `HearthpulseData`).
4. **Envelopes**: the data API answers `{"data": ..., "meta": ...}` or a bare value; errors in `{"detail": ...}` (also
   FastAPI validation lists). The site answers OAuth errors `{"error":"slow_down"}` and API errors
   `{"error":{"code","message"}}` - `AccountClient` parses both.
5. **Background, cancellable, off the log thread**: loaders run as tasks with a `CancellationToken`; the log watcher
   thread never waits for the network.
6. **Attribution**: each source has an `Attribution` constant shown in the UI next to its numbers (e.g.
   `ApiConstructedMeta.Attribution`, `BattlegroundsMeta.Attribution`, `BgCardDb.Attribution`). Keep it next to new
   data. Everything from the data API is called **"Hearthpulse data"** (`HearthpulseData.Attribution`) in anything
   the player reads - UI, tooltips, attributions, installer texts; never HSReplay or HSGuru (internal names, endpoint
   paths and these developer docs may keep them; `LocalizationTests` checks the string tables). The installer notices
   (`installer/windows/notice.txt`, `installer/*/README.txt`) credit Hearthpulse data, HearthstoneJSON and the fonts -
   update them when you add a source. UI texts are localized: `docs/localization.md`.
7. **No secrets in the repo**; tokens only through `TokenStore`; never log tokens or e-mail.

## Account and paid access (hearthpulse.net)

- Flow (RFC 8628 device grant): `POST oauth/device/code` (client `manacost-tracker`, scopes
  `profile.read subscription.read catalog.read images.read statistics.read tracker.write`) -> show code, open
  `verification_uri_complete` (must be https, same host) -> poll `POST oauth/token` with `interval` (+5 s on
  `slow_down`; stop on `access_denied`, `expired_token`, `invalid_grant`) -> `GET me`.
- Refresh tokens rotate and a reused one revokes the whole family: token requests are serialized, the new pair is
  saved **before** calling `/me`, and cancellation must not lose an issued token (use `CancellationToken.None` +
  `WaitAsync(ct)`). Session lifetime is an absolute 30 days (`AccountService.SessionLifetime`). Sign-out = revoke.
- `TokenStore`: DPAPI on Windows, a 0600 JSON file elsewhere, atomic writes, format version + site origin check.
- **Entitlements** come from `/me` `subscription` (no separate endpoint): `Entitlements.HasAccess`, `Has(section)`
  with sections `arena`, `battlegrounds`, `standard`, `contests`, `guidesArchive`, `arenaArticles`,
  `battlegroundsArticles`; it fails closed and keeps the last known state offline (`Stale`, `CheckedAt`).
- **Paywall**: none is applied yet - the UI only shows the subscription line (`AccountHasSubscription`). When gating
  a feature: read `AccountService.Entitlements.Has(Entitlements.<Section>)` in the view model, keep the free path
  working offline, show what unlocks it, and never hide data the player already had.
- Contract and manual checks: `docs/accounts-api.md`.

## Uploading games (`tracker/events/batch`) - not implemented

The app does **not** send games yet (no uploader in `src`; only the `tracker.write` scope is requested). The site
side expects `POST /api/v1/tracker/events/batch` with the same device-flow token, 1-50 events per call, idempotent by
`eventId` (UUID), answer 202 `{accepted, rejected}`, payload `schemaVersion: 2` for the site's "My statistics".
The contract lives in the shared notes as `player-games-upload-contract.md` (site branch
`claude/profile-game-stats-e88af0`, not merged). Before building it: opt-in setting (off by default), send each game
once after it ends, queue offline with a bound, never upload hidden information or the opponent's BattleTag beyond
what the contract asks, and agree the payload with the site session.

## Licences: what may be reused

| Project | Licence | Allowed |
|---|---|---|
| Hearthstone Deck Tracker (HearthSim/HDT) | All rights reserved (source published for reading) | **Ideas only.** No code, no XAML structure, no copied UI strings; do not mirror its layouts element by element (card-pool review). |
| Firestone (Zero-to-Heroes) | No licence = all rights reserved; ToS forbids derivatives | **Ideas only** for code/UI. Its stats files (`static.zerotoheroes.com`) are **not** used: no permission. |
| HdtArenaHelper (`reference/repos/ideas/HdtArenaHelper`) | MIT, (c) 2026 Alessandro Colace | Code may be adapted **with attribution**: keep the copyright + MIT notice (file header and the notice file). |
| bgtracker (`reference/repos/ideas/bgtracker`) | MIT, (c) 2026 the bgtracker contributors | Same as above: reuse with the MIT notice and credit. |
| tavern-ledger (`reference/repos/ideas/tavern-ledger`) | AGPL-3.0-only | **Ideas only.** Copying code would put Hearthpulse under AGPL. |
| HeartLens upstream (`Dev-NotAqua/HeartLens`, git remote `upstream`) | the original project | Port commits **only with the author's permission** (the `upstream-heartlens-sync` session does this); keep authorship in commit messages. |
| python-hearthstone (HearthSim) | MIT | Tag/enum names; cite it (`BattlegroundsCounters.cs`, `docs/counters-battlegrounds.md` do). |
| unity-spy | MIT | Mono memory offsets idea, cited in `Core/Memory/MonoOffsets.cs`. |
| HearthstoneJSON, hsbg.cards, our API | game data / owner's services | Data with attribution. |

The combat simulator is **clean-room**: no other simulator's code is read at all (see
`src/Hearthpulse.Core/Battlegrounds/Effects/README.md`). The HDT/Firestone index in
`C:\Users\zulut\Downloads\HeartLens-main\reference\index\` (`hdt.md`, `firestone.md`, `feature-gap.md`,
`ideas-bg-arena.md`, `LICENSES.md`) holds paths and one-line purposes for orientation - use it to learn *what* a
feature does, then design it from game data. Record a clean-room statement in the feature's doc
(`docs/counters-constructed.md` and `docs/discover-pool.md` do).
