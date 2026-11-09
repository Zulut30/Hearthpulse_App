---
name: hearthpulse-release
description: Use when preparing, cutting or debugging a Hearthpulse release - version numbers, tags vX.Y.Z, .github/workflows/release.yml, installers (NSIS setup, Avalonia preview zip, deb/rpm/tar.gz/AppImage, macOS dmg/pkg/zip), release notes, SHA256SUMS, and the in-app updater (UpdateService, GitHub repo Zulut30/Hearthpulse_App, asset names, digest check, staged install). Triggers - "релиз", "выпусти версию", "bump version", "tag", "installer", "update didn't arrive", "auto-update".
---

# Releasing Hearthpulse

Publishing is outward-facing and hard to undo: **tagging, pushing, running the release workflow or editing a GitHub
release happen only on the user's explicit word.** Everything up to that point (bump, notes, local checks) can be
prepared on a branch.

## How a release is built

`.github/workflows/release.yml` runs on a pushed tag `v*` or by hand (`workflow_dispatch`, input `version`).
The version must match `^\d+\.\d+\.\d+$` (a leading `v` is stripped). A trial run of a branch builds and checks every
installer without creating a release: `gh workflow run release.yml --ref <branch> -f version=0.0.0 -f publish=false`
(add `-f sign_selftest=true` to sign with throwaway keys, see "Signing"; it and `simulate_previous` refuse `publish=true`).
Each build job makes its packages in parallel from one publish (`installer/*/build.sh` wait for all of them).

