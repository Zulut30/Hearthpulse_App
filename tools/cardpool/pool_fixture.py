#!/usr/bin/env python3
"""Test data for the card pool engine (tests/Hearthpulse.Tests/CardPoolTests.cs).

  fetch <dir>                       download every page of /api/v1/constructed-cards?format=wild into <dir>
  fixture <dir> <cards.json> <out>  write the fixture (Fixtures/card-pool.json.gz) from those pages + HearthstoneJSON
  check <dir> <cards.json>          print the pool sizes of the tested generators, counted with hand-written filters

The fixture has the shape CardPoolCatalog.Read expects (the disk cache): the Wild catalog with HearthstoneJSON minion
types for dual-type minions, English text only for Standard cards (keeps the file small). `check` does not use the
C# parser: each filter below is written by hand from the card text, so it is an independent count of the expected
sizes in Real_standard_generators_have_the_pools_counted_by_hand. After a rotation: fetch, fixture, check, then update
the expected numbers in the test.

Run with an isolated interpreter (python3 -I) and pass file paths as arguments.
"""
import glob
import gzip
import json
import os
import re
import sys
import urllib.request

API = "https://api.kolodahearthstone.com/api/v1/constructed-cards?format=wild&per_page=200&page={}"
CLASS_IDS = {1: "DEATHKNIGHT", 2: "DRUID", 3: "HUNTER", 4: "MAGE", 5: "PALADIN", 6: "PRIEST", 7: "ROGUE", 8: "SHAMAN",
             9: "WARLOCK", 10: "WARRIOR", 12: "NEUTRAL", 14: "DEMONHUNTER"}


def normalize(text):
    """Mirrors PoolText.Normalize in C#."""
    if not text:
        return ""
    t = re.sub(r"<[^>]*>", "", text)
    t = re.sub(r"\|4\(([^,)]*),([^)]*)\)", r"\2", t)
    t = t.replace("[x]", "")
    for ch in "$#@&":
        t = t.replace(ch, " ")
    t = t.replace(" ", " ").replace("\n", " ").replace("\r", " ").replace("’", "'")
    t = re.sub(r"\s+", " ", t).strip()
    return t.replace(" .", ".").replace(" ,", ",").replace(" :", ":")


def fetch(folder):
    os.makedirs(folder, exist_ok=True)
    page = 1
    while True:
        with urllib.request.urlopen(API.format(page), timeout=60) as r:
            body = json.load(r)
        with open(os.path.join(folder, f"wild_{page:03}.json"), "w", encoding="utf-8") as f:
            json.dump(body, f, ensure_ascii=False)
        if not body.get("pagination", {}).get("has_next"):
            break
        page += 1
    print(f"{page} pages -> {folder}")


def load(folder, cards_json):
    api = [c for f in sorted(glob.glob(os.path.join(folder, "wild_*.json")))
           for c in json.load(open(f, encoding="utf-8"))["data"]]
    hsj = {c["id"]: c for c in json.load(open(cards_json, encoding="utf-8"))}
    return api, hsj


def pool_card(c, hsj):
    """One card as CardPoolCatalog.ParseApiPage + WithRaces builds it."""
    std = any(f["slug"] == "standard" for f in c["formats"])
    races = [c["minion_type"]] if c["minion_type"] else []
    known = hsj.get(c["card_id"], {}).get("races") or []
    if len(known) > 1 and not all(r in races for r in known):
        races = known
    kind = c["card_type"]["slug"]
    card = {
        "Id": c["card_id"], "DbfId": c["dbf"] or 0, "Name": c["name"]["en"] or c["card_id"],
        "Text": normalize(c["text"]["en"]) if std else "", "Type": kind, "Rarity": c["rarity"] or "",
        "Cost": c["mana_cost"] or 0, "Attack": c["attack"],
        "Health": c["durability"] if kind == "WEAPON" else c["health"],
        "Classes": [CLASS_IDS[i] for i in c["multi_class"] if i in CLASS_IDS] or [c["class"] or "NEUTRAL"],
        "Races": races, "SpellSchool": c["spell_school"], "Mechanics": c.get("mechanics") or [],
        "ReferencedTags": c.get("referenced_tags") or [], "Set": c["card_set"] or "", "Standard": std,
    }
    return {k: v for k, v in card.items() if v is not None}


def fixture(folder, cards_json, out):
    api, hsj = load(folder, cards_json)
    seen, cards = set(), []
    for c in api:
        if c["card_id"] not in seen:
            seen.add(c["card_id"])
            cards.append(pool_card(c, hsj))
    with gzip.open(out, "wt", encoding="utf-8") as f:
        json.dump({"Source": "Hearthpulse API", "Updated": "2026-10-23T00:00:00Z", "Cards": cards}, f, ensure_ascii=False)
    print(f"{len(cards)} cards -> {out}")


