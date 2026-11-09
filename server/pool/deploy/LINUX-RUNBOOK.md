# Isolated Linux staging (P4 operates; P2 does not deploy)

This is a separate single-writer Node API, not an upgrade of the site's runtime, database, nginx server or Docker
volumes. Default bind is `127.0.0.1:3181`. P4 selected `https://api.kolodahearthstone.com/stats` on the existing
TLS/nginx server on 151, stripping `/stats` before forwarding. No new DNS or other edge configuration is needed.
The latest P4 report is 44 GiB free; retain the low pilot cap and do not clean up existing host data.
Start with synthetic data in a separate data directory; no personal Power.log/Net.log is used by this service.

## Prepare a dedicated runtime and account

Example for Linux x64. Choose a reviewed patched Node **24** build, pinned below to the tested runtime `24.19.0`.
Do not change `/usr/bin/node`, apt alternatives, npm global packages or any existing site service. Download/extract
into a new staging directory first, verify the official checksum, then copy only that version into `/opt`:

```sh
node_version=24.19.0
runtime_stage=$(mktemp -d)
cd "$runtime_stage"
curl --fail --location --output node.tar.xz "https://nodejs.org/dist/v${node_version}/node-v${node_version}-linux-x64.tar.xz"
curl --fail --location --output SHASUMS256.txt "https://nodejs.org/dist/v${node_version}/SHASUMS256.txt"
expected=$(awk -v n="node-v${node_version}-linux-x64.tar.xz" '$2 == n {print $1}' SHASUMS256.txt)
test -n "$expected"
printf '%s  node.tar.xz\n' "$expected" | sha256sum --check -
tar -xf node.tar.xz
sudo install -d -m 0755 /opt/hearthpulse-pool/runtime
sudo cp -a "node-v${node_version}-linux-x64" /opt/hearthpulse-pool/runtime/
/opt/hearthpulse-pool/runtime/node-v24.19.0-linux-x64/bin/node --version
```

Prepare the dedicated `hearthpulse-pool` system user/group through the host's normal account-management process.
Copy the reviewed checkout under `/opt/hearthpulse-pool/app` (read-only for that account). There are no npm
dependencies or install step. Set `/var/lib/hearthpulse-pool` to owner `hearthpulse-pool`, mode `0700`; it must be
outside every web root, object-public mount and website backup. Place `pool.env.example` as
`/etc/hearthpulse-pool/pool.env`, mode `0600`, root-owned. Generate the operator token locally, never in a repo,
shell history or a chat. Leave worker variables unset until P3 is published. A missing worker leaves `stored`.

Apply the supplied unit only as a new service after checking its paths. It runs the pinned Node executable with
UMask 0077 and a private data root. `systemctl daemon-reload` / starting it / enabling it are P4 deployment actions.
No command in this document authorizes changing other services. A second process using the same data root fails
closed on an OS SQLite lock held in `instance.lock.sqlite`. Process exit releases it automatically, including
abrupt termination. Keep the data root on a local filesystem with reliable SQLite locking; never delete or
replace the lock database while any service instance runs.

## Real .NET worker

P3 owns `tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj`. Publish that real CLI from the integrated
checkout, using the configured SDK, with warnings treated as errors and build parallelism two:

```sh
dotnet restore tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --source https://api.nuget.org/v3/index.json --artifacts-path /tmp/hp-worker-build -r linux-x64 -p:SelfContained=true -m:2
dotnet publish tools/Hearthpulse.SourceWorker/Hearthpulse.SourceWorker.csproj --no-restore --artifacts-path /tmp/hp-worker-build -c Release -r linux-x64 --self-contained true -o /tmp/hp-worker-publish -m:2 -p:HEARTHPULSE_STRICT=true -warnaserror
```

