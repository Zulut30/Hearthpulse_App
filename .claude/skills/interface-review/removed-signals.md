# Removed signals

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/interface-review/removed-signals.md` at
> `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> ARIA, CSS and HTML signals replaced by XAML, theme, Loc and overlay signals.

What to look for on the `-` side of a hunk and which skill owns the judgement.

| Removed from the `-` side | Owner | What to check |
| --- | --- | --- |
| `AutomationProperties.Name`, `LabeledBy`, `HelpText`, `LiveSetting` | `better-accessibility` | The control lost its name, label or announcement |
| `Label Target=`, a `Button` replaced by a `Border` or `Grid` with a mouse handler | `better-accessibility` | Keyboard and Narrator behaviour traded for looks |
| `FocusVisualStyle`, a `FocusRing*` style, `FocusAdorner`, `IsTabStop`, `Focusable`, `KeyBinding`, `IsCancel`, `IsDefault` | `better-accessibility` | Keyboard users lost the focus ring, the tab stop or a key |
| A `SystemParameters.ClientAreaAnimation` check | `better-accessibility` | Motion now ignores the Windows setting |
| `TextTrimming`, `TextWrapping`, `MaxLines`, `ToolTip` beside trimmed text, `NumeralAlignment` | `better-typography` | Text now clips, overflows or jitters, or lost its way back to the full value |
| A ramp `Style` / class replaced by local `FontSize` or `FontWeight` | `better-typography` | The text left the ramp |
| `MinHeight`/`MaxWidth` replaced by fixed `Height`/`Width`, a `WrapPanel` or `*` column replaced by `StackPanel`/`Auto` | `better-layout` | Long Russian text or a small size now clips |
| A `.wide` class or `m\|MainView.compact` rule | `better-layout` | Avalonia compact lost its handling |
| `MovablePanel`, its `Id`, or a panel moved out of `Surface` | `better-layout` | The player can no longer move or resize it; a changed `Id` loses saved placements |
| `OverlayHit.Interactive` moved to a larger element | `better-accessibility` | The overlay may now take clicks the game needed |
| A `StaticResource` token replaced by a hex, or a key renamed away from `Positive`/`Negative`/`Info`/`Rarity`/`Tier`/`ManaGem`/`PoolStat` | `better-colors` | The colour no longer follows the theme, or a meaning colour now does |
| A text token swapped for a lighter one (`Muted` → `Faint`) | `better-colors` | The rendered pair may now fail; measure it |
| A Loc key, or one language's entry of it | `better-writing` | A label, error or empty state lost its text in one language |
| A user-facing string shortened or deleted | `better-writing` | It lost the information it carried |

## Equivalent replacements

These clear the signal. Drop a removal one of them explains before routing anything:

- `AutomationProperties.Name` giving way to visible `Content` text on the same control.
- A `Border` click handler removed because the element became a `Button`.
- A local `FocusVisualStyle` dropped because the new style sets the same `FocusRing*`.
- A local `FontSize` dropped because the element took a ramp style.
- A hex replaced by a token with the same value.
- A string moved from XAML into a Loc key, or a key renamed with both languages carried over.
- A `MovablePanel` moved within `Surface` with its `Id` unchanged.

## Searching the removed side

Restrict the search to deleted lines so additions do not mask a removal:

```
git diff -U0 "$BASE" <head-ref> -- "*.xaml" "*.axaml" "*.cs" "src/Hearthpulse.Core/Localization/Strings" \
  | grep -E '^-' | grep -vE '^--- (a/|/dev/null)' \
  | grep -E 'AutomationProperties|Label Target|FocusVisualStyle|FocusRing|FocusAdorner|IsTabStop|Focusable|KeyBinding|IsCancel|IsDefault|ClientAreaAnimation|TextTrimming|TextWrapping|MaxLines|ToolTip|NumeralAlignment|MinHeight|MaxWidth|WrapPanel|\.wide|compact|MovablePanel|OverlayHit|StaticResource|"[A-Z][A-Za-z]+\.[A-Za-z.]+":'
```

Leave `<head-ref>` off for uncommitted work, so the diff runs against the working tree. Read the surrounding hunk
before deciding; `-U0` hides the element a removed property came from.
