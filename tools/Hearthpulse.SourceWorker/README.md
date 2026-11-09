# Hearthpulse source worker

Standalone .NET 10 decoder for the **typed visible projection** in
`TASK-2026-11-09-source-capture/task.md`. It references existing Core only; no P1 DTO,
UI, Power.log text fallback, JavaScript decoder, application data folder or network is used.

## Build and publish

From the checkout root (use a short, private artifacts directory):

```powershell
dotnet restore tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --source https://api.nuget.org/v3/index.json --artifacts-path C:\hp-p3 -m:2
dotnet build tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --no-restore --artifacts-path C:\hp-p3 --no-incremental -m:2 -p:HEARTHPULSE_STRICT=true
```

Linux x64, including runtime (run these commands on the build host; the publish directory is portable):

```powershell
dotnet restore tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --source https://api.nuget.org/v3/index.json --artifacts-path C:\hp-p3-linux -r linux-x64 -p:SelfContained=true -m:2
dotnet publish tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --no-restore --artifacts-path C:\hp-p3-linux -c Release -r linux-x64 --self-contained true -o C:\hp-p3\linux -m:2 -p:HEARTHPULSE_STRICT=true
```

Use the host's own scratch paths on Linux. The tool is included in `Hearthpulse.slnx` for integration builds.

## Invocation and process contract

```text
dotnet Hearthpulse.SourceWorker.dll --input /private/job/source.jsonl --output /private/job/result.json
/opt/hearthpulse-source-worker/Hearthpulse.SourceWorker --input /private/job/source.jsonl --output /private/job/result.json
```

Both options are required exactly once, in either order. No other arguments are accepted.
Input is the exact byte concatenation of already verified, decompressed chunks; no manifest,
gzip or raw log is decoded here. Paths come from the server's resolved private job configuration.
The output directory must already exist and must be accessible only to the service user.
Use distinct source/result paths and a separate output per job. The worker never edits its input.

Stdout is exactly one safe JSON status line; stderr is empty. Success example:

```json
{"ok":true,"status":"parsed","parserVersion":"source-v2.0","records":4,"unsupported":0,"gaps":0}
```

Failure example:

```json
{"ok":false,"status":"failed","error":"invalid_source"}
```

Exit codes: `0` parsed/partial, `2` invalid arguments/source/unsupported version/budget,
`3` cancellation, `4` filesystem/permission/path error, `1` unexpected failure.
Fixed error discriminators are `invalid_arguments`, `invalid_source`, `unsupported_version`,
`limit_exceeded`, `cancelled`, `io_error`, `worker_failed`.
No path, source excerpt, card, capture identity or exception detail is printed.

The output file is compact UTF-8 JSON with no BOM. A new same-directory temp file is flushed
to disk, closed, then atomically renamed over the destination. Unix temp/output mode is `0600`.
Failed parses and failed writes preserve an existing result and remove the temp file when possible.
The caller **must** require exit `0` and a success status before reading/committing that job's output;
an older result file left after a failure is not a new result.

## Decoder API and semantics

Namespace: `Hearthpulse.Core.SourceCapture.Processing`.

```csharp
new SourceCaptureParser(SourceCaptureLimits? limits = null);
Task<SourceCaptureResult> ParseAsync(Stream input, CancellationToken cancellationToken = default);
SourceCaptureParser.ParserVersion; // "source-v2.0"
SourceWorkerCommand.Serialize(SourceCaptureResult result); // stable camelCase JSON
Task<int> SourceWorkerCommand.RunAsync(string[] args, TextWriter stdout,
    CancellationToken cancellationToken = default);
```

The parser leaves the input open, keeps no state between jobs, and throws `SourceCaptureException`
with a fixed `Code` on invalid source. `SourceCaptureResult` (result `v:2`) contains, in this order, `v`,
`parserVersion`, `sourceVersion`, `sourceSha256`, `status`, `records`, `unsupported`, `gaps`, `events`, `summary`,
`context`, `eligibility`, `facts` (see "Source v2 and result v2" below).
`sourceSha256` hashes the original bytes, including LF; derived data keys are sorted ordinally.
Reprocessing the same bytes with this version gives byte-identical output, independent of path/time/culture.
No aggregation store is touched. The server owns replacement of the old contribution and generation/revoke checks.
Before committing a result, the server must compare its source hash with the verified archive and recheck the generation.

Each event has `seq,kind,turn,side,entity,card,target,data`. All copied fields are validated;
scoped refs cannot be followed across a concealed opponent transition. Hidden opponent entities
carry null cards and structural position tags only. Partner cards must be public board/graveyard cards.
Source must begin with `start`, have contiguous seq from zero, nondecreasing turn, and end with
`end` or an explicit terminal `gap`. Nothing is accepted after `end`; no post-game replay records are supported.
Duplicate/extra/missing properties, unknown tags, arbitrary raw fields, unsafe cards, invalid UTF-8,
BOM, CRLF, blank lines, whitespace outside compact JSON, torn final lines and unsupported versions fail closed.

Source actions become `play`, `buy`, `sell`, `refresh`, etc. Only explicit **local** play/buy/sell
actions increment their counters; blocks do not invent another action. `TURN` becomes `turn`,
resource/overload tags become `resources`; numeric values and null unknown costs are preserved.
Shop entities and snapshots containing shop cards become `shop`; a generic/empty snapshot
cannot prove an empty shop was observed. Other supported records retain their kind.
Choices preserve ordered alternatives, duplicates and picks; summary counts distinct choice IDs.
Repeated resolutions must agree. Draft events preserve package contents and selected option index,
and never produce a game result or a claim of a completed run.

