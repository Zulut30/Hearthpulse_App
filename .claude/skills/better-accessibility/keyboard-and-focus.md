# Keyboard and focus

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-accessibility/focus-and-keyboard.md`
> at `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> CSS and ARIA patterns rewritten as WPF/Avalonia focus visuals, keyboard navigation and dialogs; skip links and SPA
> routes removed.

Focus rings, tab order, groups, dialogs and popups, and the overlay exception.

## Focus rings

WPF shows a focus visual only for keyboard focus, which matches what the player expects. The theme defines one ring
per shape in `src/Hearthpulse.App/Themes/Theme.xaml`: `FocusRing`, `FocusRingChip`, `FocusRingNav`, `FocusRingRow`,
`FocusRingRound`, `FocusRingSwitch`. A control style points at the one that fits its shape:

```xml
<Style x:Key="GhostButton" TargetType="Button">
    <Setter Property="FocusVisualStyle" Value="{StaticResource FocusRingChip}" />
</Style>
```

Avalonia's Fluent theme draws a focus adorner on keyboard focus. A custom template keeps it; `FocusAdorner="{x:Null}"`
is allowed only with a replacement that shows the same state.

A new ring uses a token, never a hex value, and reaches 3:1 against every surface and state it crosses (hover,
selected, the gold `PrimaryFill`), in every preset. `better-colors` measures it.

## Tab order

- Tab order follows the element order in the XAML. Fix the order instead of setting `TabIndex`.
- `IsTabStop="False"` / `Focusable="False"` removes a control from the order. Use it only on decoration or on a control
  that has another keyboard path, and say which.
- `KeyboardNavigation.TabNavigation="Once"` on a container makes it one stop; `"Cycle"` keeps Tab inside it, which
  only a dialog or a popup should do.

## Groups

Groups take one Tab stop, and arrow keys move inside:

| Control | Keys |
| --- | --- |
| `RadioButton` group (`Segment`, `Tab`, `NavItem`; `segment`, `nav`) | Arrow keys move and select; one Tab stop |
| `ListBox` | Arrow keys move the selection; Home/End jump; one Tab stop |
| `ComboBox` | Alt+Down or F4 opens; arrows move; Enter accepts; Escape closes |
| `TabControl` | Ctrl+Tab and arrow keys switch tabs |
| `Slider` | Arrow keys step; Page Up/Down step further |

A custom group built from `Button`s needs `KeyboardNavigation.DirectionalNavigation="Cycle"` (WPF) or arrow-key
handling, or it costs a Tab stop per item.

## Dialogs and popups

A dialog is a `Window` shown modal with its owner, so everything behind it is out of reach:

```xml
<!-- WPF and Avalonia: Escape triggers the cancel button, Enter the default one -->
<Button Content="{i:Loc Deck.Versions.Cancel}" IsCancel="True" />
<Button Content="{i:Loc Deck.Versions.Merge}" IsDefault="True" Style="{StaticResource PrimaryButton}" />
```

- On open, focus the first control. For a destructive confirmation, focus the least destructive one.
- On close, focus returns to the control that opened it. Check it after closing with the keyboard.
- A `Popup` (WPF) or `Flyout` (Avalonia) that takes focus closes on Escape and gives focus back to its trigger.
  `Popup.StaysOpen="False"` closes on an outside click only; Escape needs a handler.
- Escape closes whatever opened last: tooltip, then menu, then popup, then dialog.

## Page changes

`ShowPage` swaps the main window's content without a navigation. Move focus to the new page's first heading or
control, so focus does not stay on a control the player can no longer see. The window title and the selected
`NavItem` already name the page.

## Shortcuts

System-wide hotkeys, which work while the game has focus, are registered in `App.xaml.cs` through
`_hotkeys.Register`; shortcuts that only work in the main window are `KeyBinding`s on it. The overlay's layout mode is
Ctrl+Shift+L.
A new shortcut does not override a key the focused control uses (arrows in lists, Space on buttons, typing in a
`TextBox`), and its action is also reachable without the shortcut.

## The overlay exception

The overlay window never takes keyboard focus: the game keeps it, and the overlay is click-through except over
`OverlayHit.Interactive` elements. So the overlay has no tab order and no focus rings. Its controls still need hit
areas, contrast and a cue beyond colour. Layout mode opens from Settings ("Arrange overlay panels"), the tray and
Ctrl+Shift+L; a new overlay feature that the player can switch on or off also gets its toggle in Settings
(`hearthpulse-overlay-panel`).
