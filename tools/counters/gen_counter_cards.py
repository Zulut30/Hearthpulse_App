#!/usr/bin/env python3
"""Generates src/Hearthpulse.Core/Tracking/Counters/CounterCards.g.cs.

Each in-game counter (src/Hearthpulse.Core/Tracking/Counters/Counters.cs) is shown when a player's deck or revealed
cards include a card that cares about it. This script finds those cards from the English card text of the current
patch, so the app does not depend on the player's locale at runtime. It also writes the per-card amounts some counters
add up (Sources: Invokes, Libram and Leyline reductions, Excavates...), Blood Rune symbols and the Protoss cards.

Input: HearthstoneJSON cards.json in English (https://api.hearthstonejson.com/v1/latest/enUS/cards.json).

Usage (isolated interpreter, data file passed as an argument):
  python3 -I tools/counters/gen_counter_cards.py cards.enUS.json src/Hearthpulse.Core/Tracking/Counters/CounterCards.g.cs

Patterns are written from the card text (what the card says it counts); keep the counter ids in sync with Counters.cs.
"""
import json
import re
import sys

SCHOOLS = ["Arcane", "Fel", "Fire", "Frost", "Holy", "Nature", "Shadow"]

# counter id -> regex over the normalised English text (and name where noted). "while holding this" counts are
# kept by the card itself and shown by the game, so they are excluded.
PATTERNS = {
    "cthun": r"C'Thun abilit|C'Thun started in your deck|triggered all 4 C'Thun",
    "herald": r"\bHerald\b",
    "corpses": r"\bCorpses?\b",
    "jade": r"\bJade Golem\b",
    "spells-cast": r"spells? you've cast this game|for each spell you've cast|spells you've cast(?! while)",
    "healed": r"(restored|healed|restore).{0,40}this game",
    "hero-power": r"Hero Power.{0,40}this game|used your Hero Power",
    "friendly-deaths": r"friendly minions?( that)?( have)? died this game|friendly minion died this game",
    "minions-died": r"(?<!friendly )minions?( that)?( have)? died this game",
    "armor": r"Armor.{0,20}this game|gained .{0,10}Armor this game",
    "spell-schools": r"spell schools?( you've cast)?|different spell school",
    "minion-types": r"minion types|types of minions|different type you've played",
    "elemental-streak": r"Elemental (last turn|on each of your)|played an Elemental",
    "dragons": r"Dragons? you've played|for each Dragon you've played",
    "not-from-deck": r"didn't start in your deck",
    "deathrattle-summoned": r"Deathrattle minions? you've summoned this game",
    "minion-mana": r"spent \d+ Mana on minions",
    "cards-drawn": r"cards? you've drawn this game|drawn \d+ cards this game",
    "overload": r"you've Overloaded|Overloaded.{0,30}this game",
    "totems": r"Totems? you've summoned",
    "beasts-summoned": r"Beasts? you've summoned",
    "cards-discarded": r"cards? you've discarded|cards you discarded",
    "other-class": r"(another|other) class(es)?( card)? you've played|cards? from (another|other) class(es)? you've played",
    "secrets-played": r"Secrets? you've played",
    "friendly-attacks": r"friendly character attacked",
    "cards-played": r"for each card you('ve)? played",
    "imbue": r"\bImbue",
    "fatigue": r"\bFatigue\b",
    "abyssal-curse": r"Abyssal Curse",
    "animal-companion": r"Animal Companion",
    "extra-companion": r"Animal Companion",
    "asteroid": r"\bAsteroids?\b",
    "astral-automaton": r"Astral Automaton",
    "aviana": r"lunar cycle",
    "blood-runes": r"Blood Runes? you've played",
    "captured-archmage": r"Captured Archmage",
    "cards-drawn-turn": r"drawn this turn",
    "cards-2-mana": r"for 2 Mana",
    "cards-played-turn": r"cards? (you('ve)? )?played this turn",
    "chaotic-tendril": r"Chaotic Tendril",
    "colossus": r"Protoss spells? you cast",
    "cthun-old": r"your C'Thun",
    "discovers": r"you('ve)? Discovered",
    "elwynn-boar": r"Elwynn Boar",
    "excavate": r"\bExcavate",
    "expensive-spells": r"spell you cast this game that costs \(5\) or more",
    "galakrond": r"\bGalakrond\b",
    "hero-attacks": r"hero attacks this game",
    "leyline-cost": r"\bLeylines?\b",
    "leyline-effect": r"\bLeylines?\b",
    "leyline-trigger": r"\bLeylines?\b",
    "libram": r"\bLibrams?\b",
    "minions-died-turn": r"died this turn",
    "paladin-cards": r"Paladin cards? you've played",
    "pogo-hopper": r"Pogo-Hopper",
    "protoss-discount": r"Protoss minions? cost",
    "self-damage": r"damage you've taken on your turns",
    "silver-hand": r"Silver Hand Recruits?.{0,40}this game|Silver Hand Recruits you summon",
    "spells-on-friendly": r"spells? you('ve)? cast on friendly characters",
    "spells-on-characters": r"spells? you('ve)? cast on characters",
    "dragons-summoned": r"Dragons? you've summoned",
    "timethief-rafaam": r"\bRafaams\b",
    "void-soul": r"Void Soul",
    "ysondre": r"Ysondre has died",
    "incindius": r"\bEruptions\b",
}
for school in SCHOOLS:
    PATTERNS[f"spells-{school.lower()}"] = rf"{school} spells? you've cast(?! while)|{school} spells? you've played this game"

