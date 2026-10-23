#!/usr/bin/env python3
"""Generates src/Hearthlens.Core/Battlegrounds/Effects/CombatRelevance.g.cs.

The table says, per Battlegrounds minion (normal and golden card id), whether its card text changes a
fight after the boards are captured at the first attack. The overlay's "N minion effects not simulated"
note counts minions that are combat-relevant and have no registered effect implementation.

Inputs (research data, labelled by hand from the card text of the current patch):
  bg_pool_effects.json    302 pool minions: id, golden_id, name, category, combat_relevant (y/conditional/
                          resolved/simulated/n)
  bg_tokens.csv           tokens and summoned non-pool minions (optional)
  bg_timewarped_auto.csv  Timewarped minions, classified here with the text heuristic below (optional)
  carddefs_parsed.json    all cards of the patch, used only for golden ids of tokens/Timewarped (optional)

Usage (run with an isolated interpreter, data files are passed as arguments):
  python3 -I tools/effects/gen_combat_relevance.py <bg_pool_effects.json> <output.g.cs> \
      [--tokens bg_tokens.csv] [--timewarped bg_timewarped_auto.csv] [--carddefs carddefs_parsed.json]

The text heuristic must stay in sync with CombatRelevance.TextSuggestsCombatEffect (C#), which is the
runtime fallback for cards a new patch adds before this table is regenerated.
"""
import argparse
import csv
import json
import re
import sys

COMBAT_VERB = (r"(Summon|summon|Give|give|Gain|gain|Deal|deal|Destroy|destroy|Remove|remove|Cast|cast|"
               r"plays? \d* ?(a )?Blood Gem|Reborn|Divine Shield|Attack|have \+|Blood \|4)")
NOT_COMBAT = (r"^(Get|get|Add|You and your teammate|Your teammate|Reduce|Your Tavern spells|Gain \d+ free|"
              r"Gain .*Refresh|Give minions in the Tavern|After the Tavern)")


def normalize(text):
    text = (text or "").split("@")[0]
    text = re.sub(r"<[^>]+>", "", text)
    text = text.replace("[x]", "").replace("_", " ").replace("\n", " ")
    return re.sub(r"\s+", " ", text).strip()


def clause(text, key):
    m = re.search(key + r"(?: \(\d+\))?: (.*?)(?=(?:Deathrattle|Rally|Avenge|Battlecry)[^:]*:|$)", text)
    return m.group(1).strip() if m else None


def needs_sim(raw):
    """Text heuristic from research/scripts/heuristic_eval.py, plus a few patterns for damage, kills and deaths."""
    t = normalize(raw)
    reasons = []
    for key in ("Deathrattle", "Rally", "Avenge"):
        c = clause(t, key)
        if c and re.search(COMBAT_VERB, c) and not re.match(NOT_COMBAT, c):
            reasons.append(key)
    if re.search(r"(friendly|another|other)[^.]*\battacks\b|After this attacks|Whenever this attacks|is attacked", t):
        reasons.append("on_attack")
    if re.search(r"Also damages|excess damage", t):
        reasons.append("cleave")
    if re.search(r"(Whenever|After) you summon|summon a (Beast|minion) in combat", t):
        reasons.append("on_summon")
    if re.search(r"is Reborn|deals damage, gain|that died this game|Deathrattle you've triggered|Deathrattles trigger|"
                 r"while this has Divine Shield|Once this reaches", t):
        reasons.append("dynamic")
    if re.search(r"rest of this combat|When you have space|attack immediately", t):
        reasons.append("persistent_soc")
    # Additions (not in the research heuristic): damage taken, kills and friendly deaths during combat.
    if re.search(r"(Whenever|After) this takes damage|(Whenever|After) this kills|attacks and kills|"
                 r"(Whenever|After) a friendly [A-Za-z ]*dies|loses Divine Shield", t):
        reasons.append("combat_trigger")
    return reasons


RELEVANCE = {
    "y": "Relevant",
    "conditional": "Conditional",
    "resolved": "Resolved",
    "simulated": "Keywords",
    "n": "None",
}

KEYWORD_MECHANICS = {"TAUNT", "DIVINE_SHIELD", "POISONOUS", "VENOMOUS", "REBORN", "WINDFURY", "MEGA_WINDFURY", "STEALTH"}
EFFECT_MECHANICS = {"DEATHRATTLE", "TRIGGER_VISUAL", "AURA", "AVENGE", "BACON_RALLY", "BACON_OLD_GOD", "OVERKILL"}


