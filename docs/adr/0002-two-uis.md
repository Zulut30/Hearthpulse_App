# Two UIs: WPF is the Windows product, Avalonia the cross-platform preview; both stay and converge

The installer and the Windows portable zip ship the WPF app (`src/Hearthpulse.App`); the Avalonia app
(`src/Hearthpulse.UI` in the host `src/Hearthpulse.Desktop`) ships as the Windows preview zip and as the Linux and
macOS packages (`.github/workflows/release.yml`, skill `hearthpulse-release`). The owner decided on 2026-11-02 to keep
both and bring them closer rather than drop one: every user-facing feature goes into both UIs, shared behaviour goes
into Presentation ([0001](0001-shared-source-presentation.md)), and identical view trees become controls of the same
shape in each UI (roadmap package Q5).

## Considered options

- **Avalonia only.** One UI to maintain, but the WPF app is what Windows players run today; replacing it is a product
  risk, not a refactoring.
- **WPF only.** Drops the Linux and macOS builds.

## Consequences

- A feature is not done until both UIs have it, with Loc strings in ru and en, rendered in both (`--render-ui`).
- Platform differences that remain are listed in the presentation map and kept on purpose.
