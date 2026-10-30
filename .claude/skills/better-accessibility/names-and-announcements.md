# Names, labels and announcements

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-accessibility/semantics-and-aria.md`,
> `screen-readers.md` and `forms.md` at `592aedc`, MIT, © 2026 Jakub Krehel, see
> [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: three files merged; ARIA, alt text, live
> regions and HTML forms rewritten as UI Automation properties and WPF/Avalonia controls; autocomplete removed.

What Narrator reads: control types, names, labels, images, disabled states, errors and live updates.

## Built-in controls carry their role

UI Automation reads a control's role from its type. A `Button` is a button, a `CheckBox` a check box, a
`RadioButton` a radio button, a `ListBox` a list. A `Border` with a click handler is a pane with no role, no focus and
no Enter or Space.

```xml
<!-- Bad: looks clickable, is invisible to the keyboard and Narrator -->
<Border MouseLeftButtonUp="OpenDeck" Background="{StaticResource Row}">...</Border>

<!-- Good, WPF: a real button styled as a row -->
<Button Style="{StaticResource RowButton}" Command="{Binding OpenDeck}">...</Button>

<!-- Good, Avalonia -->
<Button Classes="bare row" Command="{Binding OpenDeck}">...</Button>
```

A control that navigates to another page is still a `Button` (or a `NavItem` radio button); a link out of the app
(`Hyperlink` / `HyperlinkButton`) opens the browser.

## Accessible names

Name precedence: `AutomationProperties.Name` > `AutomationProperties.LabeledBy` > the control's text content.

- A text `Button`, `CheckBox` or `RadioButton` is named by its `Content`. Add nothing.
- An icon-only button (`IconButton` / `icon`, an MDI glyph as content) gets
  `AutomationProperties.Name="{i:Loc Key}"`, the same key in both UIs. The name says the action, never the glyph.
- WCAG 2.5.3: when a control shows words, its name starts with those words. A button reading "Merge" with the name
  "Combine deck versions" breaks voice control.
- `AutomationProperties.HelpText` carries a longer hint, often the same Loc key as the tooltip.
- A name bound to data (`AutomationProperties.Name="{Binding Name}"` on a theme tile) is fine; check it is never empty.

## Labels for inputs

```xml
<!-- WPF (Avalonia: Classes="label"): a visible label the control points at -->
<TextBlock x:Name="HueLabel" Style="{StaticResource Label}" Text="{i:Loc Settings.Theme.Accent}" />
<Slider AutomationProperties.LabeledBy="{Binding ElementName=HueLabel}" />
```

A `Label` control with `Target` gives the same tie and an access key, but the theme has no style for it yet; use it
only after `hearthpulse-design` adds one.

A `Watermark` / `PlaceholderText` disappears on typing and is never the label.

## Images and glyphs

| Purpose | Treatment |
| --- | --- |
| Decorative (Scallop, Ornament motif, Wave, backdrop) | Nothing to read: Avalonia `AutomationProperties.AccessibilityView="Raw"`; in WPF keep it out of focusable templates |
| Card art or hero portrait next to its name in text | Decorative, the text carries the name |
| Card art standing alone (mulligan plate, discover pick) | The card name as text nearby or as `AutomationProperties.Name` |
| Chart (`RatingChart`, `StatsLineChart`, `ResultBars`) | A name that states the takeaway, and the numbers available as text (labels, hover card or a summary line) |
| Mana gem (`ManaCost`) | The cost is in its `Text`; a row reading "3 Fireball" already says it |

## Disabled states

- `IsEnabled="False"` removes the control from the tab order and from activation, greys it through the style and is
  exempt from contrast minimums.
- A disabled control shows no tooltip unless `ToolTipService.ShowOnDisabled="True"` (WPF) or
  `ToolTip.ShowOnDisabled="True"` (Avalonia). Put the reason in visible text beside it.
- Never disable an action to gate validation; let it run and report what is missing.

## Errors

- Show the error as text beside the field it concerns, with an icon or word, not only a `Negative` border.
- WPF `Validation.Errors` and Avalonia `DataValidationErrors` draw it next to a bound field; a custom message is a
  `TextBlock` placed there.
- On a failed action, move focus to the first field that needs a fix. That move is the announcement.
- Trim input before validating: pasted folder paths and names carry spaces.

## Announcing changes

Work down this list and stop at the first match:

1. **Focus moves there anyway**, as with an opened dialog or the first invalid field. Nothing extra needed.
2. **Tied to one control**, such as a field error: `AutomationProperties.HelpText` or `LabeledBy` on that control.
3. **Not tied to a control and not urgent**, such as "Saved", "Synced 12 games", a loading line: a `TextBlock` with
   `AutomationProperties.LiveSetting="Polite"`.
4. **Urgent**, such as "Sign-in expired": `LiveSetting="Assertive"`.

The element exists before its text changes; creating it together with its text is announced inconsistently. In WPF,
also raise the event from code after the text changes:

```csharp
var peer = UIElementAutomationPeer.FromElement(StatusText) ?? UIElementAutomationPeer.CreatePeerForElement(StatusText);
peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
```

Keep messages short and complete. Never move focus to a toast; announce it and leave focus where the player works.
The overlay's toasts are for a player looking at the game and need no announcement.
