# P2 handover: private capture API and worker lifecycle

Assigned branch `t3/source-capture-server-pool`, base `ee6d98f9`. Owned changes are entirely under `server/pool/`.
Commit SHA is in the implementation reply. No push, PR, merge, application launch, personal log upload or live
deployment. P4 owns composition, solution/docs, legal review, host installation and live nginx changes.

## Selected P4 installation

- Public base: **https://api.kolodahearthstone.com/stats**, existing TLS/nginx on 151. Strip `/stats` to
  `http://127.0.0.1:3181`; no DNS or unrelated edge changes. The supplied nginx locations affect `/stats/` only.
- Latest host free-space report from P4: 44 GiB. Keep the 256 MiB pilot archive cap / 128 MiB daily intake; no
  cleanup of existing host/site/Docker data. This is not a capacity estimate for 180 days of general rollout.
- Install a dedicated pinned Node 24 runtime; do not upgrade the site's system Node. Tested local Node 24.19.0.
- Configure the real self-contained worker at a fixed operator-selected path, e.g.
  `/opt/hearthpulse-pool/worker/Hearthpulse.SourceWorker`, args `[]`. HTTP cannot select commands or paths.
- Parser versions are read from validated output. The parent's corrected **source-v1.1** was built and exercised;
  no production parser version is hardcoded by this server.

Deployment files: `deploy/hearthpulse-pool.service`, `deploy/nginx-source-capture.conf`, `deploy/pool.env.example`,
`deploy/LINUX-RUNBOOK.md`, `deploy/synthetic-smoke.mjs`. The smoke preserves base path prefixes including `/stats`.

## Public Node integration signatures

```js
// node/server.mjs
await startPool({
  dataDir,                   // required absolute PRIVATE local directory, outside web roots
  host = '127.0.0.1', port = 3181, operatorToken = '', maxRequests = 4,
  archive = {}, legacyEnv = {}, schedule = true,
}); // -> { server, archive, store, url, close(): Promise<void> }
configFromEnvironment();

// node/archive.mjs
new CaptureArchive({ sqlite, root, queue, worker, /* budgets below */ });
await archive.recover();
await archive.upload(chunk);      // -> { statusCode: 201|200, body: receipt }
await archive.complete(manifest); // -> { statusCode: 202, body: {ok:true,status:'stored',captureId} }
await archive.status(identity);   // -> safe technical status JSON, never source/events
await archive.reprocess(identity);// -> 202, only the HTTP operator gate can expose this
await archive.forget(generation); // -> 200 deleted OR 202 deleting
await archive.tick();             // one real configured process job, false if none/worker absent
await archive.maintain();
await archive.close();

// node/sqlite.mjs
openDatabase(absoluteFile);       // -> { sqlite, DB, close() }; real node:sqlite, FULL synchronous WAL
new SerialQueue().run(operation);
new ResponseCache();             // match/put/clear, bounded and instance-local
```

`DB` implements `prepare(sql).bind(...args).first()/all()/run()` and transactional `batch(statements)` for the
unchanged legacy SQL routes. Node legacy requests serialize with capture intake and worker commits; quota and
insert commit together. An OS SQLite lock in `instance.lock.sqlite` excludes a second writer process, including
concurrent restarts. The lock is automatically released on abrupt process exit. Do not delete a live lock file.

## HTTP and persistence behavior

Internal loopback routes have no `/stats` prefix; public proxy adds/removes the mount. Capture POST contracts are
the task card's `chunks`, `complete`, `status`, `reprocess`, `forget`; every new body includes `v:1`. Reprocess
additionally requires BOTH loopback socket address and `Authorization: Bearer <POOL_OPERATOR_TOKEN>`; forwarding
headers never grant access. Empty token disables reprocess. Public nginx always rejects it and strips auth.

- Chunk bodies <=6 MiB, gzip input/output <=4 MiB, 64 chunks / 256 MiB source ceiling, JSONL lines <=64 KiB.
  The producer must cut chunks at complete JSONL/LF record boundaries. UTF-8/BOM/CRLF, duplicate JSON keys at all
  depths, unknown fields, kinds, tags, malformed nested data and hidden-zone named cards are rejected.
- Hash is over exact uncompressed bytes. Stored gzip is regenerated from validated bytes so caller gzip
  filename/comment metadata cannot enter the private archive. Files are flushed, renamed, and parent directories
  synced on Linux before a receipt is committed. Same receipt replay is 200; differing index/hash is 409.
