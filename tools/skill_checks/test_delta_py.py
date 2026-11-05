"""installer/windows/delta.py builds what the updater (DeltaUpdate.cs) takes, and stops a release on a name it would not.

Run from the repository root: python -B -I -m unittest discover -s tools/skill_checks -v
"""
import json
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "installer" / "windows" / "delta.py"


def run(*args):
    return subprocess.run([sys.executable, "-B", "-I", str(SCRIPT), *map(str, args)], capture_output=True, text=True)


def app(folder, files):
    for path, content in files.items():
        full = folder / path
        full.parent.mkdir(parents=True, exist_ok=True)
        full.write_text(content, encoding="utf-8")
    return folder


class DeltaScriptTests(unittest.TestCase):
    def test_the_delta_carries_changed_and_added_files_and_both_lists(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            old = app(tmp / "old", {"a.dll": "old a", "keep.dll": "same", "gone.txt": "x"})
            new = app(tmp / "new", {"a.dll": "new a", "keep.dll": "same", "ru/new.dll": "added"})
            self.assertEqual(run("manifest", old, "1.0.0", tmp / "m1.json").returncode, 0)
            self.assertEqual(run("manifest", new, "1.1.0", tmp / "m2.json").returncode, 0)
            result = run("delta", tmp / "m1.json", tmp / "m2.json", new, tmp / "d.zip")
            self.assertEqual(result.returncode, 0, result.stderr)

            with zipfile.ZipFile(tmp / "d.zip") as z:
                self.assertEqual(sorted(z.namelist()), ["delta.json", "files/a.dll", "files/ru/new.dll"])
                plan = json.loads(z.read("delta.json"))
                self.assertEqual(z.read("files/a.dll"), b"new a")
            self.assertEqual((plan["from"], plan["to"]), ("1.0.0", "1.1.0"))
            self.assertEqual(sorted(f["path"] for f in plan["base"]), ["a.dll", "gone.txt", "keep.dll"])
            self.assertEqual(sorted(f["path"] for f in plan["target"]), ["a.dll", "keep.dll", "ru/new.dll"])
            self.assertTrue(all(len(f["sha256"]) == 64 for f in plan["target"]))

    def test_the_stand_in_previous_release_changes_adds_and_removes(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            new = app(tmp / "new", {"Hearthpulse.dll": "a", "Hearthpulse.Core.dll": "b", "THIRD-PARTY-NOTICES.txt": "c", "x.dll": "d"})
            run("manifest", new, "0.0.1", tmp / "m.json")
            self.assertEqual(run("simulate", tmp / "m.json", "0.0.0", tmp / "prev.json").returncode, 0)
            run("delta", tmp / "prev.json", tmp / "m.json", new, tmp / "d.zip")
            with zipfile.ZipFile(tmp / "d.zip") as z:
                self.assertEqual(len([n for n in z.namelist() if n.startswith("files/")]), 3)
                plan = json.loads(z.read("delta.json"))
            self.assertIn("removed-in-this-version.txt", [f["path"] for f in plan["base"]])

    def test_a_name_the_updater_refuses_stops_the_release(self):
        for name in ["CON", "nul.txt", "COM1.dll", "a.", "trailing ", "per%cent.dll", "amp&.dll"]:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as tmp:
                tmp = Path(tmp)
                folder = tmp / "app"
                folder.mkdir()
                try:
                    (folder / name).write_text("x", encoding="utf-8")
                except OSError:
                    continue  # Windows will not even create it; the rule itself is checked below
                if name not in [p.name for p in folder.iterdir()]:
                    continue  # Windows dropped the trailing dot or space; Linux (the release runner) keeps it
                result = run("manifest", folder, "1.0.0", tmp / "m.json")
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("cannot take this file name", result.stderr)

    def test_the_path_rule_matches_the_updater(self):
        sys.path.insert(0, str(SCRIPT.parent))
        try:
            import delta
        finally:
            sys.path.pop(0)
        for good in ["Hearthpulse.dll", "ru/PresentationCore.resources.dll", "a b+c_d-e.txt"]:
            self.assertTrue(delta.safe(good), good)
        for bad in ["", "../x", "a/../b", "...", "a.", "/abs", "C:/x", "back\\slash", "CON", "aux.json", "x/LPT9.dll",
                    "trailing ", " lead", "dir/"]:
            self.assertFalse(delta.safe(bad), bad)


if __name__ == "__main__":
    unittest.main()
