// TASK-2026-11-09 F2: source v2 facts -> idempotent, deletable contribution -> snapshot -> GET /v2/analytics/bg/heroes.
// Thresholds are lowered for these hand-checked sets (the defaults are 10 per cell, 5 sources per hero).
process.env.ANALYTICS_MIN_CELL = '1';
process.env.ANALYTICS_MIN_SOURCES = '1';
import assert from 'node:assert/strict';
import test from 'node:test';
import * as fs from 'node:fs';
import path from 'node:path';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { DatabaseSync } from 'node:sqlite';
import { DAY, record, jsonl, chunk, manifest, setup, workerConfig } from './capture-fixture.mjs';
import { identity } from '../node/validation.mjs';
import { startPool } from '../node/server.mjs';

const hex = n => n.toString(16).padStart(32, '0');
const ids = (generation, capture) => ({ v: 1, iid: hex(0x100 + generation), generation: hex(0x200 + generation), captureId: hex(0x300 + capture) });
const v2 = (seq, kind, data, extra = {}) => ({ ...record(seq, kind, data, extra), v: 2 });
const count = (pool, table, where = '') => pool.store.sqlite.prepare(`SELECT COUNT(*) AS n FROM ${table} ${where}`).get().n;

/** A BG v2 game: start, the unconditional combat_view gap, optional extra gaps, the hero choice and the end. */
function bgSource({ mode = 'bg', build = 1000, options, picked, placement, reason = 'normal', choiceType = 'mulligan', extraGaps = [], resolved = true, day = DAY }) {
  const rows = [v2(0, 'start', { captureKind: 'game', mode, day, build, format: null, arena: null }),
    v2(1, 'gap', { reason: 'combat_view', count: 1 })];
  for (const reasonGap of extraGaps) rows.push(v2(rows.length, 'gap', { reason: reasonGap, count: 1 }));
  rows.push(v2(rows.length, 'choice', { choiceId: 11, sourceCard: null, options, picked: resolved ? [picked] : [], resolved, choiceType }, { turn: 1, side: 'local' }));
  rows.push(v2(rows.length, 'end', { status: 'incomplete', result: placement === null ? null : placement <= 4 ? 'W' : 'L', placement, reason }, { turn: 9 }));
  return { bytes: jsonl(rows), overrides: { mode, sourceVersion: 2, captureStatus: 'incomplete', gaps: 1 + extraGaps.length } };
}
async function submit(f, id, game) {
  const response = await f.complete(game.bytes, game.overrides, id);
  assert.equal(response.status, 202, await response.text());
}
async function drain(f) { while (await f.pool.archive.tick()); }
async function heroes(f, query = '?mode=solo') {
  const response = await fetch(`${f.pool.url}/v2/analytics/bg/heroes${query}`);
  return { status: response.status, headers: response.headers, body: await response.json() };
}