| Job | Runner | Does |
|---|---|---|
| `tests` | ubuntu-latest | `dotnet test tests/Hearthpulse.Tests` on every release (a docs-only push skips CI's .NET steps); runs beside the builds, `release` waits for it |
| `build` (matrix, all in parallel) | ubuntu-latest / macos-latest | `installer/windows/build.sh V app` (NSIS `Hearthpulse-Setup-V-win-x64.exe` and `Hearthpulse-Portable-V-win-x64.zip` from WPF); `installer/windows/build.sh V preview` (`Hearthpulse-Preview-V-win-x64.zip` from Avalonia); `installer/linux/build.sh V x64` and `V arm64` (deb/rpm, `Hearthpulse-V-linux-{x64,arm64}.tar.gz`, `Hearthpulse-Portable-V-linux-{x64,arm64}.tar.gz`, AppImage); `installer/macos/build.sh V arm64 x64` on macOS -> `Hearthpulse-V-macOS-{arm64,x64}.{pkg,dmg,zip}` plus `Hearthpulse-Portable-V-macOS-{arm64,x64}.zip` (ad-hoc signed, not notarized); the Windows app job also writes `Hearthpulse-Files-V-win-x64.json` and, when the previous release has such a manifest, `Hearthpulse-Delta-P-V-win-x64.zip` (see Delta updates) |
| `release` | ubuntu | merges artifacts into `dist/`, checks every expected installer is there (and the manifest; the delta exactly when `installer/windows/previous.sh` finds a previous manifest), `sha256sum * > SHA256SUMS.txt`, checks the file count (23 without a delta, 24 with one), signs it (`installer/sign-sums.sh`: `SHA256SUMS.txt.sig`, one more file: 24 / 25), publishes with `softprops/action-gh-release@v2`: tag `vV`, name "Hearthpulse V", `body_path: installer/RELEASE_NOTES.md`, `fail_on_unmatched_files: true` |

It publishes into the repository the workflow runs in. Verify `git remote -v` and the intended GitHub repository
before publishing: the updater is configured for `Zulut30/Hearthpulse_App`. CI (`ci.yml`) is separate: build + tests
on Windows/Linux/macOS (Linux and macOS also publish the self-contained app and start it with `--smoke-test`, under
`xvfb-run` on Linux: exit 0 only when the card data loaded) and the pool API/Python skill checks on Linux for every push
to `main` and every PR.

`installer/RELEASE_NOTES.md` exists and supplies the release body. Update it for the version being built and check
that it describes the actual artifacts and changes before publishing.

## Where the version lives (bump all of them together)

| File | What |
|---|---|
| `src/Hearthpulse.App/Hearthpulse.App.csproj` | `<Version>` |
| `src/Hearthpulse.UI/Hearthpulse.UI.csproj` | `<Version>` |
| `src/Hearthpulse.Desktop/Hearthpulse.Desktop.csproj` | `<Version>` |
| `installer/windows/build.sh`, `installer/linux/build.sh`, `installer/macos/build.sh` | `VERSION="${1:-X.Y.Z}"` default |
| `installer/windows/hearthpulse.nsi` | `!define VERSION "X.Y.Z"` default |
| `.github/workflows/release.yml` | `workflow_dispatch` input default |

```
rg -n 'Version>|VERSION=|define VERSION|default:' src installer .github/workflows/release.yml
```

The scripts pass `-p:Version=V` to `dotnet publish`, so release builds are consistent even if a default is stale.
What a publish carries is set once in the root `Directory.Build.props` / `.targets`: satellite assemblies for `en;ru`
only and no `.pdb` (SkiaSharp/HarfBuzz alone ship ~105 MB of native symbols for win-x64). A file that leaves the
Windows publish stays behind on upgrade (NSIS `File /r` overwrites, the uninstall list names only current files), so
`hearthpulse.nsi` deletes such leftovers before copying.
`Hearthpulse.Core.csproj` has no `<Version>`: a plain local build can report Core as 1.0.0 independently of the app
version. `UpdateService.CurrentVersion` reads the **Core** assembly version, so always
test the updater on a build made with `-p:Version=...`, never on a plain `dotnet run`.

## Signing (release workflow)

Two independent signatures; both scripts run in trial runs too, so a trial run shows what a release would do.

- **Release signature** (what the app checks): `installer/sign-sums.sh dist <key file>` in the `release` job, after
  "Checksums" and before publishing. `SHA256SUMS.txt.sig` = one line, base64 (88 chars) of the 64-byte Ed25519
  signature (RFC 8032, pure) over the exact bytes of `SHA256SUMS.txt`; it is not listed in SUMS. Private key: secret
  `UPDATE_SIGNING_KEY` (PKCS#8 PEM, `openssl genpkey -algorithm ed25519`). Public keys: one file,
  `src/Hearthpulse.Core/Updates/update-signing-key.pub` (`#` comments + zero or more `BEGIN PUBLIC KEY` blocks, several
  for rotation), embedded in Core and read by the workflow. Rules: secret present -> sign, and at least one embedded key
  must verify it (else `::error::`; no embedded key -> checked with the secret's own key and a warning); **an embedded
  key without the secret fails a publishing run before publishing** (clients with the key would stop updating); a
  trial run without the secret only warns and makes no `.sig`. Once `Hearthpulse.Core.csproj` names
  `update-signing-key.pub` (P1 of TASK-2026-11-09-update-delivery), the workflow sets `KEY_FILE_REQUIRED=true`: a
  publishing run without the file fails (a rename must not switch the rule off). Tests:
  `tools/skill_checks/test_sign_sums.py`.
- **Transition.** 0.14.0 and older know no signature and take 0.15.0 by digest; that cannot be fixed. Signature
  checks start with the first build whose `.pub` has a key; from then on every release must be signed. Losing the
  private key means those clients update only by hand: keep two offline copies.
- **Authenticode** (what SmartScreen looks at): `installer/windows/sign.sh` with jsign (Apache-2.0, jar pinned by
  SHA-256, downloaded only when signing; Java is on the runner). Secrets, passed only to the `windows-*` build jobs:
  `WINDOWS_SIGN_PFX` (`base64 -w0 cert.pfx`) + `WINDOWS_SIGN_PFX_PASSWORD`, timestamp DigiCert; else Azure Trusted
  Signing `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_SIGNING_PROFILE`, `AZURE_TENANT_ID`,
  `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, timestamp Microsoft; else one
  `::warning::Windows packages are not code-signed (no signing secrets)` per job and an unsigned build. Signed:
  `Hearthpulse.exe` of the app (before `delta.py manifest`, the portable zip and NSIS - else the manifest would not
  match the installed files) and of the preview, the uninstaller (NSIS `!uninstfinalize`, 3.08+, only when
  `-DSIGN_SCRIPT` is passed) and the setup. Own DLLs are not signed. With secrets set, any failure (jsign download or
  pin, java, .pfx/password, Azure) **fails a publishing run** (`PUBLISHING` = tag or `publish=true`, passed to the
  Build step) and is a warning in a trial run. The .pfx password and the Azure token reach jsign as
  `--storepass env:HP_SIGN_STOREPASS`, never in argv. The jar pin was checked against Maven Central (`sign.sh`).
- **Permissions**: the workflow is `contents: read`; only the `release` job has `contents: write`, so the build jobs
  that see the signing secrets cannot write to the repository.
- **Selftest** (`sign_selftest=true`, trial runs only): Authenticode with a throwaway self-signed .pfx, each signer
  shown by `osslsigncode verify`, any failure fails the job; the SUMS step signs with a throwaway Ed25519 key, checks
  that a changed byte and another key are rejected, and (without the real secret) leaves its `.sig` so the count with a
  `.sig` is exercised.
- Not done yet: the client does not check the Authenticode publisher before `/S` (after a certificate exists).

## Updater (`src/Hearthpulse.Core/Updates/UpdateService.cs`)

- `CheckAsync(http)` -> `https://api.github.com/repos/Zulut30/Hearthpulse_App/releases/latest` (`RepoOwner`/`RepoName`);
  skips drafts and prereleases, strips `v` from `tag_name`, picks the asset whose name equals
  `AssetName(version, target, arch, linuxFlavor)` - the same names the installers produce. **Renaming an installer
  file breaks updates** for everyone on older versions.
- Target: `DetectTarget()` - `WindowsInstalled` (registry `HKCU\...\Uninstall\Hearthpulse` `InstallLocation` = app
  folder), `WindowsPortable`, `MacPackage`, `LinuxPackage` (deb / rpm / tar.gz by `LinuxFlavor()`).
- **Digest check**: GitHub's per-asset `digest` (`sha256:<64 hex>`) becomes `UpdateInfo.Sha256`;
  `MatchesDigestAsync` verifies the download (`.part` -> checked -> moved). A missing or invalid digest prevents
  downloading; a mismatch discards the file. Installation rechecks the staged file. Keep the missing-digest, mismatch
  and staged-file checks fail closed.
- **Signature** (builds with a key in `update-signing-key.pub`): before downloading, `SHA256SUMS.txt` +
  `.sig` are fetched; the signature must verify with an embedded key (own verify-only Ed25519 port of RFC 8032), the
  asset's line must exist and equal GitHub's digest. Anything else: nothing is downloaded, `Core.Update.Unsigned`
  (download from the releases page), the exact reason in the log. A delta whose line does not match is dropped; the
  "repair" path (`DeltaUpdate.IsBroken`) needs the signature too. A build without a key logs once per run "release
  signature not checked: this build carries no update key" and keeps the digest-only behaviour.
- **GitHub limits**: the last `releases/latest` answer and its ETag are kept in `<data>/update-state/latest-release.json`
  and sent as `If-None-Match` (304 = same answer, no rate cost). 403 with `X-RateLimit-Remaining: 0` / 429 with
  `Retry-After` -> `UpdateCheck.RetryAfter`; no request before it, manual or automatic. No fallback source (deferred).
- Staging: `<data>/updates/<asset>`; old downloads deleted; Windows `Zone.Identifier` mark cleared.
- Apply (Windows): `ApplyStaged` writes `%TEMP%\hearthpulse-update-<pid>.cmd` that waits for the app to exit (at most
  120 s, then installs anyway: NSIS closes a hung process), copies the app folder to `<app>.setup-backup`
  (`robocopy /MIR`; a failed copy installs without a backup, `backup=0`), runs `setup.exe /S`, retries once after ~5 s
  on a non-zero code, restores the backup after a second failure (`restored=1`), deletes it on success, writes
  `<data>/update-state/install-result.txt` (`exit=`, `attempts=`, `backup=`, `restored=`) and optionally relaunches.
  Portable copies the zip. macOS/Linux open the package (`open` / `xdg-open`).
- **Install report**: `pending-install.json` marks a started install; on the next start `TakeInstallReport()`
  compares the running version with it (version >= V = success; the exit code is a detail, so a PC switched off
  mid-install is caught too). A failure keeps the staged file for another try at exit and counts in
  `install-failures.json`; after 2 failed installs of one version it is only offered for a manual download
  (`Core.Update.InstallFailedManual`) until a newer version appears. The hosts show one "update did not install"
  notification and the same text in Settings -> About. `<app>.setup-backup` is distinct from the delta's
  `.update-old`, so it never triggers `DeltaUpdate.IsBroken`. User data (`%APPDATA%`) is never touched.
- Schedule: `WatchAsync(http, notifyOnly, automatic, UpdateCallbacks, UpdateTrigger)`. `AppSettings.AutoUpdate`
  (default on) -> first check 20 s after start, then every 6 h, **for the whole run**: a newer version than the staged
  one is downloaded and verified, and the old staged file is deleted only after the new one is in place; callbacks
  once per version. `Checked` reports every finished check before `Available`/`Staged`/`Failed`. Settings -> About
  "Check now" calls `UpdateTrigger.Request()` (works with AutoUpdate off; refused while a check runs, within 60 s of
  the last one, or before `RetryAfter`, with the reason in `RefusalMessage`); "What's new" shows the release body as
  plain text (at most 1500 chars, so the notes' first 1500 characters must carry the important part) and links the
  release page. Applied on exit or from the "Update ready - restart" chip. WPF `App.xaml.cs` `StartUpdateChecks`;
  Avalonia `AppHost.cs` (skips mobile). Render mode suppresses update checks in both hosts.
- Real portable packages contain `portable.txt` and use `AppPaths.IsPortable`: the hosts notify about updates but
  do not download/apply them automatically. This is distinct from the `WindowsPortable` updater target, whose
  asset is the Avalonia preview zip; do not conflate the two.
- **Delta updates** (`DeltaUpdate.cs`, installed Windows app only): the release publishes every file's size and
  SHA-256 (`Hearthpulse-Files-V-win-x64.json`, `installer/windows/delta.py manifest`) and the files changed since the
  previous release P (`Hearthpulse-Delta-P-V-win-x64.zip`: `delta.json` with both file lists + `files/...`), only
  when P published a manifest (0.14.0 did not: 0.15.0 ships without a delta). The updater takes the delta only when the
  running version is exactly P and every local file matches P's list; it checks the zip's digest (which also covers
  `delta.json`, the source of both lists), each entry's size, then every unpacked file, stages under
  `<data>/updates/delta/V/`, and at exit (plan hash kept in memory, staged hashes, installed sizes) a helper copies
  the files next to the app folder and swaps by renames with a backup (`<app>.update-old`), every step tried 5 times.
  A failure rolls back (then `<data>/updates/delta/V.failed` makes the next try take the full setup); an old file
  that cannot go back keeps the backup and does not start the app, and the next start reinstalls the newest release
  with the full setup (`DeltaUpdate.IsBroken`), which removes the backup. Success updates `DisplayVersion` and appends
  added files to `uninstall-extra.txt`, which the NSIS uninstaller reads. Any failure before that falls back to the
  full setup. Paths: `DeltaUpdate.IsSafePath` = `delta.py safe()` (no dots/spaces at the end, no device names).
  Trial run of the whole path: `-f publish=false -f simulate_previous=true -f version=0.0.1` (a stand-in 0.0.0
  manifest). Tests: `DeltaUpdateTests.cs` (the helper is run for real on Windows, rollback included).
- Tests: `tests/Hearthpulse.Tests/UpdateTests.cs`, including
  `A_release_without_a_checksum_is_offered_but_never_downloaded` and
  `A_download_is_kept_only_when_it_matches_the_published_checksum` (plus versions, assets, offline and scripts);
  `UpdateSignatureTests`, `Ed25519Tests` (RFC 8032 vectors), `UpdateInstallScriptTests` (the script run for real on
  Windows in temp folders with a fake setup), `UpdateInstallReportTests`.

## Checklist before asking to tag

1. `main` is green in CI (all jobs); locally: restore, `dotnet build Hearthpulse.slnx --no-restore` with
   **0 warnings**, tests pass with actual passed/failed/skipped counts recorded; inspect discovery changes
   against the chosen base ([hearthpulse-testing](../hearthpulse-testing/SKILL.md)).
   Include the Windows `Hearthpulse.Presentation.Tests` suite; the cross-platform suite alone does not exercise
   both compiled presentation variants.
2. No open blocker/major in the reviews of merged branches ([hearthpulse-review](../hearthpulse-review/SKILL.md)).
3. Version bumped in every file above (one commit "Release X.Y.Z"); X.Y.Z greater than the latest GitHub release.
4. `installer/RELEASE_NOTES.md` written: user-facing changes in plain English (sections What's new / Faster / Fixes /
   Known issues / Other), known issues, data sources credited, not affiliated with Blizzard; the first ~1500
   characters carry the most important part (that much shows in the app). Claims about signing must match the state
   at the tag: the signature check only with a key in `update-signing-key.pub`, the "setup is not code-signed" known
   issue only without Authenticode secrets.
5. `installer/windows/notice.txt` and `THIRD-PARTY-NOTICES.txt` credit any new data source or third-party code since
   the last tag (`git log vPREV..main -- src docs/*sources*`; MIT notices for adapted code -
   [hearthpulse-data-sources](../hearthpulse-data-sources/SKILL.md)).
5a. Signing: if `update-signing-key.pub` has a key, the `UPDATE_SIGNING_KEY` secret exists (else the release fails
   before publishing); a trial run without `sign_selftest` logs "SHA256SUMS.txt signature verified". Authenticode
   secrets are optional (a warning without them).
6. Renders of both UIs look right on the release commit ([hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md)).
7. Optional local installer smoke test (Windows needs Git Bash + NSIS; off Windows the WPF publish needs
   `-p:EnableWindowsTargeting=true`, which `build.sh` passes): `installer/windows/build.sh X.Y.Z`.
7a. Updater check in Windows Sandbox (never on the owner's own install): install 0.14.0's setup, let it update to the
   new version (the full setup path), check Apps & features shows the new version, then on the next release update
   from that version (the delta path: `hearthpulse.log` says "Delta update ... ready", the folder has no
   `.update-old`/`.update-new` left), and uninstall: the app folder must be empty, including files a delta added.
8. Asset names unchanged (or `UpdateService.AssetName` updated in the same release and old clients considered).
8a. Trial run on the release commit: `gh workflow run release.yml --ref <branch> -f version=X.Y.Z -f publish=false
   -f sign_selftest=true` is green and `gh release list` is unchanged.
9. Ask the user: tag `vX.Y.Z` and push, or run the workflow by hand. After it finishes: the release has all files +
   `SHA256SUMS.txt` (23, or 24 with a delta) + `SHA256SUMS.txt.sig` when signed (24 / 25), assets show a `digest`,
   and an older build offers the update.
