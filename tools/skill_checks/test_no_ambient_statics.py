"""The shared view models take their environment through constructors (Etalon Q3): no ambient statics in Presentation.

Run from the repository root: python -B -I -m unittest discover -s tools/skill_checks -v
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PRESENTATION = ROOT / "src" / "Hearthpulse.Presentation"

# Loc.Instance stays (docs/adr/0007-loc-instance-stays-static.md).
FORBIDDEN = re.compile(r"AppPaths\.|(ReplayStore|ReplayLibrary|ReplayUploads)\.Default|ArchetypeTranslations\.Current|EffectRegistry\.Default")


class NoAmbientStaticsTests(unittest.TestCase):
    def test_presentation_reads_no_ambient_statics(self):
        found = []
        for path in PRESENTATION.rglob("*.cs"):
            if "obj" in path.relative_to(ROOT).parts:
                continue
            for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
                if FORBIDDEN.search(line) and not line.lstrip().startswith("///"):
                    found.append(f"{path.relative_to(ROOT).as_posix()}:{number}: {line.strip()}")
        self.assertEqual([], found)

    def test_the_removed_defaults_stay_removed(self):
        core = ROOT / "src" / "Hearthpulse.Core"
        for name in ("ReplayStore", "ReplayLibrary", "ReplayUploads", "ArchetypeTranslations"):
            for path in core.rglob("*.cs"):
                if "obj" in path.relative_to(ROOT).parts:
                    continue
                text = path.read_text(encoding="utf-8-sig")
                self.assertNotRegex(text, rf"public static {name}s? (Default|Current)", path.as_posix())


if __name__ == "__main__":
    unittest.main()