// Hand-checked set (AC9): 7 BG solo captures from 5 generations A..E.
//  #1 A b1000 [H1 H2 H3 H4] -> H1, place 1      #2 A b1000 [H1 H2 H5 H6] -> H1, place 4
//  #3 B b1000 [H1 H3 H5 H7] -> H3, place 6      #4 C b1000 [H1 H2 H3 H8] -> H1, place 2
//  #5 D b1000 [H2 H3 H4 H5] -> H2, no place (offers=true, outcome=false)
//  #6 E b999  [H1 H4 H6 H7] -> H4, place 8      #7 E b1000 first choice "general" (offers=false): counts nowhere.
// Build 1000: captures #1-#5 = 5, sources A B C D = 4.
//  H1 offered 4 (#1-#4), picked 3 (#1 #2 #4), places 1 4 2: n 3, mean 7/3 = 2.333, s = sqrt(14/3 / 2) = 1.528,
//     mean CI 2.333 -+ 1.96*1.528/sqrt3 = 0.605..4.062; dist [1,1,0,1,0,0,0,0]; top4 3/3; first 1/3.
//     pickRate 3/4: Wilson z=1.96 -> centre (0.75 + 0.4802)/1.9604 = 0.6275, half 0.3269 -> 0.301..0.954.
//  H2 offered 4 (#1 #2 #4 #5), picked 1 (#5, outcome false) -> placement n 0, every placement value null.
//  H3 offered 4 (#1 #3 #4 #5), picked 1 (#3) -> n 1, mean 6, no interval (n < 2), top4 0/1, first 0/1.
//  H4 offered 2 (#1 #5), H5 offered 3 (#2 #3 #5), H6 1, H7 1, H8 1, none picked.
// Build 999: capture #6 only; H4 picked, place 8.
const SET = [
  [0, 1, { options: ['H1', 'H2', 'H3', 'H4'], picked: 'H1', placement: 1 }],
  [0, 2, { options: ['H1', 'H2', 'H5', 'H6'], picked: 'H1', placement: 4 }],
  [1, 3, { options: ['H1', 'H3', 'H5', 'H7'], picked: 'H3', placement: 6 }],
  [2, 4, { options: ['H1', 'H2', 'H3', 'H8'], picked: 'H1', placement: 2 }],
  [3, 5, { options: ['H2', 'H3', 'H4', 'H5'], picked: 'H2', placement: null }],
  [4, 6, { options: ['H1', 'H4', 'H6', 'H7'], picked: 'H4', placement: 8, build: 999 }],
  [4, 7, { options: ['H1', 'H2', 'H3', 'H4'], picked: 'H1', placement: 1, choiceType: 'general' }],
];
async function loadSet(f, set = SET) {
  for (const [generation, capture, game] of set) await submit(f, ids(generation, capture), bgSource(game));
  await drain(f);
}

/** Independent plain calculation from the facts in the database (no code shared with node/analytics.mjs). */
function independent(sqlite, minCell = 1, minSources = 1) {
  const r3 = x => Math.round(x * 1000) / 1000;
  const wil = (k, n) => {
    if (n < minCell || n === 0) return { num: k, den: n, value: null, low: null, high: null };
    const p = k / n, z = 1.96, a = 1 + z * z / n;
    const c = (p + z * z / (2 * n)) / a, h = z * Math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / a;
    return { num: k, den: n, value: r3(p), low: r3(Math.max(0, c - h)), high: r3(Math.min(1, c + h)) };
  };
  const games = sqlite.prepare("SELECT * FROM source_fact_game WHERE mode='bg' AND offers_ok=1").all();
  const builds = [...new Set(games.map(g => g.build))].sort((a, b) => (a === null) - (b === null) || b - a);
  return builds.map(build => {
    const list = games.filter(g => g.build === build).map(g => {
      const offer = sqlite.prepare('SELECT card,picked FROM source_fact_offer WHERE capture=? ORDER BY slot').all(g.capture);
      return { ...g, options: [...new Set(offer.map(o => o.card))], pick: offer.find(o => o.picked)?.card };
    });
    const names = [...new Set(list.flatMap(g => g.options))];
    let suppressed = 0;
    const rows = [];
    for (const hero of names) {
      const seen = list.filter(g => g.options.includes(hero));
      if (new Set(seen.map(g => g.generation)).size < minSources) { suppressed++; continue; }
      const mine = seen.filter(g => g.pick === hero);
      const places = mine.filter(g => g.outcome_ok).map(g => g.placement);
      const n = places.length;
      const dist = [1, 2, 3, 4, 5, 6, 7, 8].map(p => places.filter(x => x === p).length);
      let placement = { n, mean: null, low: null, high: null, dist };
      if (n >= minCell && n > 0) {
        const mean = places.reduce((a, b) => a + b, 0) / n;
        placement = { n, mean: r3(mean), low: null, high: null, dist };
        if (n >= 2) {
          const sd = Math.sqrt(places.reduce((a, p) => a + (p - mean) ** 2, 0) / (n - 1));
          placement.low = r3(mean - 1.96 * sd / Math.sqrt(n));
          placement.high = r3(mean + 1.96 * sd / Math.sqrt(n));
        }
      }
      const row = { hero, offered: seen.length, picked: mine.length, pickRate: wil(mine.length, seen.length), placement,
        top4: wil(places.filter(p => p <= 4).length, n), first: wil(places.filter(p => p === 1).length, n) };
      row.quality = [row.pickRate.value, placement.mean, row.top4.value, row.first.value].includes(null) ? 'small_sample' : 'ok';
      rows.push(row);
    }
    rows.sort((a, b) => b.offered - a.offered || (a.hero < b.hero ? -1 : 1));
    return { build, captures: list.length, sources: new Set(list.map(g => g.generation)).size, suppressed, heroes: rows };
  });
}

