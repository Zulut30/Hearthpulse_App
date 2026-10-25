# Typical findings from the October 2026 reviews

Source: `<repo>/../reference/reviews/HeartLens-feat-*.md`, `HeartLens-main-counters-merged.md`,
`INTEGRATION.md`. Line numbers refer to the reviewed commits and will have moved; use them to find the pattern, not the
place. "→" is how it was fixed. Resolve the repository root and check the optional sibling reference folder exists
before consulting these historical reports.

## Correctness

| Branch / ID | Finding |
|---|---|
| deck-panel-opponent-hand M2 | Opening hand detected by `EnteredHandTurn == 0`, but real logs set `TURN=1` before the deal; the Coin was a skin (`JAIL_COIN3`, `COIN_CARD=1`) → detect by real order, test on a copied real sequence |
| card-pool M3 | Pool chosen from `FormatType` only; Arena/Brawl/adventures got the wrong pool → use `GameType` too |
| my-stats M1 | Arena runs split when `DeckId` is NULL → fall back to class + loss limit |
| mulligan B1 | Wrong baseline turned an existing test red |
| deck-hand m2 | Deck name ending in ":" broke Decks.log parsing |
| my-stats m2 | `winrate: null` shown as 0 % |
| my-stats m3 | Queued deck never reset between games |
| counters-panel m2 | `Counted` documented as "in first-counted order" but some counters sorted |
| card-pool m2 | "Never throws" method let `TaskCanceledException` escape |
| mulligan m6 | One malformed JSON pair emptied the whole list |
| mulligan m4 | Hard-coded "Legend EU" label; rank setting ignored |

## Hidden information

| Branch / ID | Finding |
|---|---|
| deck-hand B1 | `CREATOR_DBID` named a hidden creator → only `DISPLAYED_CREATOR`/`CREATOR` of revealed cards |
| deck-hand M1 | A card shuffled into the deck was followed by entity id and shown when drawn → stays a card back |
| deck-hand m13 | Leak paths DECK→SETASIDE→HAND, `CardId` kept after `HIDE_ENTITY`, `CHANGE_ENTITY` treated as a reveal |
| counters-panel m3 | Fallback icon on an opponent chip looked like a revealed card |
| counters-merged | To check: `TAG_LAST_KNOWN_COST_IN_HAND` on the opponent's unknown entity |

In every case the tests had pinned the leaking behaviour and were turned into negative tests.

## Licences

| Branch / ID | Finding |
|---|---|
| card-pool M5 (→ minor) | Panel repeated HDT's pool summary / related-cards layout nearly element by element (width, accordion, 3-card rows) → redesigned; clean-room note in `docs/discover-pool.md` |
| mulligan | Verbatim HSReplay tooltip quote in docs → rewritten |
| site profile-game-stats | Vendored UI kit without LICENSE/ORIGIN |

Method: grep characteristic names and UI strings across `reference/repos/*`, compare structure, record a clean-room
statement.

## Design tokens and look

- `Color.FromRgb(0x24, 0x19, 0x10)` in `DevRender.cs` instead of the `Bg` resource (counters m5).
- Token added to only one theme file; later two branches added the same `OverlayBadgeShade`.
- New PNG scenes and compact classes not documented in the design skill.

## Parity

- Feature in neither UI's XAML although the VM had it (counters M1: data computed, templates not committed).
- Click-through fixed on Windows only; macOS/X11 only compiled (card-pool M6) → platform layer for all.
- Avalonia smooth scroll without the animation guard (card-pool m16).
- Renderers loaded meta differently; Avalonia hit the network (deck-hand m11).
- Avalonia compact mode not adapted (my-stats M2); an Avalonia card always empty (my-stats m10).

## Migrations and data

- Checked: idempotent `ALTER TABLE` via `PRAGMA table_info`, older app reading a newer file (INSERT with explicit
  columns, read by `GetOrdinal`), partial-run safety, backfill only `COALESCE(col, $v)`.
- Old rows never backfilled → UI must say "no data for older games" (my-stats m4).

## Tests

- Feature without tests (card-pool M2, mulligan M1).
- Windows-only file-lock test would break ubuntu CI (oidc Minor 12 → Major) → `[WindowsFact]` + OS-neutral variant.
- Date test depending on the time zone → local noon.
- Main assertion depending on `D:\Hearthstone\Logs` (machine-dependent).
- No script to regenerate fixture numbers → `tools/cardpool/pool_fixture.py`.
- Historical UI/VM gap: tests referenced Core only. The suite now references Avalonia UI and Headless as well;
  test its view models/bindings directly and keep domain rules in Core. Common view models now live in shared
  `src/Hearthpulse.Presentation`; the separate Windows presentation suite compares both compiled variants.

## Performance

- Rebuild on every snapshot publish (up to 10/s): card-pool M4, m13/m14; mulligan m10.
- Full entity scan per BG view (counters-merged).
- O(n²) chart work per mouse move (my-stats m8).
- Full history re-read on each refresh (my-stats m7, deck-hand m5/m12).
- Unbounded request fan-out, no 404 cache (mulligan m5); unbounded render cache on disk (card-pool m18).

## Threading

- Endless hover thread without `Dispose`, one per `GameSession` (card-pool M1).
- Status written from another thread (card-pool m10).
- My Stats rebuilt on the UI thread with a stale-snapshot race → `Task.Run` + generation counter (my-stats m7).
- Refresh token lost on cancel/timeout → `CancellationToken.None` + `WaitAsync(ct)`, save before `/me` (oidc M1).
- Non-atomic file writes → temp + `File.Move(overwrite: true)`.
- Fire-and-forget `Task.Run` without logging (deck-hand m5, my-stats m11).

## Other

- Docs or skills promising behaviour the code lacks (card-pool m1/m21, oidc m8, mulligan m21).
- New CS0618 warning while the project builds with zero warnings (counters M2).
- Dead code kept: `MulliganTile`, `ShowFirst`, `KeyBrush`.
- Env override for the site must be https-only and host-checked, falling back instead of crashing (oidc).
- Historical `--render-ui` abort on an optional scene IO exception (counters m4, deck-hand m10): optional missing
  log moments should be explicit skips; fatal errors and missing mandatory scenes must still fail the run.
- Overlay interactive areas stealing game clicks over Discover / End Turn (deck-hand m7, card-pool M6).
