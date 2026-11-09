// Explicit integration scope: node --test server/pool/test/real-worker.integration.mjs
// Requires the REAL published/built P3 worker; no fallback or substituted test parser.
import assert from 'node:assert/strict';
import test from 'node:test';
import * as fs from 'node:fs';
import { ID, DAY, setup, start, record, choice, end, jsonl, chunk, manifest } from './capture-fixture.mjs';
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

// AC18 (TASK-2026-11-09 analytics round 1): the real v2 worker carries a BG hero pick and place 3 through the P2 server
// to checked facts and the published snapshot; forgetting the generation takes them out again. One installation only,
// so the publication thresholds are lowered for this test (they are read at publish time).
test('AC18 real dotnet worker v2: BG hero pick and place reach facts and the snapshot, forget removes them', async t => {
  const saved = [process.env.ANALYTICS_MIN_CELL, process.env.ANALYTICS_MIN_SOURCES];
  process.env.ANALYTICS_MIN_CELL = '1'; process.env.ANALYTICS_MIN_SOURCES = '1';
  t.after(() => {
    for (const [name, value] of [['ANALYTICS_MIN_CELL', saved[0]], ['ANALYTICS_MIN_SOURCES', saved[1]]]) {
      if (value === undefined) delete process.env[name]; else process.env[name] = value;
    }
  });
  const f = await setup(t, { archive: { worker } });
  const v2 = (seq, kind, data, extra = {}) => ({ ...record(seq, kind, data, extra), v: 2 });
  const heroes = ['HERO_A', 'HERO_B', 'HERO_C', 'HERO_D'];
  const bytes = jsonl([
    v2(0, 'start', { captureKind: 'game', mode: 'bg', day: DAY, build: 1000, format: null, arena: null }),
    v2(1, 'gap', { reason: 'combat_view', count: 1 }),
    v2(2, 'choice', { choiceId: 1, sourceCard: null, options: heroes, picked: [], resolved: false, choiceType: 'mulligan' }, { side: 'local' }),
    v2(3, 'choice', { choiceId: 1, sourceCard: null, options: heroes, picked: ['HERO_B'], resolved: true, choiceType: 'mulligan' }, { side: 'local' }),
    v2(4, 'end', { status: 'incomplete', result: 'L', placement: 3, reason: 'normal' }, { turn: 9 }),
  ]);
  const response = await f.complete(bytes, { mode: 'bg', sourceVersion: 2, captureStatus: 'incomplete', gaps: 1 });
  assert.equal(response.status, 202, await response.text());
  await f.pool.archive.tick();
  const row = f.pool.archive.row(identity(ID).key);
  assert.equal(row.status, 'partial');
  const result = JSON.parse(row.derived);
  assert.equal(result.v, 2);
  assert.equal(result.parserVersion, 'source-v2.0');
  assert.equal(result.sourceVersion, 2);
  assert.equal(result.summary.completeForAggregation, false);
  assert.deepEqual(result.eligibility, { outcome: true, offers: true, reasons: [] });
  assert.deepEqual(result.facts, { outcome: { result: 'L', placement: 3 }, offers: [{ seq: 3, kind: 'bg_hero', options: heroes, picked: ['HERO_B'] }] });
  const sqlite = f.pool.store.sqlite;
  const fact = sqlite.prepare('SELECT mode, build, outcome_ok, offers_ok, placement FROM source_fact_game').all();
  assert.deepEqual(fact.map(r => ({ ...r })), [{ mode: 'bg', build: 1000, outcome_ok: 1, offers_ok: 1, placement: 3 }]);
  assert.equal(sqlite.prepare('SELECT COUNT(*) AS n FROM source_fact_offer').get().n, 4);
  assert.equal(await f.pool.archive.publishAnalytics(), true);
  const read = async () => (await fetch(`${f.pool.url}/v2/analytics/bg/heroes?mode=solo`)).json();
  const body = await read();
  assert.equal(body.available, true);
  const group = body.groups.find(g => g.build === 1000);
  const hero = group.heroes.find(h => h.hero === 'HERO_B');
  assert.deepEqual([hero.offered, hero.picked, hero.placement.n, hero.placement.dist[2]], [1, 1, 1, 1]);
  assert.equal((await f.post('forget', { v: 1, iid: ID.iid, generation: ID.generation })).status, 200);
  assert.equal(sqlite.prepare('SELECT COUNT(*) AS n FROM source_fact_game').get().n, 0);
  assert.equal(sqlite.prepare('SELECT COUNT(*) AS n FROM source_fact_offer').get().n, 0);
  const gone = await read();
  assert.ok(gone.available === false || !gone.groups.some(g => g.heroes.length > 0), JSON.stringify(gone));
  await f.pool.archive.publishAnalytics();
  const rebuilt = await read();
  assert.ok(rebuilt.available === false || !rebuilt.groups.some(g => g.heroes.length > 0), JSON.stringify(rebuilt));
});