# counter id -> regex over the card name, for cards that are the subject of a counter without saying so in their text.
NAME_PATTERNS = {
    "cthun-old": r"^C'Thun$",
    "galakrond": r"^Galakrond, ",
    "golakka": r"^Dive the Golakka Depths$",
    "leyline-cost": r"Leyline$",
    "leyline-effect": r"Leyline$",
    "leyline-trigger": r"Leyline$",
    "libram": r"^Libram of ",
    "ceaseless-expanse": r"^The Ceaseless Expanse$",
    "tyrande": r"^Tyrande$",
    "wheel-of-death": r"^Wheel of DEATH",
    "aviana": r"^Aviana, Elune's Chosen$",
    "pogo-hopper": r"^Pogo-Hopper$",
}

# counter id -> regex for what a card adds to the count; group "n" is the amount (1 without it), "twice" doubles it.
# Whether it counts when the card is played or when it dies follows the keyword before it (Battlecry, Deathrattle...).
SOURCE_PATTERNS = {
    "abyssal-curse": r"Give your opponent an Abyssal Curse",
    "animal-companion": r"Replace your future Animal Companions with random Beasts that cost \((?P<n>\d+)\) more",
    "extra-companion": r"summon Animal Companions summon (?P<n>\d+) more",
    "asteroid": r"Your Asteroids deal (?P<n>\d+) more damage",
    "excavate": r"Excavate a treasure",
    "galakrond": r"Invoke Galakrond(?P<twice> twice)?",
    "leyline-cost": r"Your Leylines cost \((?P<n>\d+)\) less",
    "leyline-effect": r"Increase the effects of your Leylines by (?P<n>\d+)",
    "leyline-trigger": r"Your Leylines trigger an additional time",
    "libram": r"Reduce the Cost of your Librams by \((?P<n>\d+)\)",
    "protoss-discount": r"Protoss minions cost \((?P<n>\d+)\) less",
}
KEYWORDS = r"(?:Battlecry|Deathrattle|Quickdraw|Spellburst|Combo|Outcast|Infuse \(\d+\)|Manathirst \(\d+\)|Corrupt)"
LABEL = re.compile(rf"({KEYWORDS}(?:,? (?:and )?{KEYWORDS})*):")

# StarCraft mini-set factions are by class; Protoss cards belong to these.
PROTOSS_CLASSES = {"DRUID", "MAGE", "PRIEST", "ROGUE"}

EXCLUDED_SETS = {"BATTLEGROUNDS", "LETTUCE", "HERO_SKINS", "TB", "PLACEHOLDER_202204", "TUTORIAL", "MISSIONS", "CREDITS"}


