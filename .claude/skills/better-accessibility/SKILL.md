---
name: better-accessibility
description: Use when building or reviewing keyboard and focus behaviour, accessible names (AutomationProperties), labels, errors, announcements, hit areas, motion or scaling in Hearthpulse's WPF and Avalonia views, against WCAG 2.2 adapted to a desktop app. Triggers - "клавиатура", "фокус", "доступность", "screen reader", "AutomationProperties", "Tab order", "hit area".
---

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-accessibility/SKILL.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> ARIA and HTML replaced by UI Automation and WPF/Avalonia controls; the overlay's no-keyboard scope; web-only rules
> (autocomplete, iOS zoom, viewport meta, SPA routes, skip links) removed.

# Accessibility

This skill builds and reviews keyboard and focus behaviour, accessible names, labels, errors, announcements, hit areas,
motion and scaling in both UIs. It reports each failure against the WCAG 2.2 criterion it breaks and writes the fix in
XAML/AXAML with the project's styles.

Reviewing the main window means two walks. Keyboard only, where every flow completes without a mouse. Then Narrator
(Win+Ctrl+Enter) or Accessibility Insights, where every control announces a name, a role and its state. The overlay
sits over the game, which keeps keyboard focus, so it gets no keyboard walk; its hit areas, contrast, colour cues and
motion still apply. When unsure, take the built-in control over a custom rebuild.

Contrast measurement and colour fixes belong to `better-colors`. Text sizing belongs to `better-typography`. Spacing
between targets belongs to `better-layout`. Label, error and help wording belongs to `better-writing`. Animation
recipes belong to `better-ui`. Focus ring styles are tokens of `hearthpulse-design`.

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

## Criteria, not conventions

A finding cites a WCAG 2.2 Level A or AA criterion by number, or a concrete task a keyboard or Narrator user cannot
complete. Everything else is a recommendation and never above `m`. That covers AAA criteria and larger target sizes
than 24×24. The thresholds below are exact; keep an established, usable density.

## Built-in controls first

A `Button` for actions, a `CheckBox Style="{StaticResource Switch}"` / `Classes="switch"` for toggles, a
`RadioButton` (`Segment`, `NavItem`, `Tab`) for one-of choices, a `ToolTip` for hover help. Never a `Border` or `Grid`
with `MouseLeftButtonUp` / `PointerPressed` doing a button's job: it has no focus, no Enter or Space and no automation
role. Where a row must click, use the project's `RowButton` style (WPF) or `Button.bare` + `row` (Avalonia); a row that
does not click stays a `RowBorder` / `row`. See
[names-and-announcements.md](names-and-announcements.md).

## Disabled means unavailable

`IsEnabled="False"` when a control is genuinely unavailable, with the reason in visible text beside it. A disabled
control shows no tooltip unless `ToolTipService.ShowOnDisabled="True"` (WPF) / `ToolTip.ShowOnDisabled="True"`
(Avalonia) is set, and it leaves the tab order, so a reason that lives only in its tooltip never reaches the
keyboard. Disabled controls are exempt from contrast minimums.

## Visible focus rings

Every focusable control in the main window shows where focus is. WPF draws it through `FocusVisualStyle` with the
theme's `FocusRing`, `FocusRingChip`, `FocusRingNav`, `FocusRingRow`, `FocusRingRound` and `FocusRingSwitch` styles; Avalonia's Fluent
theme draws its focus adorner. `FocusVisualStyle="{x:Null}"` or `FocusAdorner="{x:Null}"` without a replacement is
a finding. A new ring reaches 3:1 against everything it crosses (1.4.11), measured with `better-colors`. Recipes are
in [keyboard-and-focus.md](keyboard-and-focus.md).

## Full keyboard support

