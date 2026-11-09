# P3 handover — source decoder and standalone worker

Completed 2026-11-09 in assigned worktree branch `t3/source-capture-parser`, baseline
`ee6d98f96fba7d123109f8e5a3a93fbc16441444`. No push, PR, merge, deployment, child agents,
personal logs, application launch or network test requests. Commit SHA is supplied in the implementation reply.

Owned files only:

- `src/Hearthpulse.Core/SourceCapture/Processing/SourceCaptureResult.cs`
- `src/Hearthpulse.Core/SourceCapture/Processing/SourceCaptureSchema.cs`
- `src/Hearthpulse.Core/SourceCapture/Processing/SourceCaptureParser.cs`
- `src/Hearthpulse.Core/SourceCapture/Processing/SourceCaptureState.cs`
- `src/Hearthpulse.Core/SourceCapture/Processing/SourceWorkerCommand.cs`
- `tests/Hearthpulse.Tests/SourceCaptureParserTests.cs`
- `tools/Hearthpulse.SourceWorker/{Hearthpulse.SourceWorker.csproj,Program.cs,README.md,handover-P3.md}`

No existing csproj, solution, UI, composition, server or shared documentation changes.
The SDK includes the new Core and test files through its existing default compile globs.

## Public integration API

Namespace `Hearthpulse.Core.SourceCapture.Processing`, existing `Hearthpulse.Core` assembly:

```csharp
public sealed class SourceCaptureParser
{
    public const string ParserVersion = "source-v1.0";
    public SourceCaptureParser(SourceCaptureLimits? limits = null);
    public Task<SourceCaptureResult> ParseAsync(Stream input, CancellationToken cancellationToken = default);
}

public static class SourceWorkerCommand
{
    public static string Serialize(SourceCaptureResult result);
    public static Task<int> RunAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken = default);
}

public sealed record SourceCaptureLimits(
    long MaxSourceBytes = 256L * 1024 * 1024, int MaxLineBytes = 64 * 1024,
    int MaxRecords = 100_000, long MaxDerivedBytes = 64L * 1024 * 1024);
```

`SourceCaptureResult` has `V, ParserVersion, SourceSha256, Status, Records, Unsupported, Gaps,
Events, Summary`. Events have `Seq, Kind, Turn, Side, Entity, Card, Target, Data` (`JsonElement`).
Summary has `Plays, Buys, Sells, Choices, Turns, ShopSnapshots, Resources, DraftChoices,
CaptureKind, Mode, CaptureStatus, HasEnd, CompleteForAggregation, Result, Placement`.
The source record types/signatures are in `SourceCaptureResult.cs`. Output uses camelCase.
`ParseAsync` leaves its input open; failed calls do not poison reuse. Invalid input throws a safe
`SourceCaptureException` whose `Code` and message are fixed technical values. No P1 DTO dependency.

## P2/P4 process integration

Start the configured executable without shell interpolation:

```text
dotnet Hearthpulse.SourceWorker.dll --input <resolved-private-jsonl-file> --output <private-result-file>
```

Linux self-contained invocation after copying the whole publish directory:

```text
/opt/hearthpulse-source-worker/Hearthpulse.SourceWorker --input /private/job/source.jsonl --output /private/job/result.json
```

Both arguments are mandatory, unique and reorderable. No additional arguments. Input must be exact
concatenation of verified uncompressed chunks. The destination directory must exist and be private.
The result file is flushed, closed and renamed atomically; Unix files are created with mode 0600.
Exit `0` for parsed/partial, `2` invalid arguments/source/version/budget, `3` cancellation,
`4` I/O/permission/path failure, `1` unexpected failure. Stdout is one safe technical JSON object;
stderr is empty. Errors never print source text, identity, cards, exception detail or paths.
An old output survives failed processing. Gate output consumption on exit 0 and `ok:true`.

Before activating a result, P2/P4 must compare `sourceSha256` with the verified archive and recheck
generation/revoke state. Replace the previous contribution for that capture/version; do not append.
The worker has no identity, database or generation store and cannot enforce server lifecycle decisions.

## Implemented semantics

- Exact compact UTF-8 JSONL v1, without BOM/CRLF/blank lines/torn final line. Strict required field sets,
  duplicate-key checks at every object level, allowlisted numeric tags, card syntax and bounded data.
  First record start, seq contiguous from zero, monotonic turns, terminal end or explicit gap; no record after end.