Install the whole self-contained publish directory read-only under `/opt/hearthpulse-pool/worker`. P4 fixes the
executable path in the environment; no runtime upgrade or shell command is needed. The API accepts the validated
reported parser version (including the parent's corrected `source-v1.1`), never a hardcoded `source-v1.0`:

```ini
POOL_WORKER_EXECUTABLE=/opt/hearthpulse-pool/worker/Hearthpulse.SourceWorker
POOL_WORKER_ARGS='[]'
```

The Node service appends **only** `--input <generated private path> --output <generated private path>` and uses
`spawn` with `shell:false`. Paths and commands are never accepted in an HTTP body. One worker at a time; 120 s
timeout; stdout/stderr capped at 16 KiB each and never forwarded/logged. Output JSON is capped at 64 MiB, validated
against its source and source hash, then replaces the active SQL result in a transaction. A crash/bad output
becomes `failed`; the archive remains for explicit reprocess. Restart requeues interrupted `processing` jobs.
The test subprocess fixture is not a production parser and must never be configured in deployment.

## Proxy and synthetic verification

P4 adds the scoped locations from `nginx-source-capture.conf` to the existing `api.kolodahearthstone.com` TLS server,
without replacing other API routes, DNS or TLS settings. They strip `/stats`, turn proxy caching off, and expose
no archive alias/static location. The proxy rejects reprocess, strips Authorization, does not buffer request bodies to its
disk, and disables access logging. Do not configure request-body logging at a WAF/monitoring layer. Decide the
public rate limit and staging allowlist before opening it. Check `nginx -t` before any P4-authorized reload.

```sh
curl --fail http://127.0.0.1:3181/healthz
/opt/hearthpulse-pool/runtime/node-v24.19.0-linux-x64/bin/node server/pool/deploy/synthetic-smoke.mjs http://127.0.0.1:3181
/opt/hearthpulse-pool/runtime/node-v24.19.0-linux-x64/bin/node server/pool/deploy/synthetic-smoke.mjs https://api.kolodahearthstone.com/stats
```

Set the operator token in the command environment through the host's secret mechanism to include loopback
reprocess. The same script over HTTPS tests upload/manifest/worker/status/forget but must run without that token
because the public proxy blocks reprocess. It prints technical counts only. HTTP 202 `deleting` is a pending
deletion, never success. Check status after service restart, repeat smoke, confirm the synthetic archive directory
and SQL facts are gone. Exercise abrupt restart, interrupted processing, quota 503/Retry-After and delayed upload
410 before opening the pilot. The default 256 MiB archive/128 MiB per day are deliberately small staging caps.

## Layout, cleanup, restoration and deletion

- `pool.sqlite` (+ WAL/SHM): legacy tables plus `source_*`; FULL synchronous WAL, foreign keys, secure_delete.
- `private/captures/<SHA256 generation>/<SHA256 capture>/<index>.jsonl.gz`: verified typed source only, fresh
  gzip without client filenames/comments. Input/output work files live inside that private generation.
- `private/revocations.jsonl`: fsynced append-only **independent revocation ledger** with generation hash and time.
  Keep it outside rollback of older SQL/archive backups; retain it indefinitely for the initial pilot. Restore
  the current ledger first, then start the API: it revokes/removes restored old captures before listening.
  Never restore an older ledger over the current one. Copying a stale SQL backup alone is not a correct restore.
- Permanent SQL capture tombstones stop expired capture IDs returning. Finished source TTL is 180 days maximum;
  unfinished uploads default 24 hours. Maintenance runs every minute and at startup, retries pending deletion,
  expires source/derived together, cleans old quotas and checkpoints WAL. No original logs are retained.

Forget fsyncs the independent tombstone first, stops the matching worker, deletes source receipts/manifests/facts,
removes the generation directory and checkpoints WAL. Physical cleanup failure answers 202 and keeps the generation
revoked; retry or maintenance finishes it. Late uploads/worker commits always check the tombstone. Backups need the
same deletion policy: this service cannot erase disconnected backups, filesystem snapshots or journals owned by a
different system. Do not promise backup erasure until P4 has verified the actual host's backup process.

The cap conservatively reserves uncompressed input + compressed bytes + derived JSON + bounded running output.
Filesystem free space is also checked before intake. Monitor health counts, disk/WAL growth, worker failures and
503 rates without IDs or source snippets. Directory/SQL orphan cleanup runs before listening after a crash.
Provision larger capacity only after measured synthetic/pilot mode-specific size, parser time and retention
estimates; the 180-day TTL is an upper retention limit, not a capacity guarantee.

## Local checks

From the checkout root with the dedicated Node 24 binary:

```sh
node --test server/pool/test/*.test.mjs
```

These tests use generated safe records, temp directories, real local HTTP/SQLite/files and test worker processes.
P4 must separately validate the **real P3 .NET CLI**, Linux filesystem permissions, systemd, nginx/TLS and remote
staging restart/deletion. Local Windows tests do not establish production or Linux runtime evidence.
