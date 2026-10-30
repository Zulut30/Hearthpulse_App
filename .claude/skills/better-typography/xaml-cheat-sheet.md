# XAML cheat sheet

> Replaces [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-typography/css-cheat-sheet.md`
> at `592aedc` (MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md)): same role, a lookup
> per property, written for WPF and Avalonia instead of CSS and Tailwind.

One line per text property this skill uses, WPF beside Avalonia. Prefer the project's style or class over any local
value (`hearthpulse-design`).

## Font

| What | WPF | Avalonia |
| --- | --- | --- |
| Ramp style | `Style="{StaticResource Caption}"` | `Classes="caption"` |
| Font family | `FontFamily="{StaticResource UiFont}"` | `FontFamily="{StaticResource UiFont}"` |
| Weight | `FontWeight="SemiBold"` | `FontWeight="SemiBold"` |
| Italic | `FontStyle="Italic"` | `FontStyle="Italic"` |
| Tabular digits | `Typography.NumeralAlignment="Tabular"` (set by `AppWindow`) | `FontFeatures` with `tnum` (`FontFeature.Parse("tnum")` in code) |
| Slashed zero | `Typography.SlashedZero="True"` | `FontFeatures` with `zero` |
| Kerning | on by default; never turn it off | on by default |
| Formatting mode | `TextOptions.TextFormattingMode="Display"` on the window | n/a |
| Rendering mode | `TextOptions.TextRenderingMode` on a window or style | `RenderOptions.TextRenderingMode` |

## Spacing

| What | WPF | Avalonia |
| --- | --- | --- |
| Line height | `LineHeight="18" LineStackingStrategy="BlockLineHeight"` | `LineHeight="18"` |
| Letter spacing | none on `TextBlock`; leave it | `LetterSpacing="0.5"` |
| Measure cap | `MaxWidth` on the `TextBlock` (about 560-680 DIP at 13) | `MaxWidth` |
| Alignment | `TextAlignment="Left"` / `"Right"` for number columns | same |

## Wrapping and overflow

| What | WPF | Avalonia |
| --- | --- | --- |
| Wrap | `TextWrapping="Wrap"` | `TextWrapping="Wrap"` |
| Wrap, let long words overflow | `TextWrapping="WrapWithOverflow"` | `TextWrapping="WrapWithOverflow"` |
| No wrap | `TextWrapping="NoWrap"` | `TextWrapping="NoWrap"` |
| One-line ellipsis | `TextTrimming="CharacterEllipsis"` | `TextTrimming="CharacterEllipsis"` |
| Ellipsis at a word | `TextTrimming="WordEllipsis"` | `TextTrimming="WordEllipsis"` |
| Several lines, then ellipsis | `MaxHeight` = lines × `LineHeight`, `BlockLineHeight`, `TextTrimming` | `MaxLines="3"` + `TextTrimming` |
| Full text on hover | `ToolTip="{Binding Name}"` | `ToolTip.Tip="{Binding Name}"` |

## Decoration and selection

| What | WPF | Avalonia |
| --- | --- | --- |
| Underline | `TextDecorations="Underline"` | `TextDecorations="Underline"` |
| Selectable value | read-only `TextBox` with the `TextBox` style | `SelectableTextBlock` |
| Gradient text (`GildedText`, `StatText`) | `Foreground="{StaticResource GildedText}"` via the `Display` / `Stat` style | the `display` / `stat` class |
