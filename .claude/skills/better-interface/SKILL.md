---
name: better-interface
description: Use when reviewing a Hearthpulse screen, overlay panel, settings card or flow across accessibility, layout, writing, typography, colour and polish in both UIs (WPF + Avalonia) and all seven themes, and returning one ranked report with B/M/m findings. Triggers - "проверь интерфейс", "ревью экрана", "UI review", "что не так с панелью", "polish this page". For a branch or diff use interface-review.
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-interface/SKILL.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> rewritten for WPF/Avalonia; project severity B/M/m/n and verdict; triggers for the owner's rules; hands branches
> to `interface-review` directly.

# Interface

This skill runs a cross-discipline review of one Hearthpulse surface. It routes the surface to each `better-*` skill,
collects their evidence and consolidates one ranked verdict.

Orchestration is all it owns. Accessibility rules belong to `better-accessibility`, structure to `better-layout`,
copy to `better-writing`, type to `better-typography`, colour to `better-colors`, visual polish and motion to
`better-ui`. Tokens, styles and themes are `hearthpulse-design`'s; rendering is `hearthpulse-visual-check`'s.

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

## Evidence, not taste

Press hard on the escalation triggers and leave deliberate project choices alone. A trigger is a failure whatever
the style says. A density, radius or tone you merely disagree with is not a finding: the Tavern look (wood, parchment,
gold, `Ornament` headings) is the product's voice.

The bar for reporting is evidence. The bar for `можно сливать` is that you inspected what you claim to have
inspected.

## 1. Resolve the scope first

Name the surface: a main-window page or tab (`hearthpulse-main-page`), an overlay panel by its `MovablePanel` Id
(`hearthpulse-overlay-panel`), a settings card, or one flow such as first launch to the first tracked game. State it
in the output, with the files of both UIs that render it.

Cover all of it in every domain, including the states that exist: no games yet, no data, loading, API offline, the
game not running, long lists, Avalonia compact width, Russian and English, a non-default theme.

When the scope is too large to inspect credibly, narrow it to one complete flow and state what it excluded. Never
imply uninspected surfaces were reviewed.

## 2. Send a change to `interface-review`

A request naming a branch, commit range or uncommitted changes is a change review: use `interface-review`, which
resolves the scope and statuses and hands the findings back here. Never resolve a diff yourself.

When `interface-review` hands a review back, apply everything below to it. The verdict counts `Introduced` and
`Regression` findings only, so a change whose only findings are `Pre-existing` is `можно сливать`.

## 3. Recon before judgment

The stack is fixed: WPF `src/Hearthpulse.App`, Avalonia `src/Hearthpulse.UI` (+ host `src/Hearthpulse.Desktop`),
shared view models in `src/Hearthpulse.Presentation`, tokens and styles in each `Themes/Theme.xaml(.axaml)`. Write
every fix in that idiom: a `StaticResource` key or style class, a Loc key, a shared view-model property.

Then read what the project wrote about the surface: `hearthpulse-design`, `docs/localization.md`,
`src/Hearthpulse.Presentation/README.md`, the feature's `docs/*.md`, and the task's `design.md` when one exists. Name
what you found.

A documented convention settles matters of taste but never excuses a trigger. When a shared token, style or control
is the cause, report it once against that source, with the views as its locations.

## 4. Use domain skills as the sources of truth

Load every owning skill and complete each domain review before consolidation, in this order, so foundational failures
are not hidden by polish:

1. `better-accessibility`
2. `better-layout`
3. `better-writing`
4. `better-typography`
5. `better-colors`
6. `better-ui`

From each, take its principles, references and verification checks. Its **Reporting** section maps its findings onto
the scale below.

If an owning skill is unavailable, mark that domain `Not reviewed`, name it and continue. Do not recreate its rules
from memory. When two skills appear to cover one issue, assign it to the owner of the underlying rule and note the
secondary effect in the **Why** cell.

## 5. Verify what can be verified

Run the cheap checks the project has: `dotnet test tests/Hearthpulse.Tests --filter
"FullyQualifiedName~ThemeContrastTests|FullyQualifiedName~ReadableTextTests|FullyQualifiedName~ThemeFileTests|FullyQualifiedName~LocBindingTests"`
and, for shared presentation code, `tests/Hearthpulse.Presentation.Tests` on Windows (`hearthpulse-testing`). Render
the surface in both UIs, plus WPF under one non-default `--theme`, when visual judgment matters and the game is not
running. Report the exact command and its result. A check you cannot run is **Not verified**, never a finding.

## Require evidence

Every finding cites `path/to/File.xaml:line` (and its twin in the other UI) and shows the current code. A visual claim
needs a rendered PNG; a code claim needs the code. A PNG alone cannot support a code finding.