- Complete verifies all receipts, exact physical source hash/size, whole-stream sequence, one start, terminal
  end/gap, metadata and gap counts. It rejects records after end and refs reused across conceal/reveal. Incomplete
  captures/drafts persist explicitly, without inventing a complete game or draft-run outcome.
- Source records remain immutable. Manifests cannot change after sealing. Status is persistent and distinguishes
  stored/processing/parsed/partial/failed/deleted. No worker configured means stored, never parsed.
- Quotas are serialized and durable; duplicates do not consume source quota again. Archive reservation includes
  uncompressed worker input, compressed chunks, derived JSON and a bounded running output. Free disk is checked
  separately. Temporary pressure is 503/Retry-After, not successful loss. SQL/file write failures are recoverable.
- `private/revocations.jsonl` is an independent fsynced append-only revocation ledger. It wins over restored old SQL
  before HTTP starts. Revocation also takes effect in memory if its subsequent SQL insert fails. Never roll this
  ledger back with older backups. Permanent capture tombstones also prevent expired IDs being revived.
- Forget revokes first, kills matching active workers, removes SQL facts/receipts/manifests, removes physical
  source/work files and checkpoints WAL. Filesystem failure returns 202 deleting; retry/startup/maintenance
  finishes it. Worker commits recheck generation + revision under the same serialized writer before activation.
- Retention: source/derived <=180 days, unfinished uploads 24 h; maintenance every minute and before listening.
  Startup removes orphan pre-receipt writes and interrupted work, requeues processing, reapplies revocations.
- Existing `/v1/games`, `/v1/constructed`, `/v1/events`, reads and legacy forget need no new client fields.
  Migration `0008_captures.sql` adds tables and transactional snapshot invalidation, without changing old tables.
  Node applies it at startup. Apply it explicitly to an existing D1 deployment if retaining that deployment.
  Deletion-sensitive events summary uses no-store; count-preserving replacements/day rollover cannot reuse stale
  snapshots. Other in-process legacy caches clear after mutations.

Legacy `/v1/events/forget` remains a separate legacy lifecycle because old senders have no generation field. P4
must keep its existing sender/forget handling alongside new capture forget; source results do not append to `e`.
No API endpoint exposes archive paths, source bytes, identities or derived per-capture facts.

## Worker executable contract and output

```text
<configured absolute executable> <configured fixed argv...> --input <private verified JSONL> --output <private JSON>
```

`spawn(...,{shell:false})`, one job at a time, default 120-second timeout. On Linux the worker has its own process
group, killed on revoke/timeout/shutdown. Stdout must be one JSON object with `ok:true,status:parsed|partial` and
exit code zero. Stdout/stderr each cap at 16 KiB, neither is forwarded/logged. Result file caps at 64 MiB and must
be a regular file. Nonzero/malformed/missing/oversized/private output is failed, with source retained.

The task card's minimal event/summary shapes and P3's full shapes are allowlisted. P3 full events additionally
carry safe side/entity/target; semantic kinds play/buy/sell/shop/turn/resources are bound to corresponding typed
source records. Data may only copy validated source fields. Full summary fields are allowlisted and source-bound,
including mode/kind/end/result/placement. `summary.completeForAggregation` is the eligibility gate; do not aggregate
just because status is parsed. Source SHA, record count, parser version and safe counters are checked. One SQL
derived result replaces the previous one only after a successful job; repeated/new-version processing does not
append contributions. Failed reprocess leaves the previous result available internally but status failed, so
consumers must use status/eligibility and not count stale failed rows. Temporary worker files are cleaned.

## Environment / commands

| Variable | Default / contract |
|---|---|
| POOL_DATA_DIR | Required absolute private local data directory |
| POOL_HOST / POOL_PORT | 127.0.0.1 / 3181 (port 0 for isolated local tests) |
| POOL_OPERATOR_TOKEN | Empty disables operator reprocess; set via host secret handling |
| POOL_WORKER_EXECUTABLE / POOL_WORKER_ARGS | Both unset, or fixed absolute executable and JSON string array |
| POOL_ARCHIVE_BYTES / POOL_DAILY_BYTES | 268435456 / 134217728 |
| POOL_DAILY_CAPTURES / POOL_MIN_FREE_BYTES | 1000 / 67108864 |
| POOL_RETENTION_DAYS / POOL_UNFINISHED_HOURS | 180 (maximum) / 24 |
| POOL_MAX_REQUESTS / POOL_WORKER_TIMEOUT_MS | 4 / 120000 |
| POOL_LEGACY_DAILY / POOL_EVENTS_DAILY | 15000 / 10000 |

