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

Use the host's own scratch paths on Linux. The tool is deliberately absent from the solution until P4 integrates it.

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
{"ok":true,"status":"parsed","parserVersion":"source-v1.0","records":4,"unsupported":0,"gaps":0}
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
SourceCaptureParser.ParserVersion; // "source-v1.0"
SourceWorkerCommand.Serialize(SourceCaptureResult result); // stable camelCase JSON
Task<int> SourceWorkerCommand.RunAsync(string[] args, TextWriter stdout,
    CancellationToken cancellationToken = default);
```

The parser leaves the input open, keeps no state between jobs, and throws `SourceCaptureException`
with a fixed `Code` on invalid source. `SourceCaptureResult` contains `v`, `parserVersion`,
`sourceSha256`, `status`, `records`, `unsupported`, `gaps`, `events`, `summary`.
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
Only a normal complete game with no gaps/unsupported/unresolved choices has
`completeForAggregation=true`. Use this summary gate, not a raw end observation, for completed-game aggregates.
Incomplete sources have null summary result/placement, even if their end observation claimed a result.
`parsed` means a fully supported, complete source; `partial` also covers explicit incomplete capture,
unresolved choices, open/missing blocks explained by gaps and unsupported observations.
This implementation conservatively reports partial for incomplete capture even when all records are understood.

`gaps` sums explicit missing counts plus parser-detected losses. `unsupported` counts unsupported
observations, not missing source rows. An OTHER block, unknown action or an archived future record
with empty data yields partial; unknown kind is normalized to `unsupported` and is not echoed.
Arbitrary future data is rejected. New uploads should use the specified `gap/unsupported` shape.

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
