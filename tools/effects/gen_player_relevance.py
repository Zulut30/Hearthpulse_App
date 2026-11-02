#!/usr/bin/env python3
"""Generates src/Hearthpulse.Core/Battlegrounds/Effects/PlayerCardRelevance.g.cs.

Every Battlegrounds player-level card the simulator may get as an extra (hero powers, trinkets with their golden
versions, quest rewards, anomalies) gets a row: does its text act in a fight after the boards are captured? The hand
table PlayerCardTable (PlayerCardRelevance.cs) wins where it has the card; the rest is labelled here from the English
card text. With every known card in a table, a card missing from both is newer than the data and the simulator counts
it as "not simulated" (it cannot know) instead of silently as simulated.

Labels, per sentence of the text:
  Relevant  - a combat trigger (a friendly minion attacks, dies, takes damage, kills, loses a keyword, is summoned or
              Reborn; Avenge; "each combat", "in combat"; an aura "your minions have +X") with an effect that changes
              the fight (give, summon, deal, destroy, trigger, play a Blood Gem, a keyword); not a reward for the Tavern
              (get, Discover, Gold, a Refresh) or a stat for later ("your Blood Gems give an extra...").
  Resolved  - only a Start of Combat effect, done before the boards are captured.
  None      - Tavern only.

Prints how the text labels compare with the hand table, and the trinkets the text calls Relevant that the hand
research (meant to hold every combat trinket) misses.

Usage (cards.enUS.json from HearthstoneJSON, e.g. the app's cache folder):
  python3 -I tools/effects/gen_player_relevance.py <cards.enUS.json> src/Hearthpulse.Core/Battlegrounds/Effects/PlayerCardRelevance.g.cs
"""
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from gen_combat_relevance import cs, normalize  # noqa: E402

HAND = pathlib.Path(__file__).resolve().parents[2] / 'src/Hearthpulse.Core/Battlegrounds/Effects/PlayerCardRelevance.cs'
TYPES = {'HERO_POWER': 'HP', 'BATTLEGROUND_TRINKET': 'TR', 'BATTLEGROUND_QUEST_REWARD': 'QR', 'BATTLEGROUND_ANOMALY': 'AN'}

TRIGGER = re.compile(
    r"\b(Whenever|After|When)\b[^,.:]*\b(attacks?|dies|die|takes damage|kills|loses|is Reborn|summons?|"
    r"trigger a (Deathrattle|Rally)|awakens|space)\b|\bAvenge \(\d+\)|\b(each|in|this|during) combat\b|"
    r"^Your (minions|[A-Z][a-z]+s) have \+|^Your first Deathrattle|\bYour [A-Za-z' ]*Deathrattles\b|^Give .* during combat",
    re.IGNORECASE)
EFFECT = re.compile(
    r"\b(give|gives|gain \+|gains \+|double|summon|resummon|deal|deals|destroy|trigger|triggers|"
    r"plays? (a )?(permanent )?Blood Gem|have \+|attack immediately|an extra time|target|Divine Shield|Reborn)\b", re.IGNORECASE)
REWARD = re.compile(r"\b(get|gets|Discover|Gold|Refresh|replace this|keep|Blood Gems give|Tavern spells give)\b", re.IGNORECASE)
START_OF_COMBAT = re.compile(r"^Start of Combat:\s*", re.IGNORECASE)


def sentences(text):
    """The text's sentences, without the "(Improved by...)" and "(N left!)" notes."""
    notes = r"\((\d+ left!|Improved by[^)]*|[^)]*times per combat\.?|[^)]*\bgame!?)\)"
    return [s.strip() for s in re.split(r"(?<=[.!])\s+", re.sub(notes, "", text)) if s.strip()]


def acts_in_combat(sentence):
    if not TRIGGER.search(sentence):
        return False
    effect = re.split(r"[,:]", sentence, maxsplit=1)[-1] if re.search(r"[,:]", sentence) else sentence
    return bool(EFFECT.search(effect)) and not REWARD.search(effect)


def label(card):
    text = normalize(card.get('text', ''))
    # a sentence about Start of Combat effects acts before the boards are captured
    after_capture = [s for s in sentences(text) if 'Start of Combat' not in s]
    if any(acts_in_combat(s) for s in after_capture):
        return 'Relevant'
    if START_OF_COMBAT.search(text) or 'Start of Combat' in text:
        return 'Resolved'
    return 'None'


def hand_rows():
    rows = {}
    for kind, cid in re.findall(r'(Relevant|Tavern)\("([^"]+)"', HAND.read_text(encoding='utf-8')):
        rows[cid] = 'Relevant' if kind == 'Relevant' else 'None'
    return rows


def player_cards(cards):
    for card in cards:
        kind = TYPES.get(card.get('type'))
        if kind and (card.get('set') == 'BATTLEGROUNDS' or kind != 'HP'):
            yield kind, card


def main():
    cards = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
    hand = hand_rows()
    rows, disagree, agree = [], [], 0
    for kind, card in sorted(player_cards(cards), key=lambda kc: kc[1]['id']):
        cid = card['id']
        guess = label(card)
        if cid in hand:
            if hand[cid] == guess:
                agree += 1
            else:
                disagree.append((cid, card.get('name', ''), f'hand {hand[cid]}, text {guess}'))
            continue
        if kind == 'TR' and guess == 'Relevant':
            disagree.append((cid, card.get('name', ''), 'text Relevant, not in the hand table'))
        rows.append((cid, guess, f'{kind}_{guess.upper()}', card.get('name', '')))
    out = ['// <auto-generated> tools/effects/gen_player_relevance.py from HearthstoneJSON cards.enUS.json; do not edit.',
           '// Player-level cards the hand table (PlayerCardRelevance.cs) does not hold, labelled from their English text.',
           'namespace Hearthpulse.Core.Battlegrounds.Effects;', '',
           'internal static class PlayerCardGenerated', '{',
           '    internal static readonly CardRelevance[] Entries =', '    [']
    for cid, rel, cat, name in rows:
        out.append(f'        new({cs(cid)}, "", Relevance.{rel}, {cs(cat)}, {cs(name)}, Origin.Player),')
    out += ['    ];', '}', '']
    pathlib.Path(sys.argv[2]).write_bytes('\r\n'.join(out).encode('utf-8'))
    counts = {}
    for _, _, cat, _ in rows:
        counts[cat] = counts.get(cat, 0) + 1
    print(f'{len(rows)} generated rows; by kind: {dict(sorted(counts.items()))}')
    print(f'hand table: {len(hand)} rows, {agree} labelled the same from the text')
    print(f'{len(disagree)} differences:')
    for cid, name, why in disagree:
        print(f'  {cid:32} {name:32} {why}')


if __name__ == '__main__':
    main()
