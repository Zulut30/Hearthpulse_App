"""Hand-written C# files in src/ and tools/ stay at 600 lines or fewer, except those listed in ADR 0008.

Run from the repository root: python -B -I -m unittest discover -s tools/skill_checks -v
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ADR = ROOT / "docs/adr/0008-file-size.md"
LIMIT = 600


def exceptions():
    """Paths named in backticks in the first column of the ADR's exception table."""
    listed = set()
    for line in ADR.read_text(encoding="utf-8").splitlines():
        if not line.startswith("| `"):
            continue
        first = line.split("|")[1]
        listed.update(re.findall(r"`([^`]+)`", first))
    return listed


def oversized():
    found = []
    for folder in ("src", "tools"):
        for path in (ROOT / folder).rglob("*.cs"):
            parts = path.relative_to(ROOT).parts
            if "obj" in parts or "bin" in parts or path.name.endswith(".g.cs"):
                continue
            with path.open(encoding="utf-8", errors="replace") as f:
                lines = sum(1 for _ in f)
            if lines > LIMIT:
                found.append((path.relative_to(ROOT).as_posix(), lines))
    return found


class FileSizeTests(unittest.TestCase):
    def test_files_over_the_limit_are_listed_in_the_adr(self):
        listed = exceptions()
        unlisted = [f"{name} ({lines} lines)" for name, lines in oversized() if name not in listed]
        self.assertEqual([], unlisted, "Split these by responsibility or add them to docs/adr/0008-file-size.md")

    def test_listed_exceptions_still_exist(self):
        missing = [name for name in exceptions() if "*" not in name and not (ROOT / name).is_file()]
        self.assertEqual([], missing, "Drop exceptions that no longer exist from docs/adr/0008-file-size.md")


if __name__ == "__main__":
    unittest.main()
