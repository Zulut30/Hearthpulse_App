import assert from 'node:assert/strict';
import test from 'node:test';
import { record, start, end, choice, jsonl } from './capture-fixture.mjs';
import { validRecord, records, inflate, MiB } from '../node/validation.mjs';
import { gzipSync } from 'node:zlib';

test('typed source allows every declared record family and preserves choice copies and ordering', () => {
  const examples = [start(), end(), choice(),
    record(1, 'entity', { zone: 'HAND', position: 1, tags: { COST: 3, ATK: 2 } }, { side: 'local', entity: 1, card: 'CARD_A' }),
    record(1, 'entity', { zone: 'HAND', position: 1, tags: { ZONE_POSITION: 1 } }, { side: 'opponent', entity: 2 }),
    record(1, 'tag', { tag: 'TEMP_RESOURCES', value: -1 }, { side: 'local' }),
    record(1, 'block_start', { block: 'ATTACK' }, { side: 'opponent', entity: 3, target: 4, card: 'CARD_A' }),
    record(2, 'block_end', { block: 'ATTACK' }),
    record(1, 'action', { action: 'buy', cost: 3 }, { side: 'local', card: 'CARD_A' }),
    record(1, 'snapshot', { gold: 10, tier: 6, cards: [{ entity: 1, card: 'CARD_A', side: 'shop', zone: 'SHOP', position: 1, tags: { HEALTH: 20 } }] }),
    record(1, 'draft_choice', { pick: 1, options: [{ card: 'CARD_A', package: ['CARD_B', 'CARD_B'] }], picked: 0, underground: false }, { side: 'local' }),
    record(1, 'gap', { reason: 'unsupported', count: 1 }),
  ];
  assert.ok(examples.every(validRecord));
  const duplicates = choice();
  duplicates.data.options = ['A', 'B', 'A']; duplicates.data.picked = ['A', 'A'];
  assert.deepEqual(records(jsonl([duplicates]))[0].data.options, ['A', 'B', 'A']);
  assert.deepEqual(records(jsonl([duplicates]))[0].data.picked, ['A', 'A']);
});

test('record reference turn sequence card and choice array limits include exact boundaries', () => {
  const valid = { ...choice(), seq: 10000000, turn: 1000, entity: 1000000, target: 1000000, card: 'A'.repeat(80) };
  valid.data.options = Array(64).fill('A'); valid.data.picked = Array(16).fill('A');
  assert.equal(validRecord(valid), true);
  for (const change of [{ seq: 10000001 }, { seq: -1 }, { turn: 1001 }, { turn: 0.5 }, { entity: 1000001 }, { target: -1 }, { card: 'A'.repeat(81) }, { card: 'C:/private' }, { side: 'spectator' }])
    assert.equal(validRecord({ ...valid, ...change }), false);
  assert.equal(validRecord({ ...valid, data: { ...valid.data, options: Array(65).fill('A') } }), false);
  assert.equal(validRecord({ ...valid, data: { ...valid.data, picked: Array(17).fill('A') } }), false);
  assert.equal(validRecord({ ...valid, data: { ...valid.data, picked: ['B'] } }), false);
});

test('snapshot and draft option bounds reject unsafe nested content and extra keys', () => {
  const card = { entity: 1, card: 'A', side: 'local', zone: 'PLAY', position: 0, tags: { ATK: 1 } };
  const snapshot = record(1, 'snapshot', { gold: null, tier: null, cards: Array(256).fill(card) });
  assert.equal(validRecord(snapshot), true);
  assert.equal(validRecord({ ...snapshot, data: { ...snapshot.data, cards: Array(257).fill(card) } }), false);
  for (const extra of [{ ...card, name: 'SyntheticPrivateMarker' }, { ...card, tags: { ...card.tags, RAW: 1 } }, { ...card, tags: { ATK: 'SyntheticPrivateMarker' } }])
    assert.equal(validRecord({ ...snapshot, data: { ...snapshot.data, cards: [extra] } }), false);
  const draft = record(1, 'draft_choice', { pick: 0, options: Array(64).fill({ card: 'A', package: Array(64).fill('B') }), picked: 63, underground: null }, { side: 'local' });
  assert.equal(validRecord(draft), true);
  assert.equal(validRecord({ ...draft, data: { ...draft.data, picked: 64 } }), false);
  assert.equal(validRecord({ ...draft, data: { ...draft.data, options: [] } }), false);
  assert.equal(validRecord(null), false);
  assert.equal(validRecord([]), false);
});

test('decompression accepts the exact four MiB bound and rejects the next byte and malformed gzip', async () => {
  const bytes = Buffer.alloc(4 * MiB, 65);
  assert.equal((await inflate(gzipSync(bytes))).length, 4 * MiB);
  await assert.rejects(inflate(gzipSync(Buffer.alloc(4 * MiB + 1))), e => e.status === 413);
  await assert.rejects(inflate(Buffer.from('not gzip')), e => e.status === 400);
  await assert.rejects(inflate(Buffer.concat([gzipSync(bytes), gzipSync(Buffer.from('B'))])), e => e.status === 413);
});