Every mouse interaction in the main window has a keyboard path: Tab moves between controls, arrow keys move inside a
group of `RadioButton`s or a list, Enter and Space activate, Escape closes the popup or dialog that opened last. Keep
tab order equal to visual order: fix the element order rather than setting `TabIndex`. Key maps and the overlay
exception are in [keyboard-and-focus.md](keyboard-and-focus.md).

## Trap and restore focus

A dialog is a `Window` shown modal with its owner (`ShowDialog()` in WPF, `ShowDialog(owner)` in Avalonia), with
`IsCancel="True"` on its cancel button so Escape closes it. Move focus inside on open and back to the trigger on
close. A `Popup` or flyout that takes focus closes on Escape and gives focus back. The recipe is in
[keyboard-and-focus.md](keyboard-and-focus.md).

## Minimum hit area

WCAG 2.5.8's AA floor is 24×24 DIP or one of its exceptions; the project's `IconButton` is 32×32. In WPF an element
with a null `Background` is not hit-testable, so a clickable area gets `Background="Transparent"` across its whole
visual extent. Decorative layers over controls take `IsHitTestVisible="False"`. In the overlay, only elements marked
`o:OverlayHit.Interactive="True"` take clicks, and none of them may cover Discover cards, the hand or End Turn.
Every new drag needs a click path to the same result, as the `MovablePanel` tag buttons give for size, opacity and
reset (2.5.7). Details are in [hit-areas.md](hit-areas.md).

## Label every input

A `TextBox`, `ComboBox` or `Slider` gets a visible label tied to it: by default a `TextBlock` with the `Label` style
(Avalonia `label` class) that the control names through `AutomationProperties.LabeledBy`. A `Label` control with
`Target` only once `hearthpulse-design` has a style for it. A placeholder (`Watermark`) is never a label.
A `CheckBox` or `RadioButton` carries its label as `Content`, so the label and the control share one hit target.
See [names-and-announcements.md](names-and-announcements.md).

## Errors that announce

Validate on submit, never by disabling the action until the input is valid. Show the error as text beside the field,
not only a red border, and move focus to the first failing field. Wording is `better-writing`'s.

## Accessible names everywhere

An icon-only button needs `AutomationProperties.Name="{i:Loc Key}"`, the same key in both UIs. A text button needs
nothing: its `Content` is the name. The visible label's words appear in the name (2.5.3). An `Image` that carries
meaning gets a name; the `CardPreview` hover is a visual extra and the card's name must also be text nearby.

## Pair colour with a second cue

Results, tiers, rarity and status carry a sign, letter, icon or word next to the colour (1.4.1): "+12", "W"/"L",
the tier letter. This skill decides which contrast requirement applies:

| Criterion | Applies to | Minimum |
| --- | --- | --- |
| 1.4.3 AA | Text | 4.5:1 |
| 1.4.3 AA | Large text, at least 24 DIP, or 18.67 DIP bold | 3:1 |
| 1.4.11 AA | Control boundaries, focus rings, states and meaningful graphics | 3:1 |

Measure the rendered pair with `better-colors` in every preset. When it fails, report the pair and the criterion and
leave the colours alone unless asked.

## Honour the Windows animation setting

WPF animates only when `SystemParameters.ClientAreaAnimation` is on, and every new animation takes the same guard
(`hearthpulse-design`); the theme's own storyboards are made instant by `ThemeEngine`. Under the setting off, a slide
or scale becomes an instant change or an opacity fade. Avalonia's Theme.axaml has `Transitions` of 0.25 s or less (hover, press): state feedback
with no guard yet. Any other Avalonia motion is a `hearthpulse-design` decision. See
[motion-and-scale.md](motion-and-scale.md).

## Nothing the player needs runs on a timer

A toast that carries an action or an error stays until dismissed. Live game data (turn timer, counters, odds) is
essential real-time content and exempt from 2.2.2; a decorative loop is not, and the themes allow none
(`ThemeFileTests`). Rules are in [motion-and-scale.md](motion-and-scale.md).

## Announce changes that matter

