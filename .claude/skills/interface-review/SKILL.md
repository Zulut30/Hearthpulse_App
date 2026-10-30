---
name: interface-review
description: Use when reviewing a Hearthpulse branch, commit range or uncommitted change for the interface problems it introduced or regressed - XAML/AXAML views, styles, theme tokens, Loc strings, shared view models - across accessibility, layout, writing, typography, colour and polish in both UIs. The UI pass of hearthpulse-reviewer. Triggers - "проверь UI в ветке", "UI-регрессии", "ревью интерфейса ветки", "interface review of the branch".
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/interface-review/SKILL.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> model-invoked so the reviewer role can run it; `main...claude/*` scopes; blast radius through resource keys, controls
> and shared view models; XAML removed signals; the task card as stated intent; B/M/m severity.

# Interface review

This skill reviews a change rather than a screen. It resolves the scope, expands the changed files to the surfaces
they render in, reads both sides of the diff and classifies every finding.

It owns the scope, the classification and the change-scoped report. Domain rules belong to the `better-*` skills.
Severity, consolidation, coverage, the cap and the verdict belong to `better-interface`, which this skill hands the
review to. Correctness, hidden-information logic, licences, threading, tests and migrations belong to
`hearthpulse-review`; name such a concern once, point at that review and drop it.

When `hearthpulse-reviewer` runs it, the findings go into that report's «Дизайн и паритет» section with the same
`B`/`M`/`m` ids.

## Owner's rules

These are the owner's rules and CLAUDE.md; a session brief may narrow them (for example, no render):

- **Everything moves and resizes.** Every overlay element sits in a `MovablePanel` that is a direct child of the
  overlay `Surface`, so the player can drag, resize, fade and reset it (`hearthpulse-overlay-panel`). Main-window
  content follows the window from its minimum size up (WPF `MinWidth="1040"`, Avalonia compact below 900 px).
- **All seven themes.** Tavern, Frost, Fel, Arcane, Ember, Tide, Shadow: colours only from tokens
  (`hearthpulse-design`), and a look at WPF under at least one non-default preset.
- **Render, but never while the game runs.** Renders go through `hearthpulse-visual-check` on a copy of the data
  folder. First check that `Hearthstone.exe` is not running: `Get-Process Hearthstone -ErrorAction SilentlyContinue`
  prints nothing. If it runs, skip the render and mark those checks `Not verified: Hearthstone running`.
- **Both UIs.** The WPF app and the Avalonia preview get the same change with the same Loc keys in ru and en; the
  only allowed gaps are the ones `hearthpulse-design` lists.
- **During a game, only what the player can see.** The exceptions are the match page after the game
  (`docs/match-replay.md`) and BG opponents' MMR (`docs/bg-opponent-mmr.md`).

## The change, not the codebase

The author asks "did I make the interface worse?". Report what the change caused and stay mostly quiet about what it
merely touched. Three pre-existing findings is a courtesy; thirty is a different review nobody asked for.

Read the whole diff before forming an opinion. A skimmed diff produces findings about code the next hunk already
fixed.

## 1. Resolve the change scope first

The invocation names the target: a branch (`claude/ux-round2`), `working`, a range `<a>..<b>` or `<a>...<b>`, or
`pr <n>`. [Scope resolution](scope-resolution.md) holds how each resolves.

With no target, resolve in this order and stop at the first match:

1. `HEAD` is ahead of `git merge-base main HEAD`. Review that range **plus** any uncommitted changes, stating the
   commit count and the uncommitted file count separately.
2. The working tree is dirty. Review the uncommitted changes.
3. Neither. There is no change to review; follow **With no change, say so rather than invent one**.

