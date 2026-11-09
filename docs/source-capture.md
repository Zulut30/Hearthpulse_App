# Source captures for Hearthpulse statistics

Source capture preserves a safe, ordered projection of the observed game protocol so the server can decode it
again when its parser improves. It supplements the compact records in [game-stats.md](game-stats.md); it does not
upload arbitrary personal log files. Source format, decoder version and derived-statistics version are separate.

The Linux deployment targets the existing Hearthpulse server, with its own process, private archive and database.
It does not use the site's account database. The API and the .NET decoder are independent processes; a successfully
stored source remains available when decoding fails. Deployment settings and limits live with `server/pool`.

## What is captured

The v1 JSONL protocol retains ordered visible entity observations, selected numeric tags, blocks, local choices
with ordered alternatives, actions, visible board/shop snapshots, draft choices and explicit gaps. Each record has
a contiguous sequence number, turn, scoped references and versioned payload. Repeated alternatives remain repeated;
a card instance is distinct from its card definition. Draft captures are independent of completed matches.

Source v2 (current recorder) adds only public context and labels: the game's format for Ranked/Casual
(`standard|wild|classic|twist`), the Arena kind (`normal|underground`, also on draft captures), the client's choice
type (`mulligan|general|other`) and a named `combat_view` gap that marks every Battlegrounds game source as not a
replay of the displayed combat. All records of a capture share one version, recorded as `manifest.sourceVersion`; a
capture spooled by an older app is finished and sent as v1 unchanged. Contract details: the
[CLI contract](../tools/Hearthpulse.SourceWorker/README.md#source-v2-and-result-v2).

There is no player name, BattleTag, account id, deck name, chat, authentication/network log or machine path in the
wire format. Other players' hidden cards, shop or future post-game reveals are not a source. References across
opponent conceal/reveal boundaries must be renewed so an entity cannot track a hidden shuffled card.

Capture is a typed safety boundary. Unknown or unavailable observations produce a gap/partial state rather than
an arbitrary string payload or an invented zero. A newly supported semantic interpretation can reuse saved source;
a new capture field or source sometimes needs a client update. The v1 allowlist does not imply coverage of every
Hearthstone mechanic or a guarantee of complete data from every installation.

## Delivery and lifecycle

Sources are written to a separate durable spool, compressed and sent in chunks with SHA-256 over the exact
uncompressed bytes. Receipts must match the submitted index and hash before a chunk is acknowledged locally.
The manifest is committed after all chunks; `stored`, `processing`, `parsed`, `partial` and `failed` are distinct
states. Missing parts, conflicting hashes, rejected data and revoked generations are not successful delivery.

Source sending follows the existing game-statistics switch and additionally requires the current source notice.
Seeing the old compact-record notice does not authorize the expanded sender to start before the updated notice.
The in-app and installer texts describe the expanded fields, the Hearthpulse server and retention. Public source
activation remains gated: the default endpoint is unset, and the published privacy policy must first match the
new hosting, source/draft processing and verified backup/deletion procedures. The initial service is synthetic-only.

Turning sending off stops capture, clears pending source and persists a forget request. The server revokes the
installation generation, removes its archive/derived contributions and prevents late uploads or jobs from restoring
them. An HTTP 202 deleting response is not final confirmation. A new generation is created only after an explicit
subsequent enabling action. Source identities are unrelated to site accounts and are never put in request URLs.

The target retention is 180 days, subject to the configured service/archive budget and a verified capacity plan.
Disk pressure rejects new intake for retry; it must not acknowledge data it discarded. Pilot deployment has an
explicit storage ceiling. Server backups and restore procedures must preserve revocation/deletion semantics.

## Decoder and aggregate eligibility

`tools/Hearthpulse.SourceWorker` is a standalone .NET 10 command with a self-contained Linux build. It validates
the JSONL envelope, numeric/card/tag restrictions, visibility, boundaries, sequence, source/line/record budgets,
choices and state, then writes an atomic versioned derived result. Error output contains fixed technical codes,
never source snippets or private paths. See [the CLI contract](../tools/Hearthpulse.SourceWorker/README.md).

The decoder hash identifies exact source bytes; `parserVersion` identifies its interpretation. Reprocessing
replaces the contribution for a capture rather than adding a second game. Publication must recheck that the source
generation has not been revoked, even when the worker has already finished.

Structural completeness and completed-game eligibility are separate. `completeForAggregation` requires a normal
complete game without unresolved observations and the appropriate outcome: W/L/T for Ranked/Casual/Arena/Brawl,
placement 1–8 for solo Battlegrounds, or team placement 1–4 for Duos. A complete source without that outcome can
still preserve actions but cannot enter completed-match result aggregates. A draft is never a completed match.

Result v2 (`source-v2.0`) keeps that flag unchanged and adds per-metric eligibility ([ADR 0013](adr/0013-per-metric-eligibility.md)):
`eligibility.outcome` and `eligibility.offers` with fixed reasons, plus the `facts` they admit (the visible result or
place; the Battlegrounds hero offer at the moment of the pick). A v2 Battlegrounds game can be fit for the place and
the hero offer while remaining `partial`; v1 Battlegrounds sources are never fit (`source_v1`). Metric definitions:
[analytics-metrics.md](analytics-metrics.md).

## Verification boundaries

Tests cover synthetic sources, malformed/bounded input, hidden-reference rejection, receipts/retry/deletion and
deterministic reprocessing. Separate acceptance requires real recorder input, both UI hosts and Linux/HTTPS
deployment. A cross-publish is not proof of Linux execution, and synthetic fixtures are not live-game coverage.