A main-window status that appears on its own ("Saved", "Sync failed", a result count) gets
`AutomationProperties.LiveSetting="Polite"` on a `TextBlock` that exists before its text changes. Errors that block
the player use `Assertive`. Never move focus to a toast. See [names-and-announcements.md](names-and-announcements.md).

## Structure is navigation

Page titles use the `Display` style and section headings the `Ornament` control, in a coherent order.
`AutomationProperties.HeadingLevel` marks a heading for screen readers in both frameworks.
After `ShowPage`, focus lands on the new page, not on a control left behind on the old one.

## Survive scale

Nothing clips or overlaps at the minimum window size (WPF `MinWidth="1040" MinHeight="660"`), in Avalonia compact
(rendered 420 wide), with Russian text, or in the overlay at `OverlayScale` 0.7 and 1.4 (the Settings slider; the code clamps to 0.6-1.6)
and panel scale 0.6 to 1.6.
Fixed `Height` on a text container is the usual cause; `better-layout` owns the fix.

## Before you finish

| Pattern | Fix |
| --- | --- |
| `FocusVisualStyle="{x:Null}"` or `FocusAdorner="{x:Null}"` with no replacement | The matching `FocusRing*` style, or Fluent's adorner |
| `MouseLeftButtonUp`, `MouseDown`, `PointerPressed` or `Tapped` on a `Border`, `Grid` or `TextBlock` in the main window | A `Button` with `RowButton` / `Button.bare` styling |
| `Focusable="False"` or `IsTabStop="False"` on an interactive control | Remove it, or say why the control is reachable another way |
| `TabIndex` on more than one control of a view | Fix the element order |
| An icon-only `Button` (`IconButton`, `icon`) without `AutomationProperties.Name` | `AutomationProperties.Name="{i:Loc Key}"` in both views |
| `AutomationProperties.Name` that omits the visible label's words | Start the name with the visible text |
| `IsEnabled="False"` with the reason only in `ToolTip` | Visible text beside it, or `ShowOnDisabled` |
| `Watermark` or `PlaceholderText` as the only label | A `Label`-style `TextBlock` and `LabeledBy` |
| A clickable `Border` or `Grid` with no `Background` (WPF) | `Background="Transparent"` |
| `OverlayHit.Interactive` on a panel root that is not all clickable | Mark only the clickable element |
| A new `Storyboard` outside `Themes/`, or an animation started from code, with no animation-setting guard | Guard it, or drop the motion |
| A status `TextBlock` created with its text at the moment it should announce | Keep the element, change its text, set `LiveSetting` |
| `Positive`/`Negative` brush as the only signal of a result | Add the sign, letter or icon |
| A dragged control with no click alternative | Buttons or a menu that reach the same result |

## Reporting

**Severity.** Use `better-interface`'s scale. This domain's escalation triggers are `M` on sight: a missing
accessible name, a missing focus indicator, a mouse path with no keyboard path in the main window, an animation that
ignores the Windows setting, clipping at the minimum size, compact width, Russian text or overlay scale, and meaning
carried by colour alone. Other failures that stop a task are `M`; ones that make it meaningfully harder are `m`;
isolated polish is `n`.

**Verification.** From the code: names on every interactive element in both views, focus styles, keyboard paths for
custom handlers, animation guards, labels tied to inputs. With the app: tab every flow in the main window in order,
read names and roles with Narrator or Accessibility Insights, confirm a visible ring at every stop. Running the app
needs `HEARTHPULSE_DATA` on a copy (`hearthpulse-visual-check`) and the game closed. Report every check you could not
run as `Not verified`.

**Format.** The table in `better-interface`'s `review-format.md` without the `Domain` column, grouped under the
principle each finding violates, one row per root cause listing every location in both UIs. `Why` names the WCAG
criterion and the player impact. With nothing to report, say «Замечаний по доступности нет» and list the checks.