- Source hash covers exact input bytes and LF, derived data is normalized to deterministic key order;
  no clocks, random IDs or machine paths in output. Parser version is fixed and must change with semantics.
- Explicit local actions produce play/buy/sell/etc facts and counts, retaining cost null when unknown.
  Blocks are retained without creating a second action. TURN/resource tags produce turn/resources facts.
- Shop entity/snapshot facts retain visible card order, positions, scoped refs and numeric tags.
  Only a snapshot with explicit shop cards increments shopSnapshots; an empty/general board snapshot
  makes no empty-shop or coverage claim.
- Choices retain alternatives, duplicates, source card, ordered picks and resolution. Repeated same ID
  counts once and must not contradict the options/resolution. No selected-card-to-instance inference.
- Draft packages and chosen index are retained; draft summary has no game result or completed-run claim.
- Hidden opponent cards are rejected, hidden structural records retain no identity, old refs cannot bridge
  hidden transitions or become named by changing side. Opponent standalone numeric card tags require a
  known public ref, except structural position. Opponent shops and nonpublic partner zones are rejected.
- Unknown safe archived discriminator with empty data becomes a fixed `unsupported` event and partial;
  arbitrary unknown data is rejected. OTHER blocks and unknown actions produce explicit gaps/unsupported.
- Gaps, unresolved choices, non-normal/incomplete ends, missing end with terminal gap, and open blocks
  remain partial. Result/placement are null in an incomplete summary. Aggregate only through
  `summary.completeForAggregation`; raw end observations retain the source's safe claims.

Budget and conservative numeric/domain bounds are explicitly listed in README. Hard budgets can
only be lowered. Budget exhaustion is an explicit failed job, never a successful empty match.
Gaps sum explicit lost-record counts plus parser-detected gaps; unsupported counts observations.
`summary.turns` is the maximum observed turn, not a reconstruction of all missing turns.

## Verification

Windows, .NET SDK `10.0.302`, strict analyzers enabled, `-m:2` throughout.
Final focused suite: **Passed: 67, Failed: 0, Skipped: 0** (20 test methods, theories expanded).
The resulting TRX was parsed, all 67 outcomes were confirmed Passed and test names checked for control characters.
Clean tool build (`--no-incremental`) reported **0 warnings, 0 errors**.
Linux x64 Release self-contained publish succeeded with analyzers; ELF apphost and bundled CoreCLR/hostfxr verified.

Exact successful commands from the checkout root:

```powershell
dotnet restore tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --source https://api.nuget.org/v3/index.json --artifacts-path C:\hp-p3 -m:2
dotnet test tests/Hearthpulse.Tests/Hearthpulse.Tests.csproj --no-restore --artifacts-path C:\hp-p3 -m:2 -p:HEARTHPULSE_STRICT=true --filter 'FullyQualifiedName~SourceCaptureParserTests' --logger 'trx;LogFileName=p3.trx' --results-directory C:\hp-p3\results
dotnet restore tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --source https://api.nuget.org/v3/index.json --artifacts-path C:\hp-p3 -m:2
dotnet build tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --no-restore --artifacts-path C:\hp-p3 --no-incremental -m:2 -p:HEARTHPULSE_STRICT=true
dotnet restore tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --source https://api.nuget.org/v3/index.json --artifacts-path C:\hp-p3-linux -r linux-x64 -p:SelfContained=true -m:2
dotnet publish tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --no-restore --artifacts-path C:\hp-p3-linux -c Release -r linux-x64 --self-contained true -o C:\hp-p3\linux -m:2 -p:HEARTHPULSE_STRICT=true
python C:\hp-p3\smoke.py
```

