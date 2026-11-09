// Explicit integration scope: node --test server/pool/test/real-worker.integration.mjs
// Requires the REAL published/built P3 worker; no fallback or substituted test parser.
import assert from 'node:assert/strict';
import test from 'node:test';
import * as fs from 'node:fs';
import { ID, setup, start, record, choice, end, jsonl, chunk, manifest } from './capture-fixture.mjs';
import { identity, sha } from '../node/validation.mjs';

const executable = process.env.POOL_TEST_WORKER_EXECUTABLE;
const args = process.env.POOL_TEST_WORKER_ARGS;
if (!executable || !args) throw new Error('Configure real P3 POOL_TEST_WORKER_EXECUTABLE and POOL_TEST_WORKER_ARGS');
const worker = { executable, args: JSON.parse(args) };

test('real dotnet worker preserves semantic facts replaces deterministically and cannot survive generation deletion', async t => {
  const f = await setup(t, { operatorToken: 'synthetic-operator', archive: { worker } });
  const local = { side: 'local', turn: 2 };
  const bytes = jsonl([
    start(), record(1, 'tag', { tag: 'TURN', value: 2 }, { turn: 2 }), choice(2),
    record(3, 'action', { action: 'buy', cost: 3 }, { ...local, card: 'CARD_A' }),
    record(4, 'action', { action: 'sell', cost: null }, { ...local, card: 'CARD_B' }),
    record(5, 'action', { action: 'play', cost: 1 }, { ...local, card: 'CARD_C' }),
    record(6, 'snapshot', { gold: 7, tier: 2, cards: [{ entity: 7, card: 'SHOP_CARD', side: 'shop', zone: 'SHOP', position: 1, tags: { ATK: 1, HEALTH: 1 } }] }, { turn: 2 }),
    record(7, 'tag', { tag: 'RESOURCES', value: 7 }, local), end(8),
  ]);
  const c = chunk(bytes);
  assert.equal((await f.post('chunks', c)).status, 201);
  assert.equal((await f.post('complete', manifest([c]))).status, 202);
  await f.pool.archive.tick();
  const row = f.pool.archive.row(identity(ID).key);
  assert.equal(row.status, 'parsed');
  const first = row.derived;
  const result = JSON.parse(first);
  assert.equal(result.sourceSha256, sha(bytes));
  assert.deepEqual(result.events.map(e => e.kind), ['start', 'turn', 'choice', 'buy', 'sell', 'play', 'shop', 'resources', 'end']);
  assert.deepEqual(result.events[2].data.options, ['A', 'B', 'C']);
  assert.deepEqual(result.events[2].data.picked, ['B']);
  for (const key of ['plays', 'buys', 'sells', 'choices', 'shopSnapshots', 'resources']) assert.equal(result.summary[key], 1, key);
  assert.equal(result.summary.completeForAggregation, true);
  assert.equal((await f.post('reprocess', ID, { authorization: 'Bearer synthetic-operator' })).status, 202);
  await f.pool.archive.tick();
  assert.equal(f.pool.archive.row(identity(ID).key).derived, first);
  await f.restart();
  assert.equal((await (await f.post('status', ID)).json()).status, 'parsed');
  await f.post('reprocess', ID, { authorization: 'Bearer synthetic-operator' });
  assert.equal((await f.post('forget', { v: 1, iid: ID.iid, generation: ID.generation })).status, 200);
  assert.equal(await f.pool.archive.tick(), false);
  assert.equal((await f.post('chunks', c)).status, 410);
  assert.equal(f.pool.store.sqlite.prepare('SELECT COUNT(*) AS n FROM source_captures').get().n, 0);
  assert.deepEqual(fs.readdirSync(f.pool.archive.captureRoot), []);
});

test('real dotnet draft terminal gap stays partial preserves packages and invents no run result', async t => {
  const f = await setup(t, { archive: { worker } });
  const bytes = jsonl([
    start({ captureKind: 'draft', mode: 'arena' }),
    record(1, 'draft_choice', { pick: 0, options: [{ card: 'A', package: ['B', 'B'] }, { card: 'C', package: [] }], picked: 1, underground: null }, { side: 'local', turn: 1 }),
    record(2, 'gap', { reason: 'unsupported', count: 2 }, { turn: 1 }),
  ]);
  assert.equal((await f.complete(bytes, { captureKind: 'draft', mode: 'arena', captureStatus: 'incomplete', gaps: 2 })).status, 202);
  await f.pool.archive.tick();
  const row = f.pool.archive.row(identity(ID).key);
  assert.equal(row.status, 'partial');
  const result = JSON.parse(row.derived);
  assert.equal(result.gaps, 2);
  assert.equal(result.unsupported, 1);
  assert.equal(result.summary.draftChoices, 1);
  assert.equal(result.summary.completeForAggregation, false);
  assert.equal(result.summary.result, null);
  assert.equal(result.summary.placement, null);
  assert.deepEqual(result.events[1].data.options[0].package, ['B', 'B']);
  assert.equal(result.events[1].data.picked, 1);
});
