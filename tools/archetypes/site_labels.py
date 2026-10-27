"""Refreshes src/Hearthpulse.Core/Constructed/SiteArchetypeLabels.json from hearthpulse.net.

The site's archetype catalogues (Standard and Wild) are rendered with every archetype's English name ("archetype") and
the Russian label the site shows ("archetypeLabel", "translated": true). The app shows the same labels
(ArchetypeNames.SiteLabel). Run from the repository root: python tools/archetypes/site_labels.py
"""

import datetime
import json
import re
import sys
import urllib.request

PAGES = [
    "https://hearthpulse.net/standard/archetypes/",
    "https://hearthpulse.net/standard/archetypes/?format=wild",
]
OUT = "src/Hearthpulse.Core/Constructed/SiteArchetypeLabels.json"
PAIR = re.compile(r'"archetype":"([^"]+)","archetypeLabel":"([^"]+)","translated":(true|false)')


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Hearthpulse archetype labels"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read().decode("utf-8")


def main():
    labels = {}
    for url in PAGES:
        page = fetch(url).replace('\\"', '"')
        for english, russian, translated in PAIR.findall(page):
            if translated == "true":
                labels.setdefault(english, russian)

    if len(labels) < 100:
        sys.exit(f"Only {len(labels)} labels found: the page layout changed, the file is left as it is.")

    data = {
        "source": "https://hearthpulse.net/standard/archetypes/ (Standard and ?format=wild): archetype -> archetypeLabel, translated only",
        "fetched": datetime.date.today().isoformat(),
        "labels": dict(sorted(labels.items(), key=lambda pair: pair[0].lower())),
    }
    with open(OUT, "w", encoding="utf-8", newline="\n") as file:
        file.write(json.dumps(data, ensure_ascii=False, indent=2) + "\n")
    print(f"{len(labels)} labels written to {OUT}")


if __name__ == "__main__":
    main()
