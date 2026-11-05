# Visual baselines

`tools/visual/visual_check.py` renders the frames of render group `baseline` (`VisualBaseline` in Core: main-window
pages, the overlay's layout mode on 16:9, 16:10 and 4:3 screens) in both UIs and three themes, and compares them with
`baselines/<ui>/<theme>-<scene>.webp` (lossless WebP).

- `data/` is the fixture data folder each render starts from (a fresh copy every time): Russian, no Hearthstone
  install, migration already offered, no history. Renders run isolated (`HEARTHPULSE_ISOLATED=1`: no network, no Hearthstone install found),
  so no frame depends on what the servers answer, on the PC's game and its logs, or on the player's games.
- CI (job "Visual baselines", Windows) renders and compares on every code change; on a difference it uploads the
  renders and diff sheets as the artifact `visual-renders`.
- A wanted change: download that artifact, look at `diff/`, then
  `python -B -I tools/visual/visual_check.py update <artifact-dir>` and commit the new baselines. Baselines come from
  the CI runner: WPF draws text with the system's fonts.
