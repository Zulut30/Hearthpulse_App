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
"""
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PROJECTS = {"wpf": "src/Hearthpulse.App", "ava": "src/Hearthpulse.Desktop"}
NUGET = "https://api.nuget.org/v3/index.json"


def real_data_dir() -> Path:
    if sys.platform == "win32":
        return Path(os.environ["APPDATA"]) / "Hearthpulse"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "Hearthpulse"
    return Path.home() / ".local" / "share" / "Hearthpulse"


def ensure_copy(target: Path) -> None:
    if (target / "settings.json").exists():
        return
    source = real_data_dir()
    target.mkdir(parents=True, exist_ok=True)
    for name in ("settings.json", "history.db"):
        if (source / name).exists():
            shutil.copy2(source / name, target / name)
    if (source / "cache").is_dir():
        shutil.copytree(source / "cache", target / "cache", dirs_exist_ok=True)
    print(f"data copy: {source} -> {target}")


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

    failed = 0
    for ui in (["wpf", "ava"] if which == "both" else [which]):
        if ui == "wpf" and sys.platform != "win32":
            print("wpf: skipped (Windows only)")
            continue
        project = PROJECTS[ui]
        target = out / ui
        target.mkdir(parents=True, exist_ok=True)
        subprocess.run(["dotnet", "restore", project, "--source", NUGET, "--artifacts-path", str(artifacts)],
                       check=True, stdout=subprocess.DEVNULL)
        log = out / f"{ui}-render.log"
        with open(log, "w", encoding="utf-8") as handle:
            code = subprocess.run(["dotnet", "run", "--project", project, "--no-restore", "--artifacts-path",
                                   str(artifacts), "--", "--render-ui", str(target)],
                                  env=env, stdout=handle, stderr=subprocess.STDOUT).returncode
        pngs = sorted(p.name for p in target.glob("*.png"))
        print(f"{ui}: exit {code}, {len(pngs)} PNG in {target} (log {log})")
        failed |= code != 0
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