test('AC9 hand-checked BG solo set publishes exact counts, rates and intervals that an independent calculation repeats', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('result-v2') } });
  await loadSet(f);
  assert.equal(count(f.pool, 'source_captures', "WHERE status='partial'"), 7);
  assert.equal(count(f.pool, 'source_fact_game'), 7);
  assert.equal(count(f.pool, 'source_fact_game', 'WHERE offers_ok=1'), 6);
  assert.equal(count(f.pool, 'source_fact_game', 'WHERE outcome_ok=1'), 6);
  assert.equal(await f.pool.archive.publishAnalytics(), true);
  const { status, headers, body } = await heroes(f);
  assert.equal(status, 200);
  assert.equal(headers.get('cache-control'), 'no-store');
  assert.match(headers.get('content-type'), /application\/json/);
  assert.deepEqual({ ...body, groups: undefined, published: undefined }, {
    v: 2, metric: 'bg.hero.offer_pick_placement', rulesVersion: 'bg-hero-1', mode: 'solo', available: true, reason: null,
    stale: false, source: 'source_capture', period: { fromDay: DAY - 30, toDay: DAY, days: 30 }, groups: undefined, published: undefined });
  assert.ok(Number.isSafeInteger(body.published));
  assert.deepEqual(body.groups.map(g => [g.build, g.captures, g.sources, g.suppressed]), [[1000, 5, 4, 0], [999, 1, 1, 0]]);
  const h = Object.fromEntries(body.groups[0].heroes.map(x => [x.hero, x]));
  assert.deepEqual(body.groups[0].heroes.map(x => [x.hero, x.offered, x.picked]),
    [['H1', 4, 3], ['H2', 4, 1], ['H3', 4, 1], ['H5', 3, 0], ['H4', 2, 0], ['H6', 1, 0], ['H7', 1, 0], ['H8', 1, 0]]);
  assert.deepEqual(h.H1.pickRate, { num: 3, den: 4, value: 0.75, low: 0.301, high: 0.954 });
  assert.deepEqual(h.H1.placement, { n: 3, mean: 2.333, low: 0.605, high: 4.062, dist: [1, 1, 0, 1, 0, 0, 0, 0] });
  assert.deepEqual([h.H1.top4.num, h.H1.top4.den, h.H1.top4.value, h.H1.first.num, h.H1.first.value], [3, 3, 1, 1, 0.333]);
  assert.equal(h.H1.quality, 'ok');
  assert.deepEqual(h.H2.placement, { n: 0, mean: null, low: null, high: null, dist: [0, 0, 0, 0, 0, 0, 0, 0] });
  assert.equal(h.H2.quality, 'small_sample');
  assert.deepEqual(h.H3.placement, { n: 1, mean: 6, low: null, high: null, dist: [0, 0, 0, 0, 0, 1, 0, 0] });
  assert.deepEqual([h.H3.top4.value, h.H3.first.value], [0, 0]);
  assert.deepEqual(body.groups[1].heroes.find(x => x.hero === 'H4').placement.dist, [0, 0, 0, 0, 0, 0, 0, 1]);
  assert.deepEqual(body.groups, independent(f.pool.store.sqlite));
});