## Rank by player impact

One scale, the project's (`hearthpulse-review`):

- `B` Blocker: must not merge.
- `M` Major: the feature is wrong for the player, missing in one UI or breaks an owner's rule; stops the merge.
- `m` Minor: meaningfully harms comprehension, efficiency, adaptability or consistency.
- `n` Nit: isolated polish; listed, not counted.

Within a severity, rank by how many places the finding reaches and how much one fix buys. A token, style or shared
control fix outranks the same symptom in one view.

**Escalation triggers.** Once the owning skill confirms one, it takes this severity on sight, never averaged down
because the surface is minor.

`B`:

- Hidden information shown during a game, outside the two exceptions in **Owner's rules**.
- A destructive action on saved player data (history, decks, settings, the account) with no confirmation and no undo.

`M`:

- An interactive control with no accessible name: an icon-only button without `AutomationProperties.Name`.
- A keyboard-reachable control with no visible focus indicator.
- A main-window control or path reachable by mouse but not by keyboard.
- An animation that ignores Windows' animation setting (`SystemParameters.ClientAreaAnimation`).
- Content or a control clipped, overlapped or unreachable at the minimum window size, at Avalonia compact width, with
  Russian text, or at `OverlayScale` 0.7 and 1.4 (the Settings slider; the code clamps to 0.6-1.6) and panel scale 0.6 and 1.6.
- Text whose rendered pair fails its required ratio in any preset, or readable text painted `Faint`.
- State or meaning carried by colour alone.
- A semantic colour used against its meaning, or a meaning colour (`Positive`, `Negative`, `Tier*`, `Rarity*`) that
  the theme engine recolours.
- A hex colour in a view, so a theme leaves a Tavern-gold spot.
- Truncated content with no way to reach the full value.
- Content past a scroll edge or in a collapsed section with no visible cue.
- An error that names no way to recover.
- A state change carried by motion alone.
- A user-facing string without a Loc key in both ru and en.
- A feature, panel or setting in one UI only, outside the gaps `hearthpulse-design` lists.
- An overlay element outside a `MovablePanel`, or an overlay interactive area over Discover cards, the hand or End
  Turn.

Triggers rank above every other finding. When more fire than the cap allows, list them first and say how many the cap
excluded. A cap may shorten a report; it may never be why a blocker went unreported.

These set severity, not new rules. The owning skill decides whether the symptom is present.

## Prefer the cheaper fix

Severity says how bad a finding is; this says which fix to propose. Take the earliest that works:

1. **Delete.** A separator that space would carry, an animation on a high-frequency interaction, a wrapper `Border`
   that draws nothing, a local value that overrides the style.
2. **Use the platform.** A real `Button`, `CheckBox`, `RadioButton`, `ToolTip` or `ScrollViewer` instead of a
   `Border` with mouse handlers.
3. **Reuse what the project has.** A token, style or class from `hearthpulse-design`, a control from `Controls/`, an
   existing Loc key with the same meaning.
4. **Correct the value.** The wrong margin, duration, token or trimming, with the exact value the owning skill gives.
5. **Add.** A new token in both theme files, a new style, a new Loc key pair, a new `AutomationProperties` value.

When the code under review adds what **Delete** would fix, that is a finding, and its **After** is the deletion.

## Consolidate systemic findings

One root cause is one finding, with every confirmed location in the same row, both UIs included. Report at most 15
findings. Never pad toward the cap; no findings is a valid result.

## Review without mutating by default

A review is read-only. Do not edit source unless asked to implement the findings. When asked, keep the consolidated
report as the change scope and re-run the relevant verification afterwards.

## Before you finish

| Mistake | Fix |
| --- | --- |
| A separate findings section per domain | One table ranked by severity, then reach |
| A finding on one UI with the twin view unread | Read both views; one row lists both locations |
| A rendering claim whose only evidence is XAML | Render it, or mark it **Not verified** |
| A render taken while `Hearthstone.exe` ran | Discard it; render with the game closed |
| WPF checked only in Tavern | Render one non-default preset, or mark themes **Not verified** |
| A missing accessible name or focus ring rated `m` because the surface is small | Triggers take their severity on sight |
| A domain marked `Clear` although its skill never loaded | Mark it `Not reviewed` and name the skill |
| The same symptom in several rows, one per view | One row against the shared token, style or control |
| An **After** that adds a wrapper, token or Loc key | Check whether a deletion or a built-in control fixes it first |
| A fix written as CSS, HTML or ARIA | Rewrite it in XAML/AXAML and the project's tokens |

## Review output format

The format lives in [review-format.md](review-format.md): scope and coverage, the findings table, verification and the
verdict. A review is not finished until its findings are reported there.
