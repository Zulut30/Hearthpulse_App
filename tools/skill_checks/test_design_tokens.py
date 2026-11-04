"""Behavior checks for tools/design/migrate_tokens.py on small markup snippets.

Run from the repository root: python -B -I -m unittest discover -s tools/skill_checks -v
"""
import importlib.util
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("migrate_tokens", ROOT / "tools/design/migrate_tokens.py")
migrate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migrate)


def run(markup, bom=False):
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "View.xaml"
        path.write_bytes((b"\xef\xbb\xbf" if bom else b"") + markup.encode("utf-8"))
        with redirect_stdout(StringIO()):
            migrate.main([str(path)])
        return path.read_bytes()


class MigrateTokensTests(unittest.TestCase):
    def test_font_sizes_become_ramp_tokens(self):
        out = run('<TextBlock FontSize="12.5" />\r\n<Setter Property="FontSize" Value="36" />').decode()
        self.assertEqual(out, '<TextBlock FontSize="{StaticResource FontSizeLabel}" />\r\n'
                              '<Setter Property="FontSize" Value="{StaticResource FontSizeDisplay}" />')

    def test_spacing_snaps_to_the_scale_and_ties_go_up(self):
        out = run('<Border Margin="0,6,-14,110" Padding="9,2" />').decode()
        self.assertEqual(out, '<Border Margin="0,8,-16,112" Padding="8,2" />')

    def test_radii_snap_but_pills_and_circles_keep_their_radius(self):
        out = run('<Border CornerRadius="6" /><Border Width="22" Height="22" CornerRadius="11" />'
                  '<Border Height="20" CornerRadius="10" /><Setter Property="CornerRadius" Value="3" />').decode()
        self.assertEqual(out, '<Border CornerRadius="8" /><Border Width="22" Height="22" CornerRadius="11" />'
                              '<Border Height="20" CornerRadius="10" /><Setter Property="CornerRadius" Value="4" />')

    def test_bindings_resources_and_other_attributes_stay(self):
        markup = '<TextBlock FontSize="{Binding Size}" Margin="{StaticResource CardPadding}" Width="13.5" Text="a > b" />'
        self.assertEqual(run(markup).decode(), markup)

    def test_a_second_run_changes_nothing_and_the_bom_survives(self):
        once = run('<TextBlock FontSize="11.5" Margin="3" />', bom=True)
        self.assertTrue(once.startswith(b"\xef\xbb\xbf"))
        self.assertEqual(run(once[3:].decode(), bom=True), once)


if __name__ == "__main__":
    unittest.main()