test('AC12 offers without an outcome count only in offered/picked, offers=false and Duos count nowhere in solo', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('result-v2') } });
  await loadSet(f, [
    [0, 1, { options: ['H1', 'H2'], picked: 'H1', placement: 2 }],
    [1, 2, { options: ['H1', 'H2'], picked: 'H1', placement: null }],
    [2, 3, { options: ['H1', 'H2'], picked: 'H1', placement: 1, choiceType: 'general' }],
    [3, 4, { options: ['H1', 'H2'], picked: 'H1', placement: 1, extraGaps: ['late_attach'] }],
    [4, 5, { mode: 'duos', options: ['H1', 'H2'], picked: 'H1', placement: 1 }],
  ]);
  assert.equal(count(f.pool, 'source_fact_game', "WHERE mode='duos' AND offers_ok=1 AND outcome_ok=1"), 1);
  await f.pool.archive.publishAnalytics();
  const { body } = await heroes(f);
  assert.equal(body.groups.length, 1);
  const g = body.groups[0];
  assert.deepEqual([g.captures, g.sources], [2, 2]);
  const h1 = g.heroes.find(x => x.hero === 'H1');
  assert.deepEqual([h1.offered, h1.picked, h1.placement.n, h1.top4.den, h1.first.den], [2, 2, 1, 1, 1]);
  assert.deepEqual((await heroes(f, '?mode=duos')).body.reason, 'unsupported_mode');
});

test('AC10 repeated chunks, manifest and reprocess with the same or another parser keep one contribution', async t => {
  const f = await setup(t, { operatorToken: 'op', archive: { worker: workerConfig('result-v2') } });
  await loadSet(f);
  await f.pool.archive.publishAnalytics();
  const before = (await heroes(f)).body.groups;
  const [generation, capture, game] = SET[0];
  const id = ids(generation, capture);
  const source = bgSource(game);
  const c = chunk(source.bytes, 0, id);
  assert.equal((await f.post('chunks', c)).status, 200);
  assert.equal((await f.post('complete', manifest([c], source.overrides, id))).status, 202);
  const key = identity(id).key;
  const facts = () => [count(f.pool, 'source_fact_game'), count(f.pool, 'source_fact_offer'), count(f.pool, 'source_fact_offer', `WHERE capture='${key}'`)];
  const original = facts();
  for (const worker of ['result-v2', 'result-v2-other']) {
    f.pool.archive.worker = workerConfig(worker);
    assert.equal((await f.post('reprocess', id, { authorization: 'Bearer op' })).status, 202);
    assert.deepEqual(facts(), original, 'old facts stay until the new result commits');
    await drain(f);
    assert.deepEqual(facts(), original, 'one set after the replacement, never two');
    assert.equal(f.pool.store.sqlite.prepare('SELECT parser_version FROM source_fact_game WHERE capture=?').get(key).parser_version,
      worker === 'result-v2' ? 'fixture-r2' : 'fixture-r2b');
    assert.equal((await heroes(f)).body.reason, 'rebuilding', 'replaced facts withdraw the published numbers');
    await f.pool.archive.publishAnalytics();
    assert.deepEqual((await heroes(f)).body.groups, before);
  }
  // A failed reparse contributes nothing.
  f.pool.archive.worker = workerConfig('lie-placement');
  await f.post('reprocess', id, { authorization: 'Bearer op' });
  await drain(f);
  assert.equal(f.pool.archive.row(key).status, 'failed');
  assert.equal(count(f.pool, 'source_fact_offer', `WHERE capture='${key}'`), 0);
  assert.equal((await heroes(f)).body.reason, 'rebuilding');
});

