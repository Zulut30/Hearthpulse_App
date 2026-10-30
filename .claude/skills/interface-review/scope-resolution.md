# Scope resolution

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/interface-review/scope-resolution.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> local `main` as the base and `claude/*` branches as targets; web lockfile and build exclusions replaced by the
> project's UI paths; consumers found through resource keys, controls and view-model bindings.

How a review target becomes a file list, and the traps that fail quietly.

## Base

The base is the local `main`. Sessions work on `claude/*` branches in their own worktrees and the coordinator merges,
so `origin/main` can lag behind; use it only when the brief says so. Name the base and its SHA in the scope block.

## Targets

| Target | Range |
| --- | --- |
| A branch name | `git diff main...<branch>` (three dots: from the merge base) |
| `working` | `git diff HEAD` plus `git ls-files --others --exclude-standard` |
| `<a>..<b>` / `<a>...<b>` | Exactly as written; two dots compare the endpoints, three the merge base |
| `pr <n>` | `gh pr view <n> --repo Zulut30/Hearthpulse_App --json baseRefName,headRefName,title,body`, then `git fetch origin pull/<n>/head:refs/remotes/pr/<n>` and diff against the PR's own base |

Diff a branch with three dots; two dots report every commit that landed on `main` meanwhile as part of the change. A
target with uncommitted work must also list untracked files, or a new view is silently dropped.

Read files at the head ref with `git show <head-ref>:path`, never from the working tree of another session. Cite line
numbers against that ref.

## Awkward states

- **Detached HEAD:** use the merge base against `main` and name the SHA.
- **Mid-rebase or mid-merge:** `git diff` succeeds and returns something that is not the change. Detect it with
  `git rev-parse --git-path rebase-merge`, `rebase-apply`, `MERGE_HEAD` and `CHERRY_PICK_HEAD` (inside a worktree
  `.git` is a file, so test the paths git prints). Stop and say the tree is mid-operation.
- **Unrelated histories or no merge base:** say the base is unresolvable and stop.

## Nothing to review

The tree is clean and `HEAD` is not ahead of the merge base. Gather the facts before offering routes: the current
branch, the count ahead of `main`, the last commit's SHA and subject, and the `claude/*` branches with commits ahead
of `main` (`git branch --no-merged main --list "claude/*"`).

## UI paths

Keep these in scope; they reach what the player sees:

| Kind | Paths |
| --- | --- |
| Views | `src/Hearthpulse.App/**/*.xaml(.cs)`, `src/Hearthpulse.UI/**/*.axaml(.cs)`, `src/Hearthpulse.Desktop/**/*.axaml(.cs)` |
| Theme and controls | `**/Themes/**`, `**/Controls/**`, `*/Infrastructure/ThemeEngine.cs`, `*/Infrastructure/Converters.cs`, `src/Hearthpulse.Core/ThemePalette.cs`, `tools/themes/**` |
| Overlay host | `*/Overlay/MovablePanel.cs`, `src/Hearthpulse.App/App.CardPool.cs` |
| Shared view models | `src/Hearthpulse.Presentation/**` |
| Text | `src/Hearthpulse.Core/Localization/Strings/**` |
| Render scenes | `src/Hearthpulse.App/DevRender*.cs`, `src/Hearthpulse.Desktop/OffscreenRenderer*.cs`, `CardPoolRender.cs` |
| Assets | `**/Assets/**` (fonts, images): review the code that references them, not the bytes |
| Settings that change the UI | `src/Hearthpulse.Core/AppSettings.cs` (`Show*`, panel and look settings) |

Leave out, and name in the scope block: `tests/**`, `docs/**`, `.claude/**`, `bin/`, `obj/`, generated `*.g.cs`, Core
folders with no UI surface (`Power/`, `Logs/`, `Tracking/`, `Api/`). A Core change that alters a displayed value is
`hearthpulse-review`'s, unless its text or format changed.

Count the files with and without the filter and confirm the difference equals what you named.

## Expanding to consumers

Consumers reference a name, not a file, so search the name at the reviewed ref:

```
git grep -n -e "StaticResource GhostButton" -e "Classes=\"ghost" <head-ref> -- "*.xaml" "*.axaml"
git grep -n -e "ShowCombatOdds" <head-ref> -- "*.xaml" "*.axaml" "src/Hearthpulse.Presentation"
```

Pass `-e` before a pattern that starts with a dash. Results come back as `<rev>:path`; read them with `git show`.

Order consumers by a rule you can evaluate:

1. **Main pages and overlay panels first**: `MainWindow.xaml` / `MainView.axaml` pages and tabs, `OverlayWindow.xaml` /
   `OverlayView.axaml` panels, the extracted panel controls.
2. **Then by use count**: a style used by twenty views carries more of the change than one used by two.
3. **Break ties by the twin**: the other UI's view of the same surface comes before an unrelated view.

Say plainly where the ordering became arbitrary.
