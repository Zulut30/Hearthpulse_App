# Motion

> Source: [jakubkrehel/skills](https://github.com/jakubkrehel/skills) `skills/better-ui/animations.md`,
> `enter-exit.md`, `icon-transitions.md` and `performance.md` at `592aedc`, MIT, © 2026 Jakub Krehel, see
> [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md). Hearthpulse changes: four files merged; CSS, Tailwind and Motion
> rewritten as WPF storyboards and Avalonia transitions; blur removed; theme-switch and `will-change` recipes dropped
> (WPF recreates windows on a theme change).

Recipes for reversible state changes, press feedback, the animation guard, entrances, exits and glyph swaps.

## The guard

Every animation starts only when Windows' animation setting is on. In WPF that is `SystemParameters.ClientAreaAnimation`
(`MainWindow.xaml.cs` does it for the nav pill and page reveal):

```csharp
if (SystemParameters.ClientAreaAnimation)
{
    var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
    element.BeginAnimation(UIElement.OpacityProperty, fade);
}
else
{
    element.Opacity = 1;
}
```

The theme's hover and press feedback (0.25 s or less, no loop) is state feedback and stays; with the setting off,
`ThemeEngine` rebuilds Theme.xaml with every `Duration` and `BeginTime` at zero, so only storyboards in views and
animations started from code need the guard. Avalonia's Theme.axaml has `Transitions` of 0.25 s or less (hover, press): state feedback
with no guard yet. Any other Avalonia motion is a `hearthpulse-design` decision.

## Animations that reverse

```xml
<!-- WPF: both halves, no From, so a reversal starts where the last one stopped -->
<Trigger Property="IsMouseOver" Value="True">
    <Trigger.EnterActions>
        <BeginStoryboard>
            <Storyboard>
                <DoubleAnimation Storyboard.TargetName="HoverLayer" Storyboard.TargetProperty="Opacity"
                                 To="1" Duration="0:0:0.12" />
            </Storyboard>
        </BeginStoryboard>
    </Trigger.EnterActions>
    <Trigger.ExitActions>
        <BeginStoryboard>
            <Storyboard>
                <DoubleAnimation Storyboard.TargetName="HoverLayer" Storyboard.TargetProperty="Opacity"
                                 To="0" Duration="0:0:0.22" />
            </Storyboard>
        </BeginStoryboard>
    </Trigger.ExitActions>
</Trigger>
```

```xml
<!-- Avalonia: a transition on the property retargets by itself -->
<Border Classes="hoverLayer">
    <Border.Transitions>
        <Transitions>
            <DoubleTransition Property="Opacity" Duration="0:0:0.12" />
        </Transitions>
    </Border.Transitions>
</Border>
```

Name the property you animate; never animate layout properties (`Width`, `Height`, `Margin`) per frame, which re-runs
layout. Use `RenderTransform` (`ScaleTransform`, `TranslateTransform`) and `Opacity`.

## Press

The theme's button templates already press: `ScaleTransform` to `0.96` (icon buttons `0.9`) over `80 ms`, `CubicEase`
`EaseOut`, and back on release. A new button style is `BasedOn` an existing one or copies that template. The press
applies only while `IsEnabled` is true. Give a button an opt-out style where the motion would distract, such as a large
card that acts as a button.

## Entrances and exits

**Entrances** are for rare moments: a page's first load (the house page reveal: 280 ms rise + 220 ms fade,
`CubicEase`), a match result, an empty state appearing.

1. Split into title, body and actions.
2. Stagger them `100 ms` apart (`BeginTime`).
3. Each fades from `0` to `1` and rises from `12` DIP to `0` over `300 ms`, `CubicEase` `EaseOut`.

```xml
<Storyboard>
    <DoubleAnimation Storyboard.TargetName="Title" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.3" />
    <DoubleAnimation Storyboard.TargetName="Body" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.3" BeginTime="0:0:0.1" />
    <DoubleAnimation Storyboard.TargetName="Actions" Storyboard.TargetProperty="Opacity" To="1" Duration="0:0:0.3" BeginTime="0:0:0.2" />
</Storyboard>
```

Start it from code behind the guard. The hidden state is set by the same code, so with animation off the content is
simply visible.

**Exits** are shorter: opacity to `0` and a `-12` DIP shift over `150 ms`, `EaseOut`. Remove the element at once where
motion adds nothing. Overlay panels follow the game: they show and hide without motion or with a fade of `150 ms` at
most, and never slide across the game view.

## Glyph swaps

Where an MDI glyph swaps on a rare state change (copy to copied, pin toggled, play to pause), keep both glyphs in one
`Grid` cell and cross-fade:

| Property | From | To |
| --- | --- | --- |
| Opacity | `0` | `1` |
| Scale (`ScaleTransform`, centre origin) | `0.25` | `1` |
| Duration, easing | `300 ms` | `CubicEase` `EaseOut` |

The leaving glyph runs the same values in reverse. With animation off, swap `Visibility` at once.

| Animate | Swap at once |
| --- | --- |
| Copy to copied, pin toggled, play to pause | A tab's or nav item's glyph |
| A loading glyph turning into a result | Actions revealed on row hover |
| | Decorative and always-visible glyphs |

## Performance

- The overlay re-evaluates up to 10 times a second over a running game. Keep its motion to opacity and transforms,
  short, with no effects.
- Brushes, geometries and drawings from the theme are frozen (`ThemeEngine.Freeze`); an animated brush cannot be, so
  animate an element's `Opacity` instead of a shared brush.
- One storyboard per state change; never a `RepeatBehavior="Forever"` on decoration (`ThemeFileTests` rejects it in
  the themes).
