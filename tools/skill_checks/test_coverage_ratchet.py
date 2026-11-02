"""Behavior checks for tools/coverage/coverage_ratchet.py on small hand-written cobertura reports.

Run from the repository root: python -B -I -m unittest discover -s tools/skill_checks -v
"""
import importlib.util
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("coverage_ratchet", ROOT / "tools/coverage/coverage_ratchet.py")
ratchet = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ratchet)


def report(source, classes):
    """A cobertura file: classes = [(filename, [(line, hits)])]."""
    body = "".join(
        f'<class name="C" filename="{name}"><lines>'
        + "".join(f'<line number="{n}" hits="{h}" branch="False" />' for n, h in lines)
        + "</lines></class>"
        for name, lines in classes)
    return (f'<?xml version="1.0"?><coverage><sources><source>{source}</source></sources>'
            f'<packages><package name="P"><classes>{body}</classes></package></packages></coverage>')


class CoverageRatchetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="hp-coverage-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def write(self, folder, text):
        path = self.root / folder / "guid" / "coverage.cobertura.xml"
        path.parent.mkdir(parents=True)
        path.write_text(text, encoding="utf-8")

    def baseline(self, floors):
        path = self.root / "baseline.json"
        path.write_text(json.dumps({"allowed_drop": 0.5, "floors": floors, "targets": {"Core": 80}}), encoding="utf-8")
        return str(path)

    def run_main(self, floors):
        out = StringIO()
        with redirect_stdout(out):
            code = ratchet.main([str(self.root / "a"), str(self.root / "b"), "--baseline", self.baseline(floors)])
        return code, out.getvalue()

    def test_shared_lines_are_covered_when_any_suite_ran_them(self):
        # The same Presentation file compiled into both apps: line 2 ran only in one, line 3 in none.
        self.write("a", report("D:\\repo\\", [("src\\Hearthpulse.Presentation\\Vm.cs", [(1, 1), (2, 0), (3, 0)])]))
        self.write("b", report("/home/runner/repo/src/", [("Hearthpulse.Presentation/Vm.cs", [(1, 0), (2, 4), (3, 0)])]))
        result, reports = ratchet.summarize([self.root / "a", self.root / "b"])
        self.assertEqual(2, reports)
        self.assertEqual(3, result["Presentation"]["lines"])
        self.assertAlmostEqual(200 / 3, result["Presentation"]["line_rate"])

    def test_generated_and_test_files_are_left_out(self):
        self.write("a", report("D:\\repo\\", [
            ("src\\Hearthpulse.Core\\obj\\Release\\X.cs", [(1, 0)]),
            ("src\\Hearthpulse.Core\\Tracking\\CounterCards.g.cs", [(1, 0)]),
            ("tests\\Hearthpulse.Tests\\T.cs", [(1, 0)]),
            ("src\\Hearthpulse.Core\\A.cs", [(1, 1)]),
        ]))
        result, _ = ratchet.summarize([self.root / "a"])
        self.assertEqual({"Core": 1}, {m: d["lines"] for m, d in result.items()})

    def test_a_drop_beyond_the_allowance_fails_and_within_it_passes(self):
        self.write("a", report("D:\\repo\\", [("src\\Hearthpulse.Core\\A.cs", [(n, 1 if n <= 70 else 0) for n in range(1, 101)])]))
        self.write("b", report("D:\\repo\\", []))
        self.assertEqual(0, self.run_main({"Core": 70.5})[0])
        code, out = self.run_main({"Core": 70.6})
        self.assertEqual(1, code)
        self.assertIn("::error title=Coverage ratchet::Core", out)

    def test_a_rise_asks_for_a_higher_floor(self):
        self.write("a", report("D:\\repo\\", [("src\\Hearthpulse.Core\\A.cs", [(1, 1), (2, 1)])]))
        self.write("b", report("D:\\repo\\", []))
        code, out = self.run_main({"Core": 90})
        self.assertEqual(0, code)
        self.assertIn("raise the floor to 100.0", out)

    def test_a_module_with_a_floor_and_no_data_fails(self):
        self.write("a", report("D:\\repo\\", [("src\\Hearthpulse.Core\\A.cs", [(1, 1)])]))
        self.write("b", report("D:\\repo\\", []))
        code, out = self.run_main({"Core": 0, "Presentation": 50})
        self.assertEqual(1, code)
        self.assertIn("Presentation: no coverage data", out)

    def test_the_checked_in_baseline_has_a_floor_for_core_and_presentation(self):
        baseline = json.loads((ROOT / "tools/coverage/baseline.json").read_text(encoding="utf-8"))
        self.assertLessEqual({"Core", "Presentation"}, set(baseline["floors"]))
        self.assertEqual({"Core": 80, "Presentation": 70}, {m: baseline["targets"][m] for m in ("Core", "Presentation")})


if __name__ == "__main__":
    unittest.main()