test('AC11 forget and retention delete facts and the snapshot together; a late worker result brings nothing back', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('result-v2') } });
  await loadSet(f);
  await f.pool.archive.publishAnalytics();
  const forgetA = { v: 1, iid: ids(0, 0).iid, generation: ids(0, 0).generation };
  assert.equal((await f.post('forget', forgetA)).status, 200);
  assert.equal(count(f.pool, 'source_fact_game', `WHERE generation='${identity(ids(0, 1)).generation}'`), 0);
  assert.equal(count(f.pool, 'source_fact_offer'), 4 * 4, 'offers of #3 #4 #5 #6 remain');
  assert.deepEqual((await heroes(f)).body, { v: 2, metric: 'bg.hero.offer_pick_placement', rulesVersion: 'bg-hero-1', mode: 'solo',
    available: false, reason: 'rebuilding', published: null, stale: false, source: 'source_capture', period: null, groups: [] });
  assert.equal(await f.pool.archive.refreshAnalytics(), true);
  let body = (await heroes(f)).body;
  const h1 = body.groups[0].heroes.find(x => x.hero === 'H1');
  assert.deepEqual([body.groups[0].captures, h1.offered, h1.picked, h1.placement.n], [3, 2, 1, 1]);
  assert.deepEqual(body.groups, independent(f.pool.store.sqlite));
  // Retention: everything is older than one day at +2 days.
  f.pool.archive.retentionDays = 1;
  await f.pool.archive.maintain(Date.now() + 2 * 86_400_000);
  assert.equal(count(f.pool, 'source_fact_game'), 0);
  assert.equal(count(f.pool, 'source_fact_offer'), 0);
  assert.equal((await heroes(f)).body.reason, 'rebuilding');
  await f.pool.archive.publishAnalytics();
  body = (await heroes(f)).body;
  assert.deepEqual([body.available, body.groups], [true, []]);
  // Late result: the worker finishes after its generation was forgotten.
  f.pool.archive.retentionDays = 180;
  f.pool.archive.worker = workerConfig('result-v2-slow');
  const late = ids(7, 70);
  await submit(f, late, bgSource(SET[0][2]));
  const job = f.pool.archive.tick();
  const deadline = Date.now() + 10000;
  while (f.pool.archive.row(identity(late).key)?.status !== 'processing') {
    assert.ok(Date.now() < deadline);
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.equal((await f.post('forget', { v: 1, iid: late.iid, generation: late.generation })).status, 200);
  await job;
  assert.equal(count(f.pool, 'source_fact_game'), 0);
  assert.equal(count(f.pool, 'source_captures', "WHERE status<>'deleted'"), 0);
});

for (const [name, worker, game] of [
  ['placement differs from end.placement', 'lie-placement', {}],
  ['offer seq is not the resolved choice record', 'lie-offer-seq', {}],
  ['offer options differ from the choice', 'lie-offer-options', {}],
  ['offer pick differs from the choice', 'lie-offer-picked', {}],
  ['offers claimed for a general choice', 'lie-offers', { choiceType: 'general' }],
  ['outcome claimed after late attach', 'lie-outcome', { extraGaps: ['late_attach'] }],
  ['outcome claimed after rotation', 'lie-outcome', { extraGaps: ['rotation'] }],
  ['outcome claimed after shutdown', 'lie-outcome', { extraGaps: ['shutdown'] }],
  ['outcome claimed for an abandoned game', 'lie-outcome', { reason: 'abandoned' }],
  ['context differs from start', 'lie-context', {}],
  ['source version differs from the manifest', 'lie-source-version', {}],
]) test(`AC13 worker result v2 is failed without facts when ${name}`, async t => {
  const f = await setup(t, { archive: { worker: workerConfig(worker) } });
  await submit(f, ids(0, 1), bgSource({ options: ['H1', 'H2', 'H3'], picked: 'H2', placement: 3, ...game }));
  await drain(f);
  const row = f.pool.archive.row(identity(ids(0, 1)).key);
  assert.equal(row.status, 'failed');
  assert.equal(count(f.pool, 'source_fact_game'), 0);
  assert.equal(count(f.pool, 'source_fact_offer'), 0);
});

