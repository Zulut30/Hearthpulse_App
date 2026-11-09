#!/usr/bin/env python3
"""Visual regression: render the baseline frames of both UIs and compare them with the stored baselines.

Usage (from the repository root, isolated interpreter, needs Pillow with WebP):
  python -B -I tools/visual/visual_check.py render <out-dir> [--ui wpf|ava|both] [--no-build] [--artifacts <dir>]
  python -B -I tools/visual/visual_check.py compare <out-dir> [--ui wpf|ava|both]
  python -B -I tools/visual/visual_check.py update <out-dir> [--ui wpf|ava|both] [--scene <name>]...

- render: builds the app (Release) unless --no-build, then for each theme of THEMES runs `--render-ui` with render group
  `baseline` (the built executable itself, found by one MSBuild evaluation, or `dotnet run --no-build` when that fails;
  each run's time is printed) on a fresh copy of the fixture data folder tests/visual/data, isolated (HEARTHPULSE_ISOLATED=1: no network, no Hearthstone install), and keeps
  that theme's frames as <out-dir>/<ui>/<theme>-<scene>.png. The real data folder is never read or written.
- compare: each kept frame against tests/visual/baselines/<ui>/<theme>-<scene>.webp. A pixel counts as changed when
  one channel moves by more than THRESHOLD; a frame fails when more than MAX_CHANGED of its pixels changed or its size
  changed (boxes in MASKS, such as the app version, are left out). Writes before | after | diff sheets to <out-dir>/diff/<ui>/. Frames listed in PENDING are reported, never
  failed (their screens are being redesigned). Exit 1 on a failure or a missing frame or baseline.
- update: stores the rendered frames as the new baselines (lossless WebP), all of them or only the --scene ones
  (e.g. --scene Tavern-main-overview). Look at the diff sheets first.

Baselines are taken from the CI runner's renders (job "Visual baselines", artifact visual-renders): WPF draws text
with the system's fonts, so a PC with other fonts renders other pixels.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from PIL import Image, ImageChops

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests/visual/data"
BASELINES = ROOT / "tests/visual/baselines"
PROJECTS = {"wpf": "src/Hearthpulse.App", "ava": "src/Hearthpulse.Desktop"}
NUGET = "https://api.nuget.org/v3/index.json"

THRESHOLD = 8  # per-channel difference that counts as a change: renders on the same runner differ by less
MAX_CHANGED = 0.0002  # share of changed pixels a frame may have and still pass (0.02 %)

PAGES = ["main-overview", "main-battlegrounds", "main-standard", "main-history", "main-settings"]
OVERLAY = ["overlay-counters-16x9", "overlay-counters-16x10", "overlay-counters-4x3", "overlay-odds-16x9"]
# The default theme gets every frame; two more (a cold one and the darkest) the pages people see first and one overlay.
THEMES = {
    "Tavern": PAGES + OVERLAY,
    "Frost": ["main-overview", "main-settings", "overlay-counters-16x9"],
    "Shadow": ["main-overview", "main-settings", "overlay-counters-16x9"],
}
# Screens being redesigned: compared and reported, never failed, until their baselines are taken again (README).
PENDING: dict[str, str] = {}
# Areas left out of the comparison (left, top, right, bottom), by scene prefix: the app version under the sidebar
# changes with every release.
MASKS = {
    "main-": [(16, 800, 140, 832)],
}


def frames() -> list[str]:
    return [f"{theme}-{scene}" for theme, scenes in THEMES.items() for scene in scenes]


def scene_of(frame: str) -> str:
    return frame.split("-", 1)[1]


def uis(which: str) -> list[str]:
    chosen = ["wpf", "ava"] if which == "both" else [which]
    if sys.platform != "win32" and "wpf" in chosen:
        print("wpf: skipped (Windows only)")
        chosen.remove("wpf")
    return chosen


def launcher(project: str, artifacts: str | None) -> list[str]:
    """The command that starts the built app: the executable `dotnet run` would start (RunCommand, one MSBuild
    evaluation instead of one per run), or `dotnet run --no-build` when it cannot be found."""
    extra = ["--artifacts-path", artifacts] if artifacts else []
    fallback = ["dotnet", "run", "--project", project, "-c", "Release", "--no-build", *extra, "--"]
    query = ["dotnet", "msbuild", project, "-nologo", "-p:Configuration=Release",
             "-getProperty:RunCommand", "-getProperty:RunArguments", "-getProperty:RunWorkingDirectory"]
    if artifacts:
        query.append(f"-p:ArtifactsPath={Path(artifacts).resolve()}")
    try:
        result = subprocess.run(query, capture_output=True, text=True, timeout=120, check=False)
        found = json.loads(result.stdout)["Properties"] if result.returncode == 0 else {}
    except (OSError, subprocess.TimeoutExpired, ValueError, KeyError, TypeError):
        found = {}
    command = found.get("RunCommand", "")
    # Arguments or a working folder of its own would need dotnet run's handling of them; this project has neither.
    if not command or not Path(command).is_file() or found.get("RunArguments") or found.get("RunWorkingDirectory"):
        print(f"{project}: built executable not found, using dotnet run")
        return fallback
    return [command]


def render(out: Path, which: str, build: bool, artifacts: str | None) -> int:
    failed = False
    extra = ["--artifacts-path", artifacts] if artifacts else []
    for ui in uis(which):
        project = PROJECTS[ui]
        if build:
            subprocess.run(["dotnet", "build", project, "-c", "Release", "--source", NUGET, "-v", "q", *extra], check=True)
        start = launcher(project, artifacts)
        target = out / ui
        target.mkdir(parents=True, exist_ok=True)
        for old in target.glob("*.png"):
            old.unlink()
        for theme, scenes in THEMES.items():
            with tempfile.TemporaryDirectory(prefix="hp-visual-") as temporary:
                data, staging = Path(temporary) / "data", Path(temporary) / "frames"
                shutil.copytree(FIXTURE, data)
                env = dict(os.environ, HEARTHPULSE_DATA=str(data), HEARTHPULSE_ISOLATED="1",
                           HEARTHPULSE_RENDER_ONLY="baseline", HEARTHPULSE_THEME=theme)
                log = out / f"{ui}-{theme}-render.log"
                began = time.monotonic()
                with open(log, "w", encoding="utf-8") as handle:
                    code = subprocess.run([*start, "--render-ui", str(staging)],
                                          env=env, stdout=handle, stderr=subprocess.STDOUT, timeout=600).returncode
                seconds = time.monotonic() - began
                kept = 0
                for scene in scenes:
                    source = staging / f"{scene}.png"
                    if source.is_file() and source.stat().st_size > 0:
                        shutil.copy2(source, target / f"{theme}-{scene}.png")
                        kept += 1
                print(f"{ui} {theme}: exit {code}, {kept}/{len(scenes)} frames, {seconds:.1f} s (log {log})")
                failed |= code != 0 or kept != len(scenes)
    return 1 if failed else 0


def masks(scene: str) -> list[tuple[int, int, int, int]]:
    return [box for prefix, boxes in MASKS.items() if scene.startswith(prefix) for box in boxes]


def difference(baseline: Image.Image, rendered: Image.Image, ignored=()) -> tuple[float, Image.Image | None]:
    """Share of changed pixels and the mask of them, leaving out the ignored boxes; a size change counts as all changed."""
    a, b = baseline.convert("RGBA"), rendered.convert("RGBA")
    if a.size != b.size:
        return 1.0, None
    for box in ignored:
        a.paste((0, 0, 0, 0), box)
        b.paste((0, 0, 0, 0), box)
    channels = ImageChops.difference(a, b).split()
    maximum = channels[0]
    for channel in channels[1:]:
        maximum = ImageChops.lighter(maximum, channel)
    mask = maximum.point(lambda v: 255 if v > THRESHOLD else 0)
    return mask.histogram()[255] / (a.width * a.height), mask


def sheet(baseline: Image.Image, rendered: Image.Image, mask: Image.Image | None, path: Path) -> None:
    """before | after | changed pixels in red over a dimmed after."""
    width, height = max(baseline.width, rendered.width), max(baseline.height, rendered.height)
    shown = []
    for image in (baseline, rendered):
        canvas = Image.new("RGBA", (width, height), (30, 30, 30, 255))
        canvas.alpha_composite(image.convert("RGBA"))
        shown.append(canvas.convert("RGB"))
    diff = Image.blend(Image.new("RGB", (width, height)), shown[1], 0.35)
    if mask is not None:
        diff.paste(Image.new("RGB", (width, height), (255, 40, 40)), mask=mask)
    result = Image.new("RGB", (width * 3 + 20, height), (30, 30, 30))
    for i, image in enumerate((*shown, diff)):
        result.paste(image, (i * (width + 10), 0))
    if result.width > 3000:
        result = result.resize((3000, int(result.height * 3000 / result.width)))
    path.parent.mkdir(parents=True, exist_ok=True)
    result.save(path)


def compare(out: Path, which: str) -> int:
    failed = []
    for ui in uis(which):
        for frame in frames():
            rendered_path = out / ui / f"{frame}.png"
            baseline_path = BASELINES / ui / f"{frame}.webp"
            pending = PENDING.get(scene_of(frame))
            if not rendered_path.is_file() or not baseline_path.is_file():
                what = "not rendered" if not rendered_path.is_file() else "no baseline"
                print(f"{ui}/{frame}: {what}" + (f" (pending {pending})" if pending else ""))
                if not pending:
                    failed.append(f"{ui}/{frame}")
                continue
            with Image.open(baseline_path) as b, Image.open(rendered_path) as r:
                baseline, rendered = b.copy(), r.copy()
            changed, mask = difference(baseline, rendered, masks(scene_of(frame)))
            if changed == 0:
                continue
            sheet(baseline, rendered, mask, out / "diff" / ui / f"{frame}.png")
            over = changed > MAX_CHANGED
            note = "size changed" if mask is None else f"changed {changed:.2%}"
            verdict = "FAIL" if over and not pending else ("pending " + pending if pending else "within tolerance")
            print(f"{ui}/{frame}: {note} - {verdict}")
            if over and not pending:
                failed.append(f"{ui}/{frame}")
    if failed:
        print(f"{len(failed)} frame(s) differ from the baselines; diff sheets in {out / 'diff'}")
        return 1
    print("visual baselines: all frames match")
    return 0


def update(out: Path, which: str, only: list[str]) -> int:
    written = 0
    for ui in uis(which):
        (BASELINES / ui).mkdir(parents=True, exist_ok=True)
        for frame in frames():
            if only and frame not in only:
                continue
            source = out / ui / f"{frame}.png"
            if not source.is_file():
                print(f"{ui}/{frame}: not rendered, kept as it was")
                continue
            with Image.open(source) as image:
                image.save(BASELINES / ui / f"{frame}.webp", "WEBP", lossless=True, quality=100, method=6)
            written += 1
    print(f"{written} baseline(s) written to {BASELINES}")
    return 0


def main(argv: list[str]) -> int:
    if len(argv) < 2 or argv[0] not in ("render", "compare", "update"):
        print(__doc__)
        return 2
    command, out, rest = argv[0], Path(argv[1]).resolve(), argv[2:]
    which, build, artifacts, only = "both", True, None, []
    while rest:
        flag = rest.pop(0)
        if flag == "--ui":
            which = rest.pop(0)
        elif flag == "--no-build":
            build = False
        elif flag == "--artifacts":
            artifacts = rest.pop(0)
        elif flag == "--scene":
            only.append(rest.pop(0))
        else:
            print(f"unknown option {flag}")
            return 2
    if which not in ("wpf", "ava", "both"):
        print("--ui is wpf, ava or both")
        return 2
    if command == "render":
        return render(out, which, build, artifacts)
    if command == "compare":
        return compare(out, which)
    return update(out, which, only)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
