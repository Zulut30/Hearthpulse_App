# Visual baselines

`tools/visual/visual_check.py` renders the frames of render group `baseline` (`VisualBaseline` in Core) in both UIs
and three themes, and compares them with `baselines/<ui>/<theme>-<scene>.webp` (lossless WebP):

- main-window pages (`main-*`) with three made-up games (`VisualBaseline.Games`: Battlegrounds, ranked Standard and
  Wild, dated in January 2026 with no time zone);
- the overlay over two made-up moments (`VisualBaseline.Moments`: `counters`, a Constructed game with the counters
  panel; `odds`, a Battlegrounds fight), on 16:9, 16:10 and 4:3 screens (`overlay-<moment>-<screen>`).

A pixel counts as changed when one RGBA channel moves by more than 8; a frame fails when more than 0.02 % of its
pixels changed or its size did. The app version under the sidebar is masked (`MASKS`).

## Where the frames come from

- `data/` is the fixture data folder each render starts from (a fresh copy every time): Russian, migration already
  offered, no history until the made-up games are added.
- Renders run isolated (`HEARTHPULSE_ISOLATED=1`): no network, no Hearthstone install found, and a text font every
  system has (WPF: Segoe UI, not Segoe UI Variable; Avalonia: the bundled Inter). So no frame depends on the servers,
  the PC's game and its logs, or its fonts.
- The WPF main window is laid out at 1260 x 840 for its frames whatever the screen (the runner's is 1024 x 768, and
  Windows keeps a window within the screen); every frame is drawn at 96 DPI.
- **Baselines are taken on the CI runner** (job "Visual baselines", Windows). Two runs there match exactly; another PC
  still differs a little (text antialiasing, the system's number formats: "100%" there, "100 %" on a Russian Windows
  11), so a render on another PC is compared, never committed.

## A wanted change

1. Push the branch and start CI on it: `gh workflow run ci.yml --ref <branch>`.
2. Download its artifact `visual-renders`: `gh run download <run-id> -n visual-renders -D <dir>`.
3. Read the sheets in `<dir>/diff/`: each changed frame, before | after | changed pixels in red.
4. `python -B -I tools/visual/visual_check.py update <dir>` (or `--scene Tavern-main-overview` for one frame), and commit
   the baselines with the change.

## After V3 (overlay and decks redesign)

The overlay frames and `main-standard` are in `PENDING`: compared and reported, never failed, until V3 lands. Then:

1. dispatch CI on the branch with V3 (`gh workflow run ci.yml --ref <branch>`);
2. download the artifact `visual-renders` (it is uploaded for pending frames too);
3. `python -B -I tools/visual/visual_check.py update <dir>`;
4. delete the V3 entries from `PENDING` in `tools/visual/visual_check.py`;
5. commit the baselines and the script together.

## Locally

- Render and compare (Hearthstone closed; needs Pillow with WebP):
  `python -B -I tools/visual/visual_check.py render <out> --artifacts <short-dir>` then
  `python -B -I tools/visual/visual_check.py compare <out>`.
- Expect small text differences against the CI baselines on a PC with another DPI or font version: read the sheets in
  `<out>/diff/` for what you changed on purpose, and never `update` from a local render.
- One UI only: `--ui wpf` or `--ui ava`; without a build: `--no-build`.