test('AC13 honest v2 results for the same sources are accepted, and a v1 result stays accepted without facts', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('result-v2') } });
  await submit(f, ids(0, 1), bgSource({ options: ['H1', 'H2'], picked: 'H2', placement: 3, extraGaps: ['late_attach'] }));
  await submit(f, ids(0, 2), bgSource({ options: ['H1', 'H2'], picked: 'H2', placement: 3, reason: 'abandoned' }));
  await drain(f);
  assert.deepEqual(f.pool.store.sqlite.prepare('SELECT outcome_ok,offers_ok,placement FROM source_fact_game').all().map(r => ({ ...r })),
    [{ outcome_ok: 0, offers_ok: 0, placement: null }, { outcome_ok: 0, offers_ok: 0, placement: null }]);
  f.pool.archive.worker = workerConfig('v1');
  await submit(f, ids(1, 3), bgSource({ options: ['H1', 'H2'], picked: 'H2', placement: 3 }));
  await drain(f);
  assert.equal(f.pool.archive.row(identity(ids(1, 3)).key).status, 'partial');
  assert.equal(count(f.pool, 'source_fact_game', `WHERE capture='${identity(ids(1, 3)).key}'`), 0);
  // A v2 result for a v1 Constructed source: outcome follows completeForAggregation, no offers.
  f.pool.archive.worker = workerConfig('result-v2');
  const v1 = jsonl([record(0, 'start', { captureKind: 'game', mode: 'ranked', day: DAY, build: 5 }), record(1, 'end', { status: 'complete', result: 'W', placement: null, reason: 'normal' }, { turn: 2 })]);
  assert.equal((await f.complete(v1, {}, ids(2, 4))).status, 202);
  await drain(f);
  assert.deepEqual({ ...f.pool.store.sqlite.prepare('SELECT mode,source_version,outcome_ok,offers_ok,result FROM source_fact_game WHERE capture=?').get(identity(ids(2, 4)).key) },
    { mode: 'ranked', source_version: 1, outcome_ok: 1, offers_ok: 0, result: 'W' });
});

test('AC15 not published, unsupported mode, unknown query and the response leaks no identities', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('result-v2') } });
  let answer = await heroes(f);
  assert.equal(answer.status, 200);
  assert.equal(answer.headers.get('cache-control'), 'no-store');
  assert.deepEqual([answer.body.available, answer.body.reason], [false, 'not_published']);
  assert.deepEqual([(await heroes(f, '')).body.reason, (await heroes(f, '?mode=duos')).body.reason], ['not_published', 'unsupported_mode']);
  for (const query of ['?mode=solo&x=1', '?mode=all', '?mode=solo&mode=solo', '?iid=1']) assert.equal((await heroes(f, query)).status, 400, query);
  assert.equal((await fetch(`${f.pool.url}/v2/analytics/bg/heroes`, { method: 'POST', body: '{}' })).status, 405);
  assert.equal(await f.pool.archive.refreshAnalytics(), false, 'nothing to publish yet');
  await loadSet(f);
  assert.equal(await f.pool.archive.refreshAnalytics(), true);
  assert.equal(await f.pool.archive.refreshAnalytics(), false, 'fresh snapshot is not rebuilt');
  answer = await heroes(f);
  const text = JSON.stringify(answer.body);
  for (const [generation, capture] of SET) {
    const id = identity(ids(generation, capture));
    for (const secret of [id.key, id.generation, id.owner, ids(generation, capture).iid, ids(generation, capture).generation, ids(generation, capture).captureId])
      assert.equal(text.includes(secret), false);
  }
  assert.doesNotMatch(text, /"(capture|generation|iid|key|owner)"/);
  // New facts mark the snapshot stale; the old numbers stay available until the next rebuild.
  await submit(f, ids(9, 90), bgSource({ options: ['H1', 'H9'], picked: 'H9', placement: 5 }));
  await drain(f);
  answer = await heroes(f);
  assert.deepEqual([answer.body.available, answer.body.stale], [true, true]);
  assert.equal(answer.body.groups[0].heroes.some(x => x.hero === 'H9'), false);
  assert.equal(await f.pool.archive.refreshAnalytics(), true);
  assert.equal((await heroes(f)).body.groups[0].heroes.some(x => x.hero === 'H9'), true);
});