def check(folder, cards_json):
    api, hsj = load(folder, cards_json)
    cards = [pool_card(c, hsj) for c in api]
    std = [c for c in cards if c["Standard"]]
    past = [c for c in cards if not c["Standard"]]

    def race(c, r):
        return r in c["Races"] or "ALL" in c["Races"]

    def mech(c, *tags):
        return any(t in c["Mechanics"] for t in tags)

    def mine(c, cls):  # Discover without a class: your class + neutral
        return cls in c["Classes"] or "NEUTRAL" in c["Classes"]

    def other(c, cls):  # from another class: class cards that are not yours
        return "NEUTRAL" not in c["Classes"] and cls not in c["Classes"]

    minion = lambda c: c["Type"] == "MINION"
    spell = lambda c: c["Type"] == "SPELL"
    checks = {
        "CORE_GIL_836 SHAMAN": [c for c in std if minion(c) and mech(c, "BATTLECRY") and mine(c, "SHAMAN")],
        "CORE_ONY_022 PALADIN": [c for c in std if spell(c) and c.get("SpellSchool") == "HOLY" and mine(c, "PALADIN")],
        "CORE_KAR_057 PALADIN": [c for c in std if spell(c) and mine(c, "PALADIN")],
        "CORE_EDR_001": [c for c in std if spell(c) and "MAGE" in c["Classes"]],
        "CORE_DRG_024": [c for c in std if minion(c) and race(c, "PIRATE")],
        "DINO_422": [c for c in std if minion(c) and race(c, "BEAST") and c["Cost"] == 3],
        "DINO_433": [c for c in std if minion(c) and mech(c, "TAUNT") and c["Cost"] in (2, 4, 6)],
        "CATA_484": [c for c in std if spell(c) and c["Cost"] == 1],
        "CORE_TID_931 ROGUE": [c for c in std if spell(c) and other(c, "ROGUE") and c["Cost"] >= 5],
        "CORE_KAR_069 ROGUE": [c for c in std if other(c, "ROGUE")],
        "TIME_711": [c for c in past if minion(c) and c["Cost"] == 1],
        "DINO_430": [c for c in std if minion(c) and race(c, "BEAST") and c["Rarity"] == "LEGENDARY"],
        "CATA_556": [c for c in std if minion(c) and race(c, "DRAGON") and c["Cost"] <= 3],
        "JAIL_806": [c for c in std if spell(c) and c["Cost"] >= 5],
        "TLC_824 HUNTER": [c for c in std if minion(c) and race(c, "BEAST") and (c.get("Attack") or 0) % 2 == 1 and mine(c, "HUNTER")],
        "FIR_920 ROGUE": [c for c in std if minion(c) and mech(c, "COMBO", "BATTLECRY", "STEALTH") and mine(c, "ROGUE")],
        "EDR_273 DRUID": [c for c in std if mech(c, "CHOOSE_ONE") and other(c, "DRUID")],
        "CORE_EX1_189": [c for c in std if minion(c) and c["Rarity"] == "LEGENDARY"],
        "JAIL_321": [c for c in std if spell(c) and mech(c, "SECRET") and "MAGE" in c["Classes"]],
        "TLC_814": [c for c in std if spell(c) and c.get("SpellSchool") in ("HOLY", "SHADOW")],
        "DINO_412": [c for c in std if minion(c) and (len(c["Races"]) > 1 or "ALL" in c["Races"])],
        "TIME_446 DEMONHUNTER": [c for c in std if minion(c) and race(c, "DEMON") and c["Cost"] >= 5 and mine(c, "DEMONHUNTER")],
        "CORE_LOE_039 MAGE": [c for c in std if minion(c) and race(c, "MECHANICAL") and mine(c, "MAGE")],
        "TLC_334": [c for c in std if spell(c) and c["Cost"] >= 8],
        "JAIL_878": [c for c in std if minion(c) and c["Cost"] == 1 and mech(c, "DEATHRATTLE")],
        "TIME_016": [c for c in past if minion(c) and race(c, "MECHANICAL") and "PALADIN" in c["Classes"]],
        "JAIL_706": [c for c in std if spell(c) and c["Cost"] == 4],
    }
    for name, pool in checks.items():
        print(f"{len(pool):5d}  {name}")


def main():
    args = sys.argv[1:]
    if args[:1] == ["fetch"] and len(args) == 2:
        fetch(args[1])
    elif args[:1] == ["fixture"] and len(args) == 4:
        fixture(args[1], args[2], args[3])
    elif args[:1] == ["check"] and len(args) == 3:
        check(args[1], args[2])
    else:
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main()
