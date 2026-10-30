# Motion and scale

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-accessibility/motion-and-zoom.md`
> at `592aedc`, MIT, © 2026 Jakub Krehel, see [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes:
> `prefers-reduced-motion` replaced by Windows' animation setting; browser zoom and 320 px reflow replaced by the
> window minimum, Avalonia compact and overlay scale.

The Windows animation setting, timed UI and the sizes every view must survive.

## The Windows animation setting

"Animation effects" in Windows settings is the player's reduced-motion switch. WPF reads it as
`SystemParameters.ClientAreaAnimation`; `MainWindow.xaml.cs` checks it before the nav pill slide and the page reveal.
Every new animation takes the same guard:

```csharp
if (SystemParameters.ClientAreaAnimation)
    element.BeginAnimation(UIElement.OpacityProperty, fade);
else
    element.Opacity = 1;
```

Storyboards in `Themes/Theme.xaml` are covered already: with the setting off, `ThemeEngine` rebuilds the theme with
every `Duration` and `BeginTime` at zero. A storyboard in a view's XAML (`EventTrigger` + `BeginStoryboard`) or one
started from code is not, so it starts from code behind the guard or moves into a theme style.

Avalonia's Theme.axaml has `Transitions` of 0.25 s or less (hover, press): state feedback
with no guard yet. Any other Avalonia motion is a `hearthpulse-design` decision.

| Remove under the setting off | Replace | Keep |
| --- | --- | --- |
| Slides across the window, large scale changes, looping decoration | Slide or scale → instant change or opacity fade | Progress and loading indicators |
| Moving backdrops | Smooth scroll → instant jump | Hover and focus colour changes |
| | | A press shrink of 0.96 on buttons |

## Timed UI

- **Explicit dismissal over timers.** A toast may time out only when it carries nothing the player must act on. One
  with an action, an error or a link stays until dismissed.
- **At least 5 seconds.** In the main window the timer also pauses while the mouse is over the toast; the overlay
  is click-through, so its toasts only get the time.
- **Never the only place.** A message whose only copy is in a toast that vanished is lost information; the history,
  the match page or Settings keeps the durable copy.
- **Live game data is exempt** from 2.2.2: the turn timer, counters and odds update because the game does. A
  decorative loop is not exempt, and the themes allow none (`ThemeFileTests`).

## Scale

WPF and Avalonia lay out in device-independent pixels, so Windows display scaling changes how many DIPs fit on the
screen, not the layout rules. The sizes every change must survive:

| Surface | Size to check | How |
| --- | --- | --- |
| WPF main window | Minimum `1040 × 660` and the default `1260 × 840` | Resize by hand; the render uses 1260 × 840 |
| Avalonia main window | Compact below 900 px, rendered 420 wide (`main-*-compact`) | `hearthpulse-visual-check` |
| Overlay | `OverlayScale` 0.7 and 1.4 (the Settings slider; the code clamps to 0.6-1.6), on the 1080-high reference surface | `OverlayScale` in the data copy's `settings.json`, then a render |
| One overlay panel | Panel scale 0.6 to 1.6, `Compact` on and off | Layout mode, wheel and tag buttons |
| Any text | Russian interface text and Russian card names | A `"Language": "ru"` data copy |

Text containers take `MinHeight`, never a fixed `Height`; `better-layout` owns the rest of that fix.