From the integrated checkout root with its dedicated runtime and environment:

```sh
node server/pool/node/server.mjs
curl --fail http://127.0.0.1:3181/healthz
node server/pool/deploy/synthetic-smoke.mjs http://127.0.0.1:3181
node server/pool/deploy/synthetic-smoke.mjs https://api.kolodahearthstone.com/stats
node --test server/pool/test/*.test.mjs
```

Public smoke runs without the operator token. Direct loopback smoke includes reprocess when the token is present.
It generates only typed synthetic facts and requires confirmed 200 deleted; 202 deleting is not success.

## Verification evidence and limits

Local Windows, Node 24.19.0. Original baseline: 41 passed, zero failed/skipped. Final common pool suite and exact
expanded names are recorded below. Node syntax checks and `git diff --check` passed. No JS lint configuration
exists in this package. Test scratch, logs and broad research/plan/status files live outside the checkout in
`%TEMP%/hearthpulse-p2-test-state` and per-test temporary directories.

The REAL parent-corrected P3 worker was restored and built from sibling integration checkout, outputs isolated
outside any checkout at `C:/hp-p2-v11`. SDK 10.0.302, `-m:2 -p:HEARTHPULSE_STRICT=true`: **0 warnings, 0 errors**.
Integration HEAD observed `55c6c4e906960881b7714b82133b43f7a373cba3`, source parser version `source-v1.1`.
Built worker DLL SHA256 `f3f2279e81b87de233964dee85c20c6a75ae4dee50fb1e27a8ae1f96fe40dbd5`.
Earlier original P3 `5f0f351c` was also exercised, then verification was repeated against the parent's correction.

```powershell
dotnet restore 'C:\Users\zulut\.t3\worktrees\HeartLens-main\claude-source-capture-integration\tools\Hearthpulse.SourceWorker\Hearthpulse.SourceWorker.csproj' --source https://api.nuget.org/v3/index.json --artifacts-path C:\hp-p2-v11 -m:2
dotnet build 'C:\Users\zulut\.t3\worktrees\HeartLens-main\claude-source-capture-integration\tools\Hearthpulse.SourceWorker\Hearthpulse.SourceWorker.csproj' --no-restore --artifacts-path C:\hp-p2-v11 -m:2 -p:HEARTHPULSE_STRICT=true
$env:POOL_TEST_WORKER_EXECUTABLE = 'C:\Program Files\dotnet\dotnet.exe'
$env:POOL_TEST_WORKER_ARGS = '["C:/hp-p2-v11/bin/Hearthpulse.SourceWorker/debug/Hearthpulse.SourceWorker.dll"]'
node --test server/pool/test/real-worker.integration.mjs
```

The explicit integration file requires a real worker and fails if configuration is absent; it never substitutes a
fixture parser. Windows DLL invocation verifies the same CLI protocol; Linux self-contained launch remains P4's
runtime check. Standalone process fixtures in common tests only exercise lifecycle/error paths, never production.

| Requirement | Evidence |
|---|---|
| Durable receipt / disconnect-retry / restart | `durable receipt replay survives restart without duplicate chunks or quota` |
| File failure / receipt atomicity / repair | `rename failure and orphan file recovery never acknowledge an unwritten chunk`; `SQL receipt failure rolls back quota cleans file and allows a genuine retry` |
| Privacy / malformed / decompression / limits | `strict intake rejects hidden opponent hand before archival`; `shadowed duplicate keys invalid UTF8 BOM CRLF and partial lines are rejected before archival`; `bounded decompression checksum gzip base64 and hard chunk limits return deterministic errors` |
| Whole stream / no post-game data / incomplete draft | `whole stream rejects post-game reveal`; `whole stream rejects concealed reference linkage`; `incomplete and independent draft captures persist without invented game results` |
| Quotas / pressure / concurrent intake | `atomic archive cap and daily quotas reject concurrent intake without partial receipts`; `disk pressure gives retryable failure and no success receipt` |
| Revoke / delete / physical cleanup / restored SQL | `forget during a running worker kills it and late completion cannot resurrect the generation`; `pending physical deletion returns 202 remains revoked and recovers on restart`; `revocation ledger wins over a restored older SQLite backup` |
| Reprocess replacement / failure recovery | `worker process writes versioned results and reprocess replaces one contribution`; `worker false-summary fails safely and supports explicit recovery`; `corrupt source after manifest fails the worker without fabricating empty results` |
| Legacy routes / stale aggregates | `Node HTTP preserves games constructed events reads and durable SQLite restart contracts`; `events deletion insertion replacement and day rollover cannot serve stale snapshots or caches` |
| Real CLI / crash restart / private operator gate | `real Node entrypoint restarts after abrupt exit with persisted manifest and safe health output`; `single writer OS lock rejects another service and releases after normal shutdown` |
| Public mount prefix | `synthetic smoke preserves the stats mount prefix through upload status and deletion` |
| Real corrected .NET source-v1.1 / hash / facts / deletion | `real dotnet worker preserves semantic facts replaces deterministically and cannot survive generation deletion` |
| Real .NET incomplete draft / packages / no invented result | `real dotnet draft terminal gap stays partial preserves packages and invents no run result` |