`summary` includes plays, buys, sells, choices, highest observed turns, shopSnapshots, resources,
draftChoices, captureKind, mode, captureStatus, hasEnd, completeForAggregation, result, placement.
Only a normal complete game with no gaps/unsupported/unresolved choices and a mode-appropriate outcome has
`completeForAggregation=true`: W/L/T for Ranked, Casual, Arena and Brawl, placement 1–8 for solo BG, or team
placement 1–4 for Duos. A structurally complete source with no outcome still parses and retains its observations,
but is not eligible for completed-game aggregates. Drafts are never completed-game contributions.
Use this summary gate, not a raw end observation, for completed-game aggregates.
Incomplete sources have null summary result/placement, even if their end observation claimed a result.
`parsed` means a fully supported, complete source; `partial` also covers explicit incomplete capture,
unresolved choices, open/missing blocks explained by gaps and unsupported observations.
This implementation conservatively reports partial for incomplete capture even when all records are understood.

`gaps` sums explicit missing counts plus parser-detected losses. `unsupported` counts unsupported
observations, not missing source rows. An OTHER block, unknown action or an archived future record
with empty data yields partial; unknown kind is normalized to `unsupported` and is not echoed.
Arbitrary future data is rejected. New uploads should use the specified `gap/unsupported` shape.

## Source v2 and result v2

The parser reads source `v:1` and `v:2`. All records of one source carry the same `v`, equal to the upload's
`manifest.sourceVersion`; a mix, or `v` other than 1/2 (`unsupported_version`), fails closed. Source v2 differs from v1
only in three places, and each v2-only field is rejected in a v1 record:

- `start.data` has exactly `captureKind, mode, day, build, format, arena`. `format` is `standard|wild|classic|twist`
  or null and may be non-null only for `ranked`/`casual`; `arena` is `normal|underground` or null and may be non-null
  only for `arena` (game or draft capture).
- `choice.data` adds `choiceType`: `mulligan|general|other`. It may not change between records of one `choiceId`.
- `gap.reason` may be `combat_view`: the Power state runs ahead of the displayed BG combat. It is a structural marker
  written once in the header of every v2 `bg`/`duos` game capture (before any observation); it is still a gap, so such a
  source stays `partial` and `completeForAggregation=false`.

Result v2 keeps `events` and `summary` exactly as before and adds the per-metric layer (ADR 0013):

```jsonc
"context":     { "mode": "bg", "day": 20766, "build": 123, "format": null, "arena": null },   // from start; v1: format/arena null
"eligibility": { "outcome": true, "offers": true, "reasons": [] },
"facts":       { "outcome": { "result": "L", "placement": 3 },                               // null when outcome=false
                 "offers": [ { "seq": 3, "kind": "bg_hero", "options": ["..."], "picked": ["..."] } ] } // [] when offers=false
```

`reasons` come from `source_v1, draft, no_end, end_reason, lifecycle_gap, no_outcome, no_offer, offer_unresolved,
offer_shape`, in that order, without repeats.

- `outcome` (v2 game): an `end` with `reason:"normal"`, no `late_attach|rotation|shutdown` gap and a mode outcome
  (`bg` place 1–8, `duos` 1–4, otherwise W/L/T). Other gaps (`combat_view`, `unsupported`, `limit`, `invalid`,
  `source_missing`) do not spoil it. v1: `outcome = summary.completeForAggregation`, except BG/Duos v1, which is always
  `false` with `source_v1` (v1 cannot tell the structural BG gap from a real one).
- `offers` (v2 `bg`/`duos` only): the same lifecycle rules without the outcome; the first choice by `choiceId` is typed
  `mulligan`, its last record is resolved with 2–4 options and exactly one pick. The fact keeps the options at the
  moment of the pick (heroes rerolled away are not in it) and `seq` of that resolving record. Other modes: `no_offer`.
- Draft captures: `outcome=false, offers=false, reasons=["draft"]` (v1 drafts add `source_v1`).

The server re-checks every fact against the source rows before storing it (task contract C2).

## Budgets and validation

Hard ceilings: source 256 MiB, line 64 KiB before LF, JSON depth 16, 100,000 records/references,
64 MiB canonical event-data budget (includes 256 bytes overhead per record), 64 nested blocks.
`SourceCaptureLimits` can lower source/line/record/derived budgets only. Exceeding a budget fails
the job explicitly; it cannot silently produce an empty successful game.
Wire bounds: turn 0..1000, refs/choice IDs 0..1,000,000, card IDs 1..80 ASCII alphanumeric/underscore,
64 choice options/16 picks, 256 snapshot cards. Conservative v1 domain bounds also apply:
day 0..100,000, positive build through Int32.MaxValue, position/pick 0..1000, placement 1..8,
tier 1..10, cost/gold 0..1,000,000, integer tag values -1,000,000..1,000,000,
draft up to 64 alternatives with up to 64 cards per package. Choice picks must be a multiset subset
of the offered alternatives. Counts cannot overflow Int32. Source gaps retain explicit reasons.

Focused verification:

```powershell
dotnet restore tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --source https://api.nuget.org/v3/index.json --artifacts-path C:\hp-p3 -m:2
dotnet test tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --no-restore --artifacts-path C:\hp-p3 -m:2 -p:HEARTHPULSE_STRICT=true --filter 'FullyQualifiedName~SourceCaptureParserTests' --logger 'trx;LogFileName=p3.trx' --results-directory C:\hp-p3\results
```

These are synthetic projections. Recorder visibility, live draft signal coverage, upload/archive
verification, source retention/deletion, aggregate replacement, Linux execution and deployment remain
integration evidence owned by P1/P2/P4; a successful parser does not establish those claims.