def normalize(text):
    text = (text or "").split("@")[0]
    text = re.sub(r"<[^>]+>", "", text)
    text = text.replace("[x]", "").replace("_", " ").replace("\n", " ").replace("$", "").replace("#", "")
    return re.sub(r"\s+", " ", text).strip()


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    cards = json.load(open(sys.argv[1], encoding="utf-8"))
    pool = [c for c in cards if c.get("collectible") and c.get("set") not in EXCLUDED_SETS]
    keys = list(PATTERNS) + [k for k in NAME_PATTERNS if k not in PATTERNS]
    related = {key: [] for key in keys}
    imbue_sources = {}
    sources = {key: {} for key in SOURCE_PATTERNS}
    blood_runes = {}
    for card in pool:
        text = normalize(card.get("text"))
        for key, pattern in PATTERNS.items():
            if re.search(pattern, text, re.I):
                related[key].append(card["id"])
        for key, pattern in NAME_PATTERNS.items():
            if re.search(pattern, card.get("name", "")):
                related[key].append(card["id"])
        for key, pattern in SOURCE_PATTERNS.items():
            play = death = 0
            for m in re.finditer(pattern, text):
                n = int(m.groupdict().get("n") or 1) * (2 if m.groupdict().get("twice") else 1)
                labels = LABEL.findall(text[: m.start()])
                label = labels[-1] if labels else ""
                if "Deathrattle" in label:
                    death += n
                if not label or "Battlecry" in label or "Quickdraw" in label or "Combo" in label or "Outcast" in label:
                    play += n
            if play or death:
                sources[key][card["id"]] = (play, death)
        if (card.get("runeCost") or {}).get("blood"):
            blood_runes[card["id"]] = card["runeCost"]["blood"]
        # Cards that Imbue on their own: "Imbue your Hero Power" (twice when the text says so).
        if re.search(r"Imbue your Hero Power", text):
            imbue_sources[card["id"]] = 2 if re.search(r"Imbue your Hero Power twice", text) else 1
        if "JADE_GOLEM" in card.get("mechanics", []) and card["id"] not in related["jade"]:
            related["jade"].append(card["id"])
    # Protoss cards, tokens included: a Protoss spell made by another card still improves Colossus.
    protoss = sorted(
        c["id"] for c in cards
        if c.get("set") == "SPACE" and c["id"].startswith("SC_") and c.get("type") in ("MINION", "SPELL", "LOCATION", "HERO")
        and PROTOSS_CLASSES & set(c.get("classes") or [c.get("cardClass")])
    )

    lines = [
        "// <auto-generated>",
        "// Generated by tools/counters/gen_counter_cards.py from HearthstoneJSON enUS cards.json; do not edit by hand.",
        "// </auto-generated>",
        "",
        "namespace Hearthpulse.Core.Tracking.Counters;",
        "",
        "internal static class CounterCards",
        "{",
        "    /// <summary>Per counter id, the collectible cards whose text cares about that count.</summary>",
        "    internal static readonly IReadOnlyDictionary<string, string[]> Related = new Dictionary<string, string[]>(StringComparer.Ordinal)",
        "    {",
    ]
    for key in keys:
        ids = ", ".join(f'"{i}"' for i in sorted(set(related[key])))
        lines.append(f'        ["{key}"] = [{ids}],')
    lines += [
        "    };",
        "",
        "    /// <summary>Cards that Imbue the Hero Power when played, and how many times.</summary>",
        "    internal static readonly IReadOnlyDictionary<string, int> ImbueSources = new Dictionary<string, int>(StringComparer.Ordinal)",
        "    {",
    ]
    for card_id, times in sorted(imbue_sources.items()):
        lines.append(f'        ["{card_id}"] = {times},')
    lines += [
        "    };",
        "",
        "    /// <summary>Per counter id, what a card adds to the count when played and when it dies or is destroyed.</summary>",
        "    internal static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, (int Play, int Death)>> Sources =",
        "        new Dictionary<string, IReadOnlyDictionary<string, (int Play, int Death)>>(StringComparer.Ordinal)",
        "        {",
    ]
    for key in SOURCE_PATTERNS:
        entries = ", ".join(f'["{i}"] = ({p}, {d})' for i, (p, d) in sorted(sources[key].items()))
        lines.append(f'            ["{key}"] = new Dictionary<string, (int, int)>(StringComparer.Ordinal) {{ {entries} }},')
    lines += [
        "        };",
        "",
        "    /// <summary>Blood Rune symbols on each card (Death Knight rune cost).</summary>",
        "    internal static readonly IReadOnlyDictionary<string, int> BloodRunes = new Dictionary<string, int>(StringComparer.Ordinal)",
        "    {",
    ]
    for card_id, runes in sorted(blood_runes.items()):
        lines.append(f'        ["{card_id}"] = {runes},')
    lines += [
        "    };",
        "",
        "    /// <summary>Protoss cards of the StarCraft mini-set (tokens included).</summary>",
        "    internal static readonly IReadOnlySet<string> Protoss = new HashSet<string>(StringComparer.Ordinal)",
        "    {",
        "        " + ", ".join(f'"{i}"' for i in protoss),
        "    };",
        "}",
        "",
    ]
    with open(sys.argv[2], "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(lines))
    for key in keys:
        print(f"{key}: {len(set(related[key]))}")
    print(f"imbue sources: {len(imbue_sources)}")
    for key in SOURCE_PATTERNS:
        print(f"source {key}: {sorted(sources[key].items())}")
    print(f"blood runes: {len(blood_runes)}, protoss: {len(protoss)}")


if __name__ == "__main__":
    main()