Unverified here: Linux execution (WSL returned `Wsl/CallMsi/Install/REGDB_E_CLASSNOTREG`), live systemd/nginx/TLS on
151, actual host backup deletion/restore policy, physical power-loss behavior, P1 live recorder and both UI flows.
P4 must run synthetic Linux/HTTPS restart/reprocess/forget and full integration/legal checks. No host files were
changed. Existing backups/snapshots outside this service need their own verified deletion process.

P4 producer checks: whole-line LF chunk boundaries, opaque fresh refs across conceal, opponent standalone tags
with card=null and public refs for numeric card tags, public-only partner zones, and real P3-compatible source
semantics. Server validation does not establish that a game client actually observed an asserted public fact.
No claim of complete live draft/mechanics coverage or production readiness is made by these local tests.

## Final exact results and expanded test inventory

Common pool command: `node --test server/pool/test/*.test.mjs` — Passed: 111, Failed: 0, Skipped: 0, Cancelled: 0 (exit 0). Original 41 tests preserved; 70 new common tests.

Real corrected source-v1.1 worker: `node --test server/pool/test/real-worker.integration.mjs` with the executable/argv environment above — Passed: 2, Failed: 0, Skipped: 0, Cancelled: 0 (exit 0).

Every exact name below passed in the final runs (the first 111 are the common suite; the final two use the real worker).