test('AC14 strict v2 intake: accepted contract lines, rejected cross-version fields and version mismatch', async t => {
  const f = await setup(t);
  let n = 0;
  const next = () => ids(0, ++n);
  const reject = async (rows, overrides = {}, at = 'chunks') => {
    const id = next();
    const bytes = jsonl(rows);
    if (at === 'chunks') return (await f.post('chunks', chunk(bytes, 0, id))).status;
    return (await f.complete(bytes, overrides, id)).status;
  };
  const ok = bgSource({ options: ['H1', 'H2'], picked: 'H1', placement: 1 });
  assert.equal((await f.complete(ok.bytes, ok.overrides, next())).status, 202);
  const ranked = [v2(0, 'start', { captureKind: 'game', mode: 'ranked', day: DAY, build: 1, format: 'wild', arena: null }),
    v2(1, 'choice', { choiceId: 1, sourceCard: null, options: ['A', 'B', 'C'], picked: ['A'], resolved: true, choiceType: 'mulligan' }, { side: 'local' }),
    v2(2, 'end', { status: 'complete', result: 'W', placement: null, reason: 'normal' }, { turn: 2 })];
  assert.equal((await f.complete(jsonl(ranked), { sourceVersion: 2 }, next())).status, 202);
  const arena = [v2(0, 'start', { captureKind: 'game', mode: 'arena', day: DAY, build: null, format: null, arena: 'underground' }), { ...ranked[2], seq: 1 }];
  assert.equal((await f.complete(jsonl(arena), { mode: 'arena', sourceVersion: 2 }, next())).status, 202);
  const v1start = record(0, 'start', { captureKind: 'game', mode: 'ranked', day: DAY, build: null });
  for (const rows of [
    [{ ...v1start, data: { ...v1start.data, format: null, arena: null } }],
    [v1start, record(1, 'choice', { choiceId: 1, sourceCard: null, options: ['A'], picked: ['A'], resolved: true, choiceType: 'general' }, { side: 'local' })],
    [v1start, record(1, 'gap', { reason: 'combat_view', count: 1 })],
    [{ ...ranked[0], data: { ...ranked[0].data, format: 'standard2' } }],
    [{ ...ranked[0], data: { ...ranked[0].data, format: null, arena: 'normal' } }],
    [{ ...ranked[0], data: { ...ranked[0].data, mode: 'bg' } }],
    [{ ...arena[0], data: { ...arena[0].data, arena: 'heroic' } }],
    [{ ...ranked[1], data: { ...ranked[1].data, choiceType: 'discover' } }],
    [ranked[0], { ...ranked[1], data: { ...ranked[1].data, choiceType: undefined } }],
    [ranked[0], { ...ranked[1], v: 1, data: { choiceId: 1, sourceCard: null, options: ['A'], picked: ['A'], resolved: true } }],
    [{ ...ranked[0], v: 3 }],
  ]) assert.equal(await reject(rows), 400, JSON.stringify(rows));
  // A v2 source under a v1 manifest and the reverse; combat_view outside BG and twice in BG.
  assert.equal(await reject(ranked, {}, 'complete'), 400);
  assert.equal(await reject([v1start, record(1, 'end', { status: 'complete', result: 'W', placement: null, reason: 'normal' })], { sourceVersion: 2 }, 'complete'), 400);
  const combat = v2(1, 'gap', { reason: 'combat_view', count: 1 });
  assert.equal(await reject([ranked[0], combat, v2(2, 'end', { status: 'incomplete', result: null, placement: null, reason: 'normal' })],
    { sourceVersion: 2, captureStatus: 'incomplete', gaps: 1 }, 'complete'), 400);
  const bg = ok.bytes.toString().trimEnd().split('\n').map(JSON.parse);
  const twice = [bg[0], bg[1], { ...bg[1], seq: 2 }, { ...bg[2], seq: 3 }, { ...bg[3], seq: 4 }];
  assert.equal(await reject(twice, { ...ok.overrides, gaps: 2 }, 'complete'), 400);
  const progression = type => [bg[0], { ...bg[2], seq: 1, data: { ...bg[2].data, resolved: false, picked: [] } },
    { ...bg[2], seq: 2, data: { ...bg[2].data, choiceType: type } }, { ...bg[3], seq: 3 }];
  assert.equal(await reject(progression('mulligan'), { ...ok.overrides, gaps: 0 }, 'complete'), 202);
  assert.equal(await reject(progression('general'), { ...ok.overrides, gaps: 0 }, 'complete'), 400, 'choiceType cannot change within one choice');
});