Record the merge base as `$BASE` and name it in the scope block. Keep only the files that reach the interface per
[UI paths](scope-resolution.md#ui-paths) and name what you left out. If nothing survives, treat it as no change.

## 2. With no change, say so rather than invent one

A clean tree with nothing ahead of `main` has no change to review. Never fall back to `HEAD~1..HEAD` on your own; the
last commit is whatever landed, often a merge or another session's work. State the facts from
[Nothing to review](scope-resolution.md#nothing-to-review) and offer the routes: a branch by name, the last commit by
short SHA and subject, an explicit range, or a whole-surface review handed to `better-interface`. Never report a
review of nothing as `можно сливать`.

## 3. A diff is not a surface

A changed file is evidence, not the review subject. Its **blast radius** is the set of surfaces it renders in; review
those, in both UIs.

Expand one hop by default: the views that use a changed style, control, template or view-model property. Expand a
second hop only for theme tokens, the base styles (`GhostButton`, `Card`, `Segment`, `OverlayCard`) and `Controls/`,
where one line reaches the whole app. Review at most five consumers in total, ordered by
[the rule in Scope resolution](scope-resolution.md#expanding-to-consumers), and state how many you did not expand.

## 4. Read the removed lines

Regressions are invisible in the post-change state. Read the `-` side of every hunk against
[Removed signals](removed-signals.md).

A signal is a lead, not a finding. A removal is a regression only when nothing in the change replaces it, and the
domain skill owns that judgement. Route each unmatched removal to its owner and status it `Regression` only when that
skill confirms it.

## 5. Classify every finding

- `Introduced`: the change created it.
- `Regression`: the change weakened something previously correct.
- `Pre-existing`: present before the change and not made worse by it.

Status by cause, not by location. A finding a changed line causes is `Introduced` or `Regression` wherever it shows,
including an untouched view of a changed token. To check whether a line predates the change, blame the range; a `^`
prefix marks a line unchanged since the base:

```
git blame -L <line>,<line> "$BASE"..<head-ref> -- path/to/File.xaml
```

For uncommitted work, drop the range: a line marked `Not Committed Yet` belongs to the change.

## 6. Hold the change to its stated intent

Read the task card (`D:\HeartLens-main\reference\tasks\TASK-*\task.md`), its `design.md` when there is one, and the
commit messages; then review whether the interface delivers what they claim. This finds the **incomplete** change,
which a surface review misses because it inspects only the states that exist:

- A new view in one UI only, or a setting toggle in one main window only.
- A new string in `en` without `ru`, or the reverse.
- A new style or state that covers some of hover, focus, pressed, selected and disabled but not all.
- A new component with no empty, loading, error, compact or long-Russian-text state.
- A new overlay element outside a `MovablePanel`, or a new panel with no render scene.
- A colour added as a hex in a view, or a meaning colour without its fixed key prefix.

Do not report scope creep; whether a change does too much is the card's question, not this review's.

## 7. Hand the review to `better-interface`

Hand `better-interface` the scope block, the affected surfaces and a status on every finding, and report in the format
below. If `better-interface` is unavailable, report the scope and the file inventory, name it as missing and stop;
do not invent severity or a verdict.

## Never mutate the working tree

A change review is read-only, including the checkout. No `git checkout`, `git switch`, `git stash` or edits in another
session's worktree; read files at the head ref with `git show <head-ref>:path`. `git fetch` writes only to `.git` and
is allowed.

Rendered verification is opt-in: mark visual claims **Not verified** unless the brief asks for a render. To render a
branch, add your own worktree at its head (`git worktree add <short path> <sha>`), render there with
`hearthpulse-visual-check` on a data copy and the game closed, and remove the worktree when done.

## Before you finish

| Mistake | Fix |
| --- | --- |
| The scope counts only uncommitted files while `HEAD` is ahead of `main` | Review the range plus the uncommitted changes, with both counts |
| A report on `HEAD~1..HEAD` nobody chose | State the facts and offer the routes |
| A changed style or token with no consumer under `Surfaces expanded` | Expand to the views that use the key and name the ones skipped |
| One UI's view reviewed, the twin skipped | Expand to both; parity is part of the surface |
| A deleted `AutomationProperties.Name`, `FocusVisualStyle` or animation guard with no finding and no note | Route it through [Removed signals](removed-signals.md) |
| A `Regression` whose removed property reappears elsewhere in the diff | Check the [equivalent replacements](removed-signals.md#equivalent-replacements) first |
| A finding on an untouched view of a changed token statused `Pre-existing` | The change caused it: `Introduced` or `Regression` |
| `git checkout` or `git switch` in the command log | Read with `git show <head-ref>:path` |
| A cited line that does not match `git show <head-ref>:path` | Cite against the head ref in the scope block |
| A render taken while `Hearthstone.exe` ran | Discard it; render with the game closed |

## Review output format

Write it in Russian, identifiers in English. Open with the scope block:

| Field | Value |
| --- | --- |
| Target | `claude/ux-round2`, `working` or the range as entered |
| Base | `main` at `a1b2c3d` (merge base) |
| Head | `claude/ux-round2` at `e4f5g6h` |
| Commits | 7 committed, 2 files uncommitted |
| Files in scope | 12 UI files of 31 changed |
| Left out | `tests/**`, `src/Hearthpulse.Core/Tracking/**`: no interface |
| Surfaces expanded | `MainWindow.xaml` Settings tab, `MainView.axaml` Settings tab; 3 further `GhostButton` users not expanded |

The coverage table from `better-interface` follows. A domain with no evidence in the change is
`Not reviewed: no evidence in the change`, a coverage statement rather than a gap.

Then the findings, with a `Status` column:

| Id | Domain | Status | Location | Before | After | Why |
| --- | --- | --- | --- | --- | --- | --- |
| M1 | Accessibility | Regression | `src/Hearthpulse.UI/Main/MainView.axaml:812` | `AutomationProperties.Name` removed from the icon button | Restore it with the same Loc key as WPF | The control had a name before this change and no longer does |

With no `Introduced` or `Regression` findings, omit the table and write «UI-замечаний в этом изменении нет».

Then `Pre-existing` findings, at most three, highest severity first, stated plainly as not this change's
responsibility. Omit the section when there are none.

| Severity | Domain | Location | Issue |
| --- | --- | --- | --- |
| m | Typography | `src/Hearthpulse.App/Main/MainWindow.xaml:1210` | Rank column uses proportional digits; predates this change |

Verification and the verdict follow in `better-interface`'s format.