- `typed source allows every declared record family and preserves choice copies and ordering`
- `record reference turn sequence card and choice array limits include exact boundaries`
- `snapshot and draft option bounds reject unsafe nested content and extra keys`
- `decompression accepts the exact four MiB bound and rejects the next byte and malformed gzip`
- `durable receipt replay survives restart without duplicate chunks or quota`
- `concurrent identical and conflicting intake preserves one authoritative receipt`
- `manifest checks missing mismatched extra and corrupted physical chunks`
- `manifest validates cross-chunk sequence start end metadata and immutable sealing`
- `whole stream rejects sequence gap`
- `whole stream rejects missing start`
- `whole stream rejects missing end`
- `whole stream rejects post-game reveal`
- `whole stream rejects duplicate start`
- `whole stream rejects false complete`
- `whole stream rejects gap mismatch`
- `whole stream rejects unknown action completeness`
- `whole stream rejects concealed reference linkage`
- `incomplete and independent draft captures persist without invented game results`
- `strict intake rejects extra raw field before archival`
- `strict intake rejects unknown kind before archival`
- `strict intake rejects unknown data field before archival`
- `strict intake rejects hidden opponent hand before archival`
- `strict intake rejects hidden snapshot deck before archival`
- `strict intake rejects hidden numeric tags before archival`
- `strict intake rejects opponent shop before archival`
- `strict intake rejects nonpublic partner zone before archival`
- `strict intake rejects raw string tag before archival`
- `strict intake rejects opponent choices before archival`
- `strict intake rejects hidden opponent draw before archival`
- `strict intake rejects malformed snapshot before archival`
- `shadowed duplicate keys invalid UTF8 BOM CRLF and partial lines are rejected before archival`
- `bounded decompression checksum gzip base64 and hard chunk limits return deterministic errors`
- `atomic archive cap and daily quotas reject concurrent intake without partial receipts`
- `disk pressure gives retryable failure and no success receipt`
- `rename failure and orphan file recovery never acknowledge an unwritten chunk`
- `atomic file writer preserves previous file when rename fails`
- `forget revokes durably deletes every artifact and rejects all late operations`
- `revocation ledger wins over a restored older SQLite backup`
- `worker process writes versioned results and reprocess replaces one contribution`
- `worker crash fails safely and supports explicit recovery`
- `worker bad-hash fails safely and supports explicit recovery`
- `worker bad-count fails safely and supports explicit recovery`
- `worker private fails safely and supports explicit recovery`
- `worker noisy fails safely and supports explicit recovery`
- `worker false-summary fails safely and supports explicit recovery`
- `forget during a running worker kills it and late completion cannot resurrect the generation`
- `restart recovers processing status and incomplete source parses as partial`
- `worker timeout and missing executable persist failure while retaining the source`
- `retention expires unfinished and complete archives with persistent capture tombstones`
- `pending physical deletion returns 202 remains revoked and recovers on restart`
- `concurrent upload complete reprocess and forget converge to durable deletion`
- `SQL receipt failure rolls back quota cleans file and allows a genuine retry`
- `durable revoke survives a failed SQL tombstone insert in the running process`
- `torn unacknowledged ledger append is truncated without losing earlier revocations`
- `corrupt source after manifest fails the worker without fabricating empty results`
- `sixty-four ordered chunks form one whole stream and a sixty-fifth is refused`
- `bounded worker output rejects oversized files without storing derived data`
- `public opponent resource and card tags remain usable while unobserved hidden tags fail completion`
- `a Constructed record keeps every known field and nothing else`
- `a Battlegrounds record keeps its lobby, buys, sells, picks and placement`
- `a record with a bad core field is rejected`
- `bad optional parts are dropped, not the record`
- `lists are capped`
- `POST /v1/events stores a batch once, and a second send is not counted twice`
- `POST /v1/events refuses a bad envelope`
- `POST /v1/events refuses a body over the size limit`
- `a record over the per-game size limit is dropped`
- `the daily cap answers 503 past it`
- `POST /v1/events/forget deletes every record of the install and nobody else's`
- `the summary counts games per day and builds Discover and mulligan totals from 10 games`
- `the summary leaves out cards seen in fewer than 10 games, or from fewer than 5 installs`
- `Node HTTP preserves games constructed events reads and durable SQLite restart contracts`
- `legacy quota reservation is atomic and rolls back with a failed insert`
- `events deletion insertion replacement and day rollover cannot serve stale snapshots or caches`
- `source HTTP rejects caller commands malformed envelopes body overflow and forwarded operator authority`
- `streamed HTTP body limit is enforced without content length`
- `gzip metadata is discarded while exact validated uncompressed source bytes are preserved`
- `D1 adapter batch rolls back atomically and returns real selected rows and changes`
- `response cache clones bodies isolates instances and clears entries`
- `real Node entrypoint restarts after abrupt exit with persisted manifest and safe health output`
- `single writer OS lock rejects another service and releases after normal shutdown`
- `corrupt complete revocation ledger fails closed before HTTP starts`
- `synthetic smoke preserves the stats mount prefix through upload status and deletion`
- `valid details keep chosen trinkets and deduplicate the offer`
- `invalid trinkets discard details without losing the game: numeric picks`
- `invalid trinkets discard details without losing the game: object offers`
- `invalid trinkets discard details without losing the game: string picks`
- `invalid trinkets discard details without losing the game: string offers`
- `invalid trinkets discard details without losing the game: invalid chosen card`
- `invalid trinkets discard details without losing the game: invalid offered card`
- `invalid trinkets discard details without losing the game: primitive container`
- `invalid trinkets discard details without losing the game: array container`
- `missing and null optional trinkets remain empty`
- `trinket list limits include the boundary and reject the next item`
- `raw trinket lists are bounded before deduplication`
- `non-object details return an empty result`
- `malformed identifier objects cannot invoke string coercion`
- `invalid choices and plays do not remove valid entries`
- `legacy trinket fields use the same validation`
- `POST stores valid games and drops invalid details in a mixed batch`
- `POST rejects malformed JSON and batches without valid games before writing`
- `POST accepts a batch at the twenty game boundary`
- `a Constructed game keeps only valid picks and holds no deck, class or opponent`
- `a Constructed game without a valid core field or any valid pick is rejected`
- `POST /v1/constructed stores a batch once and the totals count offers, picks and wins`
- `POST /v1/constructed rejects malformed bodies before writing`
- `percentile floors need 1000 rated games and 50 at or above each floor, and a repeated floor is left out`
- `the totals split heroes and trinkets by rating bracket and heroes by lobby tribe, from 10 games a cell`
- `past the daily write budget a batch is refused with 503 and not stored`
- `the cron job deletes games older than 60 days`
- `quest and reward picks are accepted choice kinds`
- `real dotnet worker preserves semantic facts replaces deterministically and cannot survive generation deletion`
- `real dotnet draft terminal gap stays partial preserves packages and invents no run result`