test('AC16 migration 0009 on a 0008 database is additive and idempotent; a failed rebuild publishes nothing; 10 000 captures rebuild', async t => {
  const dataDir = mkdtempSync(path.join(tmpdir(), 'hp-analytics-'));
  let pool;
  t.after(async () => { await pool?.close(); rmSync(dataDir, { recursive: true, force: true }); });
  const old = new DatabaseSync(path.join(dataDir, 'pool.sqlite'));
  old.exec(fs.readFileSync(new URL('../schema.sql', import.meta.url), 'utf8'));
  old.exec(fs.readFileSync(new URL('../migrations/0008_captures.sql', import.meta.url), 'utf8'));
  old.prepare("INSERT INTO source_captures(key,generation,owner,created,touched,status) VALUES('k','g','o',?,?,'stored')").run(Date.now(), Date.now());
  old.prepare('INSERT INTO source_revoked(generation,revoked_at) VALUES(?,?)').run('f'.repeat(64), 1);
  const tables = db => db.prepare("SELECT name FROM sqlite_master WHERE type IN ('table','index','trigger') ORDER BY name").all().map(r => r.name);
  const before = tables(old);
  old.close();
  const config = { dataDir, port: 0, schedule: false, archive: { minFreeBytes: 0 } };
  for (let i = 0; i < 2; i++) {
    pool = await startPool(config);
    const now = tables(pool.store.sqlite);
    assert.ok(before.every(name => now.includes(name)));
    assert.deepEqual(now.filter(name => !before.includes(name)).sort(),
      ['analytics_snap', 'source_fact_game', 'source_fact_game_by_generation', 'source_fact_game_by_mode_day', 'source_fact_offer']);
    assert.equal(pool.store.sqlite.prepare("SELECT status FROM source_captures WHERE key='k'").get().status, 'stored');
    assert.equal(pool.store.sqlite.prepare('SELECT COUNT(*) AS n FROM source_revoked').get().n, 1);
    await pool.close();
  }
  // A rebuild that fails half way (here: an invalid threshold read inside the transaction) leaves the withdrawn row.
  pool = await startPool(config);
  const db = pool.store.sqlite;
  db.prepare("INSERT INTO analytics_snap(key,rules_version,built,stale,body) VALUES('bg-heroes-solo','bg-hero-1',1,1,'')").run();
  insertSynthetic(db, 10_000);
  process.env.ANALYTICS_MIN_CELL = 'broken';
  await assert.rejects(pool.archive.publishAnalytics());
  process.env.ANALYTICS_MIN_CELL = '1';
  assert.equal(db.prepare("SELECT body FROM analytics_snap WHERE key='bg-heroes-solo'").get().body, '');
  await pool.close();
  pool = await startPool(config);
  const reply = await (await fetch(`${pool.url}/v2/analytics/bg/heroes?mode=solo`)).json();
  assert.equal(reply.reason, 'rebuilding', 'a restart never exposes a half-built snapshot');
  const started = performance.now();
  assert.equal(await pool.archive.refreshAnalytics(), true);
  const ms = performance.now() - started;
  t.diagnostic(`rebuild of 10000 synthetic BG captures: ${ms.toFixed(0)} ms`);
  assert.ok(ms < 5000, `rebuild took ${ms} ms`);
  const body = (await (await fetch(`${pool.url}/v2/analytics/bg/heroes?mode=solo`)).json());
  assert.equal(body.groups.reduce((n, g) => n + g.captures, 0), 10_000);
});

function insertSynthetic(db, n) {
  db.exec('BEGIN');
  const game = db.prepare(`INSERT INTO source_fact_game(capture,generation,parser_version,source_version,mode,day,build,outcome_ok,offers_ok,result,placement)
    VALUES(?,?,'synthetic',2,'bg',?,?,1,1,NULL,?)`);
  const offer = db.prepare("INSERT INTO source_fact_offer(capture,seq,kind,slot,card,picked) VALUES(?,3,'bg_hero',?,?,?)");
  for (let i = 0; i < n; i++) {
    const capture = i.toString(16).padStart(64, '0');
    game.run(capture, (i % 997).toString(16).padStart(64, 'a'), DAY - (i % 20), 200000 + (i % 3), 1 + (i % 8));
    for (let slot = 0; slot < 4; slot++) offer.run(capture, slot, `HERO_${(i * 7 + slot * 13) % 90}`, slot === i % 4 ? 1 : 0);
  }
  db.exec('COMMIT');
}
