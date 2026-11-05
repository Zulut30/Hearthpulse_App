"""Checks for tools/visual/visual_check.py (T5 visual regression), without starting .NET or a UI.

Run from the repository root: python -B -I -m unittest discover -s tools/skill_checks -v
Requires Pillow with WebP, the dependency of visual_check.py itself.
"""
import importlib.util
import re
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path
from unittest.mock import patch

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("visual_check", ROOT / "tools/visual/visual_check.py")
visual = importlib.util.module_from_spec(spec)
spec.loader.exec_module(visual)


class VisualBaselineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="hp-visual-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_the_frames_are_the_ones_both_renderers_draw(self):
        # VisualBaseline (Core) names the pages and screens the render group "baseline" saves.
        source = (ROOT / "src/Hearthpulse.Core/Hosting/VisualBaseline.cs").read_text(encoding="utf-8")
        pages = re.search(r"Pages \{ get; \} = \[(.*?)\];", source).group(1)
        rendered = {f"main-{p.lower()}" for p in re.findall(r'"(\w+)"', pages)}
        screens = re.findall(r'\("(\w+)", \d+, \d+\)', source)
        moments = re.findall(r'\("(\w+)", (?:cards|_) =>', source)
        rendered |= {f"overlay-{m}-{n}" for m in moments for n in screens}
        wanted = {scene for scenes in visual.THEMES.values() for scene in scenes}
        self.assertLessEqual(wanted, rendered)
        self.assertLessEqual(set(visual.PENDING), wanted)
        # Every screen shape is watched (16:9, 16:10, 4:3).
        self.assertEqual({"16x9", "16x10", "4x3"}, {s.rsplit("-", 1)[1] for s in wanted if s.startswith("overlay-")})
        self.assertGreaterEqual(len(visual.THEMES), 3)
        self.assertTrue(20 <= 2 * len(visual.frames()) <= 32, len(visual.frames()))

    def test_every_frame_has_a_lossless_baseline_in_both_uis(self):
        for ui in ("wpf", "ava"):
            for frame in visual.frames():
                with self.subTest(ui=ui, frame=frame):
                    path = visual.BASELINES / ui / f"{frame}.webp"
                    self.assertTrue(path.is_file(), path)
                    with Image.open(path) as image:
                        self.assertEqual("WEBP", image.format)

    def test_noise_passes_masked_boxes_are_left_out_and_a_size_change_is_a_full_change(self):
        before = Image.new("RGB", (100, 10), (0, 0, 0))
        after = before.copy()
        after.putpixel((0, 0), (8, 8, 8))
        self.assertEqual(0, visual.difference(before, after)[0])
        after.putpixel((1, 0), (0, 0, 9))
        self.assertAlmostEqual(0.001, visual.difference(before, after)[0])
        self.assertEqual(0, visual.difference(before, after, [(0, 0, 2, 1)])[0])
        self.assertEqual((1.0, None), visual.difference(before, Image.new("RGB", (100, 11))))
        self.assertEqual([(16, 800, 140, 832)], visual.masks("main-overview"))
        self.assertEqual([], visual.masks("overlay-counters-16x9"))

    def compare(self, frames):
        """Baseline and render folders of one UI with the given (baseline, rendered) images per frame."""
        run = Path(tempfile.mkdtemp(dir=self.root))
        baselines, out = run / "baselines", run / "out"
        for frame, (baseline, rendered) in frames.items():
            if baseline is not None:
                (baselines / "ava").mkdir(parents=True, exist_ok=True)
                baseline.save(baselines / "ava" / f"{frame}.webp", "WEBP", lossless=True)
            if rendered is not None:
                (out / "ava").mkdir(parents=True, exist_ok=True)
                rendered.save(out / "ava" / f"{frame}.png")
        printed = StringIO()
        with patch.object(visual, "BASELINES", baselines), patch.object(visual, "frames", lambda: list(frames)), redirect_stdout(printed):
            code = visual.compare(out, "ava")
        return code, printed.getvalue(), out

    def test_a_frame_over_the_tolerance_fails_with_a_sheet_and_a_pending_one_only_reports(self):
        grey, red = Image.new("RGB", (40, 40), (90, 90, 90)), Image.new("RGB", (40, 40), (200, 40, 40))
        code, printed, out = self.compare({"Tavern-main-overview": (grey, red), "Tavern-overlay-counters-16x9": (grey, red)})
        self.assertEqual(1, code)
        self.assertIn("ava/Tavern-main-overview: changed 100.00% - FAIL", printed)
        self.assertIn("ava/Tavern-overlay-counters-16x9: changed 100.00% - pending V3 (overlay)", printed)
        self.assertTrue((out / "diff/ava/Tavern-main-overview.png").is_file())

    def test_identical_frames_pass_and_a_missing_baseline_or_render_fails(self):
        grey = Image.new("RGB", (40, 40), (90, 90, 90))
        self.assertEqual(0, self.compare({"Frost-main-settings": (grey, grey.copy())})[0])
        code, printed, _ = self.compare({"Frost-main-settings": (None, grey)})
        self.assertEqual(1, code)
        self.assertIn("ava/Frost-main-settings: no baseline", printed)
        code, printed, _ = self.compare({"Frost-main-overview": (grey, None)})
        self.assertEqual(1, code)
        self.assertIn("ava/Frost-main-overview: not rendered", printed)

    def test_update_writes_lossless_baselines_only_for_the_chosen_frames(self):
        out, baselines = self.root / "out", self.root / "baselines"
        (out / "ava").mkdir(parents=True)
        image = Image.new("RGB", (8, 8), (12, 34, 56))
        image.putpixel((3, 3), (250, 1, 2))
        for frame in ("Tavern-main-overview", "Tavern-main-history"):
            image.save(out / "ava" / f"{frame}.png")
        with patch.object(visual, "BASELINES", baselines), \
                patch.object(visual, "frames", lambda: ["Tavern-main-overview", "Tavern-main-history"]), \
                redirect_stdout(StringIO()):
            visual.update(out, "ava", ["Tavern-main-history"])
        self.assertFalse((baselines / "ava/Tavern-main-overview.webp").exists())
        with Image.open(baselines / "ava/Tavern-main-history.webp") as stored:
            self.assertEqual(image.tobytes(), stored.convert("RGB").tobytes())


if __name__ == "__main__":
    unittest.main()