def cs(s):
    return '"' + (s or "").replace("\\", "\\\\").replace('"', '\\"') + '"'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pool")
    ap.add_argument("output")
    ap.add_argument("--tokens")
    ap.add_argument("--timewarped")
    ap.add_argument("--carddefs")
    args = ap.parse_args()

    pool = json.load(open(args.pool, encoding="utf-8"))
    build = pool.get("build", {})
    source = pool.get("source", "")
    m = re.search(r"patch ([\d.]+)", source)
    patch = m.group(1) if m else build.get("CardDefs.Bacon.xml") or next(iter(build.values()), "")

    golden_of = {}
    english = {}
    if args.carddefs:
        cards = json.load(open(args.carddefs, encoding="utf-8"))["cards"]
        by_dbf = {c["dbfId"]: c for c in cards}
        for c in cards:
            up = c["tags"].get("BACON_TRIPLE_UPGRADE_MINION_ID")
            if up in by_dbf:
                golden_of[c["id"]] = by_dbf[up]["id"]
            english[c["id"]] = c.get("text") or ""

    entries = []  # (normal, golden, relevance, category, name, origin)
    seen = set()

    def add(normal, golden, relevance, category, name, origin):
        if normal in seen or (golden and golden in seen):
            return
        seen.add(normal)
        if golden:
            seen.add(golden)
        entries.append((normal, golden or "", relevance, category, name, origin))

    for r in pool["minions"]:
        add(r["id"], r["golden_id"], RELEVANCE[r["combat_relevant"]], r["category"], r["name"], "pool")

    if args.tokens:
        for r in csv.DictReader(open(args.tokens, encoding="utf-8")):
            mechanics = set(filter(None, r["mechanics"].split("|")))
            if mechanics & EFFECT_MECHANICS:
                relevance = "Relevant"
            elif mechanics & KEYWORD_MECHANICS:
                relevance = "Keywords"
            else:
                relevance = "None"
            add(r["id"], golden_of.get(r["id"], ""), relevance, "TOKEN", r["name"], "token")

    if args.timewarped:
        for r in csv.DictReader(open(args.timewarped, encoding="utf-8")):
            text = english.get(r["id"]) or r["text"]
            relevance = "Relevant" if needs_sim(text) else "None"
            add(r["id"], golden_of.get(r["id"], ""), relevance, "TIMEWARPED_" + r["auto_category"], r["name"], "heuristic")

    # Heuristic quality on the hand-labelled pool, printed for the record.
    tp = fp = fn = 0
    for r in pool["minions"]:
        pred = bool(needs_sim(r["text"]))
        truth = r["combat_relevant"] == "y"
        tp += pred and truth
        fp += pred and not truth
        fn += truth and not pred
    print(f"heuristic on pool: precision={tp / max(1, tp + fp):.2f} recall={tp / max(1, tp + fn):.2f} (tp={tp} fp={fp} fn={fn})",
          file=sys.stderr)

    lines = [
        "// <auto-generated>",
        "// Generated by tools/effects/gen_combat_relevance.py; do not edit by hand. Regenerate after a patch:",
        "//   python3 -I tools/effects/gen_combat_relevance.py bg_pool_effects.json \\",
        "//       src/Hearthlens.Core/Battlegrounds/Effects/CombatRelevance.g.cs \\",
        "//       --tokens bg_tokens.csv --timewarped bg_timewarped_auto.csv --carddefs carddefs_parsed.json",
        f"// Source: {source}",
        "// Pool rows are labelled by hand from card text; Timewarped rows come from the text heuristic.",
        "// </auto-generated>",
        "",
        "namespace Hearthlens.Core.Battlegrounds.Effects;",
        "",
        "internal static partial class CombatRelevanceTable",
        "{",
        f"    internal const string Patch = {cs(patch)};",
        "",
        "    internal static readonly CardRelevance[] Entries =",
        "    [",
    ]
    for normal, golden, relevance, category, name, origin in entries:
        lines.append(f"        new({cs(normal)}, {cs(golden)}, Relevance.{relevance}, {cs(category)}, {cs(name)}, Origin.{origin.capitalize()}),")
    lines += ["    ];", "}", ""]
    with open(args.output, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    counts = {}
    for e in entries:
        counts[e[2]] = counts.get(e[2], 0) + 1
    print(f"wrote {len(entries)} cards ({counts}) to {args.output}", file=sys.stderr)


if __name__ == "__main__":
    main()
