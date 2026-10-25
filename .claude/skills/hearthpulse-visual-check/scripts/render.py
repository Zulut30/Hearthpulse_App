#!/usr/bin/env python3
"""Render Hearthpulse UI scenes to PNG on a COPY of the data folder.

Usage (from the repository root, isolated interpreter):
  python -I .claude/skills/hearthpulse-visual-check/scripts/render.py <out-dir> [wpf|ava|both] [--theme NAME]
      [--data <data-copy-dir>] [--artifacts <short-build-dir>]

- Copies settings.json, history.db and cache/ from the real data folder into <data-copy-dir> once (default
  %TEMP%/hpd, or $TMPDIR/hpd); account.dat (the signed-in session), logs, backups and updates are never copied.
  Delete the copy to refresh it.
- Sets HEARTHPULSE_DATA to the copy for the child process only, so the real folder is never written.
- --theme sets HEARTHPULSE_THEME (WPF only: Tavern, Frost, Fel, Arcane, Ember, Tide, Shadow).
- --artifacts must be a SHORT path (default %TEMP%/hpa): a long one breaks loading of e_sqlite3.dll.
- Writes <out-dir>/wpf and/or <out-dir>/ava plus a render log per UI.
- Each render starts in an empty temporary folder; exit zero alone is not success: the six main pages and
  overlay-layout must be present and nonempty. Optional log-dependent scenes may be absent.
"""
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PROJECTS = {"wpf": "src/Hearthpulse.App", "ava": "src/Hearthpulse.Desktop"}
NUGET = "https://api.nuget.org/v3/index.json"
REQUIRED_SCENES = frozenset({
    "main-overview.png", "main-battlegrounds.png", "main-standard.png", "main-wild.png",
    "main-history.png", "main-settings.png", "overlay-layout.png",
})


def real_data_dir() -> Path:
    if sys.platform == "win32":
        return Path(os.environ["APPDATA"]) / "Hearthpulse"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "Hearthpulse"
    return Path.home() / ".local" / "share" / "Hearthpulse"


def ensure_copy(target: Path) -> None:
    source = real_data_dir().resolve()
    if target.resolve().is_relative_to(source):
        raise ValueError("--data must be a scratch copy outside the real Hearthpulse data folder")
    if (target / "settings.json").exists():
        return
    target.mkdir(parents=True, exist_ok=True)
    for name in ("settings.json", "history.db"):
        if (source / name).exists():
            shutil.copy2(source / name, target / name)
    if (source / "cache").is_dir():
        shutil.copytree(source / "cache", target / "cache", dirs_exist_ok=True)
    print(f"data copy: {source} -> {target}")


def missing_scenes(folder: Path) -> list[str]:
    """Mandatory scenes are independent of history/log availability in both renderers."""
    return sorted(name for name in REQUIRED_SCENES
                  if not (folder / name).is_file() or (folder / name).stat().st_size == 0)


def publish_scenes(source: Path, target: Path, out: Path) -> list[str]:
    """Keep the latest run's partial output for diagnosis, without leaving stale scenes in the result."""
    if not target.resolve().is_relative_to(out.resolve()):
        raise ValueError("render output must stay inside --out-dir")
    target.mkdir(parents=True, exist_ok=True)
    for previous in target.glob("*.png"):
        if previous.is_file() or previous.is_symlink():
            previous.unlink()
    pngs = sorted(source.glob("*.png"))
    for png in pngs:
        shutil.copy2(png, target / png.name)
    return [p.name for p in pngs]


def main() -> int:
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return 2
    out = Path(args.pop(0)).resolve()
    which = "both"
    if args and args[0] in ("wpf", "ava", "both"):
        which = args.pop(0)
    opts = dict(zip(args[::2], args[1::2]))
    temp = Path(tempfile.gettempdir())
    data = Path(opts.get("--data", temp / "hpd"))
    artifacts = Path(opts.get("--artifacts", temp / "hpa"))
    ensure_copy(data)

    env = dict(os.environ, HEARTHPULSE_DATA=str(data))
    if "--theme" in opts:
        env["HEARTHPULSE_THEME"] = opts["--theme"]

    failed = False
    ran = False
    for ui in (["wpf", "ava"] if which == "both" else [which]):
        if ui == "wpf" and sys.platform != "win32":
            print("wpf: skipped (Windows only)")
            continue
        project = PROJECTS[ui]
        target = out / ui
        out.mkdir(parents=True, exist_ok=True)
        subprocess.run(["dotnet", "restore", project, "--source", NUGET, "--artifacts-path", str(artifacts)],
                       check=True, stdout=subprocess.DEVNULL)
        log = out / f"{ui}-render.log"
        # A process can exit before rendering, or render only a few scenes. Files from an earlier run must not
        # disguise that failure; timestamp comparison would still accept files on coarse-resolution filesystems.
        with tempfile.TemporaryDirectory(prefix="hp-render-") as temporary:
            staging = Path(temporary)
            with open(log, "w", encoding="utf-8") as handle:
                code = subprocess.run(["dotnet", "run", "--project", project, "--no-restore", "--artifacts-path",
                                       str(artifacts), "--", "--render-ui", str(staging)],
                                      env=env, stdout=handle, stderr=subprocess.STDOUT).returncode
            missing = missing_scenes(staging)
            pngs = publish_scenes(staging, target, out)
        ran = True
        print(f"{ui}: exit {code}, {len(pngs)} PNG in {target} (log {log})")
        if missing:
            print(f"{ui}: missing or empty required scenes: {', '.join(missing)}")
        failed |= code != 0 or bool(missing)
    return 1 if failed or not ran else 0


if __name__ == "__main__":
    sys.exit(main())