Standalone process smoke made four real `dotnet ...SourceWorker.dll` calls: initial result, identical
repeat, malformed-source failure preserving the previous result, then successful recovery. Checked exact
stdout status, empty stderr, independent Python SHA-256, alternatives/pick, counts, byte-identical result,
and absence of temp files. All passed. Smoke/script/TRX/publish artifacts live outside the checkout:
`C:\hp-p3\smoke.py`, `C:\hp-p3\smoke\`, `C:\hp-p3\results\p3.trx`, `C:\hp-p3\linux\`.

All names below are in `Hearthpulse.Tests.SourceCaptureParserTests`, all listed cases passed:

| Requirement | Evidence |
|---|---|
| AC2 ordered alternatives/actions/shop/resources/turns | `Choice_actions_shop_resources_and_turns_preserve_order_and_local_counts` (1) |
| AC5 deterministic hash/version and culture-independent repeat | `Reprocess_is_byte_stable_culture_independent_and_hashes_exact_source` (1) |
| Streaming record boundaries/hash/input ownership | `Fragmented_reads_preserve_hash_record_boundaries_and_leave_input_open` (1) |
| Strict framing/envelopes/seq/start/end/version/turn | `Malformed_envelopes_sequences_and_boundaries_fail_closed` (19) |
| AC1/P3 privacy, hidden identity, unsafe fields/tags | `Private_fields_hidden_cards_and_reference_links_are_rejected` (14); `Hidden_structure_and_fresh_public_reference_do_not_restore_hidden_identity` (1) |
| AC11 gaps/unsupported/unresolved are honest partials | `Gaps_unsupported_and_unresolved_sources_never_claim_complete_aggregation` (6) |
| Incomplete/terminal-gap recovery without invented result | `Terminal_gap_and_incomplete_end_preserve_observations_without_inventing_result` (1) |
| AC9 draft package/pick preservation without game result | `Draft_packages_and_selected_index_are_preserved_without_game_result` (1) |
| Choice multiset membership, duplicate tags, draft index | `Invalid_choice_membership_duplicates_and_draft_indices_are_rejected` (5) |
| Before/at source-byte/line/record budget boundary | `Source_line_and_record_budgets_accept_exact_limit_and_reject_next_byte_or_record` (3) |
| Hard line limit/derived memory/invalid UTF-8 | `Oversized_line_derived_budget_and_invalid_utf8_fail_without_partial_result` (1) |
| At/after options/picks/snapshot/card/ref/turn bounds | `Contract_collection_and_identifier_limits_accept_boundary_and_reject_one_more` (6) |
| No action duplication from blocks; supported action facts | `Public_blocks_and_supported_actions_are_observations_not_inferred_extra_plays` (1) |
| Same choice counted once, contradictions rejected | `Repeated_resolved_choice_is_counted_once_and_conflicting_resolution_is_invalid` (1) |
| Known missing block boundary/empty snapshot quality | `A_gap_explains_missing_block_boundaries_and_empty_snapshot_does_not_invent_shop_coverage` (1) |
| Atomic output, failed rename/parse, cleanup, repeat/recovery, safe stdout | `Cli_repeats_atomically_and_recovers_after_invalid_input_or_failed_output` (1) |
| Strict CLI arguments, same file, missing source, cancellation | `Cli_rejects_duplicate_arguments_same_file_missing_input_and_cancellation_safely` (1) |
| Partial exit 0 and exact safe process status | `Cli_partial_source_returns_zero_and_only_safe_technical_status` (1) |
| Decoder state recovery after a failed job | `A_failed_job_does_not_poison_reuse_of_the_decoder` (1) |

Synthetic process result example (full events are in the private result file; this excerpt omits events):

```json
{"v":1,"parserVersion":"source-v1.0","sourceSha256":"2d8e22c6a42cc2716f4a5ba0533b1572ed8bdbf400c6332df01153040ac5363b","status":"parsed","records":4,"unsupported":0,"gaps":0,"summary":{"plays":1,"buys":0,"sells":0,"choices":1,"turns":2,"shopSnapshots":0,"resources":0,"draftChoices":0,"captureKind":"game","mode":"ranked","captureStatus":"complete","hasEnd":true,"completeForAggregation":true,"result":"W","placement":null}}
```

## Integration limits and remaining evidence

- P1 recording/projection, P2 archive/hash/revoke/version replacement, P4 UI/composition/solution/docs/legal,
  full Core/Avalonia/Presentation runs and deployment are intentionally outside these owned changes.
- No Linux process was executed here: Windows cross-publish/package evidence is not Linux runtime/staging proof.
  The parent should execute the synthetic smoke using the Linux apphost before service activation.
- No claim of real-log/live draft coverage, complete game mechanics, live privacy capture, server restart,
  generation races, retention or physical power-loss recovery. A server-verified archive and P1 visibility gate
  remain required; a decoder cannot recover or verify fields that were never legitimately projected.
- No stubs on parser/CLI paths. Unsupported data is explicitly partial or invalid, never silently treated as zero.
