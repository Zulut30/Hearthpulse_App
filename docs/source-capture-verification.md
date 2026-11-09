# Source capture integration and pilot, 2026-11-09

Implementation: P1 `3ff4fbcc`, P2 `0d57016d`, P3 `5f0f351c`, integrated with the outcome gate and producer/consumer
contract repairs. This is a synthetic pilot, not public source activation. Card-data permission was recorded
separately in `50105a2f`. No release version or tag was created.

## Verified behavior

| Requirement | Evidence |
|---|---|
| Visible source only, no identity/hidden cards | `Hidden_cards_names_account_ids_paths_and_post_game_reveals_never_enter_source`; `Private_fields_hidden_cards_and_reference_links_are_rejected` |
| Choices/actions preserve context across producer and decoder | `Visible_shop_updates_and_buys_decode_with_local_actor_and_preserved_item`; `Rerolled_alternatives_and_incremental_multi_pick_resolution_preserve_history` |
| Crash recovery remains parseable and honestly partial | `Crash_recovery_after_a_nonzero_turn_produces_parseable_partial_source`; `Checked_in_battlegrounds_source_round_trips_from_recorder_to_decoder` |
| Complete source without a mode-specific outcome is excluded from completed-game aggregation | `A_complete_ranked_capture_without_result_is_not_eligible_for_completed_game_aggregates`; `A_complete_bg_capture_without_placement_is_not_eligible_for_completed_game_aggregates`; `Aggregate_eligibility_requires_the_outcome_of_the_specific_mode` |
| Durable receipts and retry | `Network_loss_and_wrong_receipt_keep_exact_source_until_verified_retry_then_manifest`; Node `durable receipt replay survives restart without duplicate chunks or quota` |
| New notice and old opt-out in both hosts | `Existing_users_see_the_expanded_notice_before_source_capture`; `An_existing_opt_out_is_preserved_until_the_user_explicitly_enables_sending` |
| Save failure cannot grant sending or undo withdrawal | `A_real_settings_write_failure_never_enables_source_capture`; `Opt_out_remains_revoked_after_restart_even_when_settings_cannot_be_saved` |
| Pending deletion stays visible; re-enable is explicit | `Pending_deletion_is_displayed_until_server_confirmation`; `Rapid_off_then_explicit_on_is_serialized_and_retains_old_generation_deletion` |
| Shutdown keeps the unpicked independent Arena draft | `Graceful_shutdown_preserves_the_open_unpicked_draft_offer`; `Arena_session_captures_pick_and_unpicked_close_even_with_the_helper_off_and_no_game` |
| Bounded intake/revocation metadata | Node `unknown generation deletion has a durable budget while known and repeated deletion remain available`; `Source_line_and_record_budgets_accept_exact_limit_and_reject_next_byte_or_record` |
| Real worker replaces results and cannot resurrect deleted data | Node `real dotnet worker preserves semantic facts replaces deterministically and cannot survive generation deletion` |

Strict Release solution build: **0 warnings, 0 errors**. Full Core suite: **3103 passed, 0 failed, 2 skipped**
(the external recorded-memory fixture and Unix-only session-permissions test on Windows). Presentation:
**227 passed, 0 failed, 0 skipped**. Latest focused host run: **7 passed**. Skill scripts: **48 passed**.
Linux Node 24: **114 passed, 0 failed, 0 skipped**, plus **2 passed** with the real self-contained .NET worker.
Core tests use `dotnet test tests/Hearthpulse.Tests -c Release --no-build --no-restore --artifacts-path C:\hp-int`;
Presentation uses the same command with `tests/Hearthpulse.Presentation.Tests`. TRX evidence is under
`C:\hp-int\results`; red-to-green reports retain the reproduced outcome, source-contract and lifecycle failures.

Both UI hosts rendered the expanded notice and Data settings in Russian and English from scratch copies without
account tokens. Dedicated frames: `C:\hp-int\source-renders`. Text/buttons/status were visually inspected.
These frames prove render behavior, not interaction during a live Hearthstone game.

## Live service

Endpoint: `https://api.kolodahearthstone.com/stats` on Debian 12 server 151, port 3181 loopback. Separate
`hearthpulse-pool.service`, service account and private `/var/lib/hearthpulse-pool` (0700), root-owned code under
`/opt/hearthpulse-pool`, private root-owned configuration. Node **24.19.0** was checked against its official SHA256;
the existing system Node and website processes were retained. Decoder **source-v1.2** actually ran on Linux.

The existing TLS vhost includes only the new scoped nginx snippet; its previous file is backed up in the private
staging directory. nginx configuration validation passed before reload (one pre-existing map-hash sizing warning).
Upload paths disable access/error logs, buffering and caching, and remove cookies, auth and IP-forwarding headers.
Public reprocess returns **403**; private archive requests return **404**; the site's root retains its redirect.
The existing Cloudflare edge remains in front of this origin; this does not establish that its independent logs
or backups have been erased.

Verified through loopback and public HTTPS: receipt/replay, manifest, actual .NET processing, status, authorized
loopback reprocess, deterministic result replacement, forget and repeat after service restart. A typed source
generated by the real P1 recorder from the checked-in Battlegrounds fixture contained **535 records**; it passed
HTTPS intake, produced an honestly **partial** result, reprocessed byte-identically and was deleted. Late upload
returned **410** both before and after restart. Archive files and SQL capture/derived rows were absent afterward;
only the independent revocation ledger remained, as required. No personal live game logs were uploaded.
The actual four-record spool recovery after a nonzero turn also passed HTTPS intake, reprocess, deletion and the
post-restart late-upload rejection.

Pilot limits: 256 MiB archive, 128 MiB daily input, 1000 daily captures; 64 MiB metadata ceiling, bounded unknown
revocations, one worker, 768 MiB service memory, file-size limit 128 MiB. Source retention is at most 180 days;
this is not a demonstrated capacity for 180 days of public traffic.

## Activation conditions and remaining limits

`PoolClient.DefaultEndpoint` remains empty. Public expanded sending must wait for the published privacy policy
to match the new host, draft/source processing and verified backup/deletion policy. Installer/in-app notices and
developer docs have been updated; the website policy has not been changed in this implementation.

Host backup/snapshot restoration and real-game installation coverage have not been certified. Restore must keep
the current independent revocation ledger, never roll it back with SQL. No claim of every mechanic, every mode
being complete, 100% telemetry, or public-launch readiness is made. A trusted worker's output is schema/source
validated; its output-size check occurs after exit, with the service file-size cap bounding runtime writes.
