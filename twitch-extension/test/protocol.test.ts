import { describe, expect, it } from 'vitest';
import { makeEnvelope, gzipBase64 } from '../src/dev/encode';
import { decodeEnvelope, EnvelopeError, parseEnvelope, readState } from '../src/protocol';

const deckState = {
  mode: 'constructed',
  deck: {
    name: 'Pirate Warrior',
    hero: 'HERO_01',
    format: 'standard',
    left: 22,
    cards: [
      { id: 'CORE_CS2_106', dbf: 69, cost: 1, name: 'Fiery War Axe', left: 1, total: 2 },
      { id: 'EX1_116', dbf: 559, cost: 5, name: 'Leeroy Jenkins', left: 0, total: 1 },
    ],
  },
  game: { turn: 5 },
};

describe('parseEnvelope', () => {
  it('reads the contract envelope from a PubSub string', () => {
    const env = parseEnvelope('{ "v": 1, "seq": 42, "at": 1762500000000, "z": "H4sI" }');
    expect(env).toEqual({ v: 1, seq: 42, at: 1762500000000, z: 'H4sI' });
  });

  it('accepts an already parsed snapshot body', () => {
    expect(parseEnvelope({ v: 1, seq: 0, at: 1, z: 'x' }).seq).toBe(0);
  });

  it.each([
    ['not JSON', 'nope'],
    ['an array', '[1]'],
    ['another version', '{"v":2,"seq":1,"at":1,"z":"x"}'],
    ['a negative seq', '{"v":1,"seq":-1,"at":1,"z":"x"}'],
    ['a fractional seq', '{"v":1,"seq":1.5,"at":1,"z":"x"}'],
    ['a string seq', '{"v":1,"seq":"1","at":1,"z":"x"}'],
    ['no at', '{"v":1,"seq":1,"z":"x"}'],
    ['an empty payload', '{"v":1,"seq":1,"at":1,"z":""}'],
    ['a huge string', 'x'.repeat(20000)],
  ])('rejects %s', (_name, raw) => {
    expect(() => parseEnvelope(raw)).toThrow(EnvelopeError);
  });
});

describe('decodeEnvelope', () => {
  it('unpacks gzip + base64 into the deck', async () => {
    const frame = await decodeEnvelope(await makeEnvelope(42, deckState, 1762500000000));
    expect(frame.seq).toBe(42);
    expect(frame.at).toBe(1762500000000);
    expect(frame.state).toEqual({
      mode: 'constructed',
      deck: {
        name: 'Pirate Warrior',
        hero: 'HERO_01',
        format: 'standard',
        left: 22,
        cards: [
          { id: 'CORE_CS2_106', cost: 1, name: 'Fiery War Axe', left: 1, total: 2 },
          { id: 'EX1_116', cost: 5, name: 'Leeroy Jenkins', left: 0, total: 1 },
        ],
      },
    });
  });

  it('reads mode none', async () => {
    const frame = await decodeEnvelope(await makeEnvelope(7, { mode: 'none' }));
    expect(frame.state).toEqual({ mode: 'none' });
  });

  it('keeps Cyrillic names intact', async () => {
    const card = { ...deckState.deck.cards[0], name: 'Огненный боевой топор' };
    const state = { ...deckState, deck: { ...deckState.deck, name: 'Пират-воин', cards: [card] } };
    const frame = await decodeEnvelope(await makeEnvelope(1, state));
    expect(frame.state.mode === 'constructed' && frame.state.deck.cards[0].name).toBe('Огненный боевой топор');
    expect(frame.state.mode === 'constructed' && frame.state.deck.name).toBe('Пират-воин');
  });

  it('rejects a payload that is not base64', async () => {
    await expect(decodeEnvelope('{"v":1,"seq":1,"at":1,"z":"***"}')).rejects.toThrow(EnvelopeError);
  });

  it('rejects base64 that is not gzip', async () => {
    const raw = JSON.stringify({ v: 1, seq: 1, at: 1, z: btoa('plain text') });
    await expect(decodeEnvelope(raw)).rejects.toThrow(EnvelopeError);
  });

  it('rejects gzip that is not JSON', async () => {
    const raw = JSON.stringify({ v: 1, seq: 1, at: 1, z: await gzipBase64('{oops') });
    await expect(decodeEnvelope(raw)).rejects.toThrow(EnvelopeError);
  });

  it('stops a payload that inflates past the cap', async () => {
    const z = await gzipBase64(JSON.stringify({ mode: 'none', pad: ' '.repeat(200_000) }));
    await expect(decodeEnvelope(JSON.stringify({ v: 1, seq: 1, at: 1, z }))).rejects.toThrow('state too large');
  });
});

describe('readState', () => {
  it('drops cards with an id that is not a card id (it goes into an image URL)', () => {
    const bad = { id: '../x?y', cost: 1, name: 'a', left: 1, total: 1 };
    const state = readState({ mode: 'constructed', deck: { ...deckState.deck, cards: [bad, deckState.deck.cards[0]] } });
    expect(state.mode === 'constructed' && state.deck.cards.map((c) => c.id)).toEqual(['CORE_CS2_106']);
  });

  it('drops cards with bad counts and keeps a missing name as the id', () => {
    const state = readState({
      mode: 'constructed',
      deck: { cards: [{ id: 'A', cost: -1, left: 1, total: 1 }, { id: 'B', cost: 2, left: 1, total: 1 }] },
    });
    expect(state).toEqual({
      mode: 'constructed',
      deck: { name: null, hero: null, format: 'other', left: 1, cards: [{ id: 'B', cost: 2, name: 'B', left: 1, total: 1 }] },
    });
  });

  it('keeps only the deck: no game, hand or opponent fields pass through (AC9)', () => {
    const state = readState({ ...deckState, hand: ['X'], opponent: { cards: ['Y'] }, deck: { ...deckState.deck, order: ['Z'] } });
    expect(Object.keys(state)).toEqual(['mode', 'deck']);
    expect(state.mode === 'constructed' && Object.keys(state.deck)).toEqual(['name', 'hero', 'format', 'left', 'cards']);
  });

  it('rejects an unknown mode', () => {
    expect(() => readState({ mode: 'battlegrounds' })).toThrow(EnvelopeError);
  });
});
