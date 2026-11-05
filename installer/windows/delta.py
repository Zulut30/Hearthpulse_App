"""Manifest and delta package of the Windows app, for delta updates (src/Hearthpulse.Core/Updates/DeltaUpdate.cs).

  delta.py manifest <app-dir> <version> <out.json>
      Every file of the published app with its size and SHA-256: Hearthpulse-Files-<version>-win-x64.json.
  delta.py delta <previous-manifest.json> <manifest.json> <app-dir> <out.zip>
      The files that differ from the previous release, with delta.json (both file lists) at the root:
      Hearthpulse-Delta-<previous>-<version>-win-x64.zip.
  delta.py simulate <manifest.json> <version> <out.json>
      A stand-in previous manifest for a trial release (no real previous one): two files changed, one added, one
      removed, so every path of the delta is built and checked.
"""
import hashlib
import json
import os
import re
import sys
import zipfile

# The same rule as DeltaUpdate.IsSafePath: the updater refuses any other path, so the release must not produce one.
SAFE = re.compile(r"^[A-Za-z0-9._+ -]+(/[A-Za-z0-9._+ -]+)*$")
DEVICES = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}


def safe(rel):
    """A path the updater takes: relative, no "."/".."/"...", no trailing dot or space, no Windows device name."""
    if not rel or len(rel) > 200 or not SAFE.match(rel):
        return False
    return all(part and part.strip() == part and not part.endswith(".") and part.split(".")[0].upper() not in DEVICES
               for part in rel.split("/"))


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def manifest(app_dir, version, out):
    files = []
    for base, dirs, names in os.walk(app_dir):
        dirs.sort()
        for name in sorted(names):
            full = os.path.join(base, name)
            rel = os.path.relpath(full, app_dir).replace(os.sep, "/")
            if not safe(rel):
                sys.exit(f"{rel}: the updater cannot take this file name")
            files.append({"path": rel, "size": os.path.getsize(full), "sha256": sha256(full)})
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"version": version, "rid": "win-x64", "files": files}, f, indent=1)
    print(f"manifest {version}: {len(files)} files")


def delta(previous_path, current_path, app_dir, out):
    with open(previous_path, encoding="utf-8") as f:
        previous = json.load(f)
    with open(current_path, encoding="utf-8") as f:
        current = json.load(f)
    bad = [e["path"] for e in previous["files"] + current["files"] if not safe(e["path"])]
    if bad:
        sys.exit(f"the updater cannot take these file names: {bad}")
    old = {e["path"].lower(): e for e in previous["files"]}
    carried = [e for e in current["files"] if old.get(e["path"].lower(), {}).get("sha256") != e["sha256"]]
    plan = {"from": previous["version"], "to": current["version"], "base": previous["files"], "target": current["files"]}
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        z.writestr("delta.json", json.dumps(plan, indent=1))
        for e in carried:
            z.write(os.path.join(app_dir, *e["path"].split("/")), "files/" + e["path"])
    new = {e["path"].lower() for e in current["files"]}
    removed = sum(1 for p in old if p not in new)
    print(f"delta {plan['from']} -> {plan['to']}: {len(carried)} files carried, {removed} removed, {os.path.getsize(out)} bytes")


def simulate(current_path, version, out):
    with open(current_path, encoding="utf-8") as f:
        current = json.load(f)
    files = []
    for e in current["files"]:
        if e["path"] == "THIRD-PARTY-NOTICES.txt":
            continue  # "added" in the new version
        if e["path"] in ("Hearthpulse.dll", "Hearthpulse.Core.dll"):
            e = dict(e, sha256="0" * 64)  # "changed"
        files.append(e)
    files.append({"path": "removed-in-this-version.txt", "size": 1, "sha256": "1" * 64})  # "removed"
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"version": version, "rid": "win-x64", "files": files}, f, indent=1)
    print(f"simulated previous manifest {version}")


if __name__ == "__main__":
    commands = {"manifest": manifest, "delta": delta, "simulate": simulate}
    if len(sys.argv) < 2 or sys.argv[1] not in commands:
        sys.exit(__doc__)
    commands[sys.argv[1]](*sys.argv[2:])
