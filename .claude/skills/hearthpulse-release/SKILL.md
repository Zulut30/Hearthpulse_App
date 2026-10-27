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
The version must match `^\d+\.\d+\.\d+$` (a leading `v` is stripped).

| Job | Runner | Does |
|---|---|---|
| `tests` | ubuntu-latest | `dotnet test tests/Hearthpulse.Tests`, skipped when `ci.yml` already passed on the same commit; runs beside the builds, `release` waits for it |
| `build` (matrix, all in parallel) | ubuntu-latest / macos-latest | `installer/windows/build.sh V app` (NSIS `Hearthpulse-Setup-V-win-x64.exe` and `Hearthpulse-Portable-V-win-x64.zip` from WPF); `installer/windows/build.sh V preview` (`Hearthpulse-Preview-V-win-x64.zip` from Avalonia); `installer/linux/build.sh V x64` and `V arm64` (deb/rpm, `Hearthpulse-V-linux-{x64,arm64}.tar.gz`, `Hearthpulse-Portable-V-linux-{x64,arm64}.tar.gz`, AppImage); `installer/macos/build.sh V arm64 x64` on macOS -> `Hearthpulse-V-macOS-{arm64,x64}.{pkg,dmg,zip}` plus `Hearthpulse-Portable-V-macOS-{arm64,x64}.zip` (ad-hoc signed, not notarized) |
| `release` | ubuntu | merges artifacts into `dist/`, checks every expected installer is there, `sha256sum * > SHA256SUMS.txt`, publishes with `softprops/action-gh-release@v2`: tag `vV`, name "Hearthpulse V", `body_path: installer/RELEASE_NOTES.md`, `fail_on_unmatched_files: true` |

It publishes into the repository the workflow runs in. Verify `git remote -v` and the intended GitHub repository
before publishing: the updater is configured for `Zulut30/Hearthpulse_App`. CI (`ci.yml`) is separate: build + tests
on Windows/Linux and the pool API/Python skill checks on Linux for every push to `main` and every PR.

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
`Hearthpulse.Core.csproj` has no `<Version>`: a plain local build can report Core as 1.0.0 independently of the app
version. `UpdateService.CurrentVersion` reads the **Core** assembly version, so always
test the updater on a build made with `-p:Version=...`, never on a plain `dotnet run`.

## Updater (`src/Hearthpulse.Core/Updates/UpdateService.cs`)

- `CheckAsync(http)` -> `https://api.github.com/repos/Zulut30/Hearthpulse_App/releases/latest` (`RepoOwner`/`RepoName`);
  skips drafts and prereleases, strips `v` from `tag_name`, picks the asset whose name equals
  `AssetName(version, target, arch, linuxFlavor)` - the same names the installers produce. **Renaming an installer
  file breaks updates** for everyone on older versions.
- Target: `DetectTarget()` - `WindowsInstalled` (registry `HKCU\...\Uninstall\Hearthpulse` `InstallLocation` = app
  folder), `WindowsPortable`, `MacPackage`, `LinuxPackage` (deb / rpm / tar.gz by `LinuxFlavor()`).
- **Digest check**: GitHub's per-asset `digest` (`sha256:<64 hex>`) becomes `UpdateInfo.Sha256`;
  `MatchesDigestAsync` verifies the download (`.part` -> checked -> moved). A missing or invalid digest prevents
  downloading; a mismatch discards the file. Installation rechecks the staged file. `SHA256SUMS.txt` is for people;
  the app does not use it as a fallback. Keep the missing-digest, mismatch and staged-file checks fail closed.
- Staging: `<data>/updates/<asset>`; old downloads deleted; Windows `Zone.Identifier` mark cleared.
- Apply (Windows): `ApplyStaged` writes `%TEMP%\hearthpulse-update-<pid>.cmd` that waits for the app to exit, runs
  `setup.exe /S` (installed) or copies the zip (portable), optionally relaunches. macOS/Linux open the package
  (`open` / `xdg-open`).
- Schedule: `AppSettings.AutoUpdate` (default on) -> first check 20 s after start, then every 6 h until something is
  staged; applied on exit or from the "Update ready - restart" chip. WPF `App.xaml.cs` `StartUpdateChecks`; Avalonia
  `AppHost.cs` (skips mobile). Render mode suppresses update checks in both hosts.
- Real portable packages contain `portable.txt` and use `AppPaths.IsPortable`: the hosts notify about updates but
  do not download/apply them automatically. This is distinct from the `WindowsPortable` updater target, whose
  asset is the Avalonia preview zip; do not conflate the two.
- Tests: `tests/Hearthpulse.Tests/UpdateTests.cs`, including
  `A_release_without_a_checksum_is_offered_but_never_downloaded` and
  `A_download_is_kept_only_when_it_matches_the_published_checksum` (plus versions, assets, offline and scripts).

## Checklist before asking to tag

1. `main` is green in CI (all jobs); locally: restore, `dotnet build Hearthpulse.slnx --no-restore` with
   **0 warnings**, tests pass with actual passed/failed/skipped counts recorded; inspect discovery changes
   against the chosen base ([hearthpulse-testing](../hearthpulse-testing/SKILL.md)).
   Include the Windows `Hearthpulse.Presentation.Tests` suite; the cross-platform suite alone does not exercise
   both compiled presentation variants.
2. No open blocker/major in the reviews of merged branches ([hearthpulse-review](../hearthpulse-review/SKILL.md)).
3. Version bumped in every file above (one commit "Release X.Y.Z"); X.Y.Z greater than the latest GitHub release.
4. `installer/RELEASE_NOTES.md` written: user-facing changes in plain English, known issues, data sources credited.
5. `installer/windows/notice.txt` credits any new data source or third-party code (MIT notices for adapted code -
   [hearthpulse-data-sources](../hearthpulse-data-sources/SKILL.md)).
6. Renders of both UIs look right on the release commit ([hearthpulse-visual-check](../hearthpulse-visual-check/SKILL.md)).
7. Optional local installer smoke test (Windows needs Git Bash + NSIS; off Windows the WPF publish needs
   `-p:EnableWindowsTargeting=true`, which `build.sh` passes): `installer/windows/build.sh X.Y.Z`.
8. Asset names unchanged (or `UpdateService.AssetName` updated in the same release and old clients considered).
9. Ask the user: tag `vX.Y.Z` and push, or run the workflow by hand. After it finishes: the release has all files +
   `SHA256SUMS.txt`, assets show a `digest`, and an older build offers the update.
