"""Keep the checked-in skill catalog discoverable and its local recipe links usable."""
import re
import unittest
from pathlib import Path
from urllib.parse import unquote, urlsplit

import yaml

ROOT = Path(__file__).resolve().parents[2]
SKILLS = sorted((ROOT / ".claude/skills").glob("*/SKILL.md"))


class SkillCatalogTests(unittest.TestCase):
    def test_every_skill_has_matching_name_and_a_nonempty_description(self):
        self.assertTrue(SKILLS, "No project skills discovered")
        names = set()
        for skill in SKILLS:
            with self.subTest(skill=skill.parent.name):
                source = skill.read_text(encoding="utf-8")
                self.assertTrue(source.startswith("---\n"), "Missing YAML frontmatter")
                front = yaml.safe_load(source.split("---", 2)[1])
                self.assertIsInstance(front, dict)
                self.assertEqual(skill.parent.name, front.get("name"))
                self.assertNotIn(front["name"], names)
                names.add(front["name"])
                self.assertIsInstance(front.get("description"), str)
                self.assertTrue(front["description"].strip())

    def test_relative_recipe_links_resolve_from_their_own_skill(self):
        checked = 0
        for skill in SKILLS:
            for href in re.findall(r"\[[^\]]*\]\(([^\s)]+)\)", skill.read_text(encoding="utf-8")):
                target = urlsplit(href)
                if target.scheme or target.netloc or not target.path:
                    continue
                with self.subTest(skill=skill.parent.name, link=href):
                    self.assertTrue((skill.parent / unquote(target.path)).is_file(), "Broken recipe link")
                checked += 1
        self.assertGreater(checked, 0, "No local recipe links discovered")


if __name__ == "__main__":
    unittest.main()
