// Analytics facts and the published "BG hero: offer -> pick -> placement" snapshot (TASK-2026-11-09 F2).
// Contracts C2-C5 of the task card; field meanings in docs/analytics-api.md. Facts come only from a worker result
// v2 that the archive has cross-checked against the typed source; the snapshot is rebuilt outside HTTP in the
// archive's SerialQueue, in one transaction, and the API never exposes capture keys, generations or install ids.
import { LIFECYCLE_GAPS, exact, int } from './validation.mjs';

export const BG_HEROES_KEY = 'bg-heroes-solo';
export const BG_HEROES_METRIC = 'bg.hero.offer_pick_placement';
export const BG_HEROES_RULES = 'bg-hero-1';
export const WINDOW_DAYS = 30;
const REASONS = ['source_v1', 'draft', 'no_end', 'end_reason', 'lifecycle_gap', 'no_outcome', 'no_offer', 'offer_unresolved', 'offer_shape'];
const CONTEXT = ['mode', 'day', 'build', 'format', 'arena'];
const Z = 1.96;

const invalid = code => { throw new Error(code); };
const sameList = (a, b) => Array.isArray(a) && Array.isArray(b) && a.length === b.length && a.every((x, i) => x === b[i]);

/**
 * What the typed source itself says about one capture, read record by record (a source can be 256 MiB, so rows are
 * not kept): start, end, lifecycle gaps and the last record of the first choice (C2: lowest choiceId).
 */
export function sourceFacts() {
  const facts = { version: null, start: null, end: null, lifecycleGap: false, choice: null };
  return {
    facts,
    add(r) {
      if (r.seq === 0) { facts.version = r.v; facts.start = r.kind === 'start' ? r.data : null; }
      if (r.kind === 'end') facts.end = r.data;
      if (r.kind === 'gap' && LIFECYCLE_GAPS.includes(r.data.reason)) facts.lifecycleGap = true;
      if (r.kind === 'choice' && (facts.choice === null || r.data.choiceId <= facts.choice.data.choiceId)) facts.choice = r;
    },
  };
}

/**
 * Checks the v2 additions of a worker result against the source and returns the facts to store, or null for a
 * capture that gives no facts (draft). Throws on anything the source does not support (AC13).
 */
export function checkResultV2(r, manifest, src) {
  if (!exact(r, ['v', 'parserVersion', 'sourceVersion', 'sourceSha256', 'status', 'records', 'unsupported', 'gaps', 'events', 'summary', 'context', 'eligibility', 'facts'])
    || r.sourceVersion !== manifest.sourceVersion || src.version !== manifest.sourceVersion) invalid('invalid_worker_v2');
  const { start, end } = src;
  // context = the start record (v1 sources have no format/arena: null).
  if (!exact(r.context, CONTEXT) || CONTEXT.some(k => r.context[k] !== (start?.[k] ?? null))) invalid('worker_context_not_in_source');
  const e = r.eligibility;
  if (!exact(e, ['outcome', 'offers', 'reasons']) || typeof e.outcome !== 'boolean' || typeof e.offers !== 'boolean'
    || !Array.isArray(e.reasons) || e.reasons.length > REASONS.length
    || !e.reasons.every((x, i) => REASONS.includes(x) && (i === 0 || REASONS.indexOf(e.reasons[i - 1]) < REASONS.indexOf(x)))) invalid('invalid_worker_eligibility');
  const f = r.facts;
  if (!exact(f, ['outcome', 'offers']) || !Array.isArray(f.offers)) invalid('invalid_worker_facts');
  const bg = ['bg', 'duos'].includes(manifest.mode);
  const lifecycleOk = manifest.captureKind === 'game' && end !== null && end.reason === 'normal' && !src.lifecycleGap;
  // Outcome: the safety direction is re-derived from the source; a worker may only be stricter.
  if (e.outcome) {
    if (manifest.sourceVersion === 1 && (bg || !r.summary.completeForAggregation)) invalid('worker_eligibility_not_in_source');
    const placementMax = manifest.mode === 'duos' ? 4 : 8;
    const value = bg ? int(end?.placement, 1, placementMax) : ['W', 'L', 'T'].includes(end?.result);
    if (!lifecycleOk || !value) invalid('worker_eligibility_not_in_source');
    if (!exact(f.outcome, ['result', 'placement']) || f.outcome.result !== end.result || f.outcome.placement !== end.placement) invalid('worker_outcome_not_in_source');
  } else if (f.outcome !== null) invalid('worker_outcome_not_in_source');
  if (e.offers) {
    const c = src.choice;
    if (manifest.sourceVersion !== 2 || !bg || !lifecycleOk || !c || c.data.choiceType !== 'mulligan' || !c.data.resolved
      || !int(c.data.options.length, 2, 4) || c.data.picked.length !== 1) invalid('worker_eligibility_not_in_source');
    if (f.offers.length !== 1) invalid('worker_offer_not_in_source');
    const o = f.offers[0];
    if (!exact(o, ['seq', 'kind', 'options', 'picked']) || o.seq !== c.seq || o.kind !== 'bg_hero'
      || !sameList(o.options, c.data.options) || !sameList(o.picked, c.data.picked)) invalid('worker_offer_not_in_source');
  } else if (f.offers.length !== 0) invalid('worker_offer_not_in_source');
  if (manifest.captureKind === 'draft') return null;
  return {
    game: { mode: start.mode, day: start.day, build: start.build ?? null, outcome: e.outcome, offers: e.offers,
      result: f.outcome?.result ?? null, placement: f.outcome?.placement ?? null },
    offers: f.offers.map(o => ({ seq: o.seq, kind: o.kind, options: o.options, picked: o.picked[0] })),
  };
}

/** Replaces the facts of one capture; returns true when it had facts before (a replacement, not an addition). */
export function replaceFacts(db, capture, generation, owner, parserVersion, sourceVersion, facts) {
  const had = removeFacts(db, [capture]);
  if (facts) {
    const g = facts.game;
    db.prepare(`INSERT INTO source_fact_game(capture,generation,owner,parser_version,source_version,mode,day,build,outcome_ok,offers_ok,result,placement)
      VALUES(?,?,?,?,?,?,?,?,?,?,?,?)`).run(capture, generation, owner, parserVersion, sourceVersion, g.mode, g.day, g.build, g.outcome ? 1 : 0, g.offers ? 1 : 0, g.result, g.placement);
    const insert = db.prepare('INSERT INTO source_fact_offer(capture,seq,kind,slot,card,picked) VALUES(?,?,?,?,?,?)');
    for (const o of facts.offers) {
      const pickedSlot = o.options.indexOf(o.picked);
      o.options.forEach((c, slot) => insert.run(capture, o.seq, o.kind, slot, c, slot === pickedSlot ? 1 : 0));
    }
  }
  return had;
}

/** Deletes the facts of these captures; true when any existed. */
export function removeFacts(db, captures) {
  let n = 0;
  for (const capture of captures) {
    n += Number(db.prepare('DELETE FROM source_fact_offer WHERE capture=?').run(capture).changes);
    n += Number(db.prepare('DELETE FROM source_fact_game WHERE capture=?').run(capture).changes);
  }
  return n > 0;
}

/** Deletes every fact of a generation; true when any existed. */
export function removeGenerationFacts(db, generation) {
  const captures = db.prepare('SELECT capture FROM source_fact_game WHERE generation=?').all(generation).map(r => r.capture);
  return removeFacts(db, captures);
}

/** The published numbers may contain the removed facts: withdraw them in the caller's transaction (AC11). */
export function withdrawSnapshot(db) {
  db.prepare("UPDATE analytics_snap SET body='',stale=1 WHERE key=?").run(BG_HEROES_KEY);
}
/** New facts only add: the old snapshot stays correct to serve until the next rebuild. */
export function markSnapshotStale(db) {
  db.prepare('UPDATE analytics_snap SET stale=1 WHERE key=?').run(BG_HEROES_KEY);
}
/** A rebuild is due when nothing is published, it is stale/withdrawn, or the 30-day window moved. */
export function snapshotDue(db, today) {
  const row = db.prepare('SELECT built,stale,body FROM analytics_snap WHERE key=?').get(BG_HEROES_KEY);
  if (row) return row.stale === 1 || row.body === '' || Math.floor(row.built / 86_400_000) !== today;
  return !!db.prepare("SELECT 1 FROM source_fact_game WHERE mode='bg' LIMIT 1").get();
}

const round = x => (x === null || !Number.isFinite(x) ? null : Math.round(x * 1000) / 1000);
/** Wilson score interval, z = 1.96. */
export function wilson(num, den) {
  if (!den) return { low: null, high: null };
  const p = num / den;
  const z2 = Z * Z;
  const centre = (p + z2 / (2 * den)) / (1 + z2 / den);
  const half = (Z * Math.sqrt(p * (1 - p) / den + z2 / (4 * den * den))) / (1 + z2 / den);
  return { low: Math.max(0, centre - half), high: Math.min(1, centre + half) };
}
function rate(num, den, minCell) {
  if (den < minCell || den === 0) return { num, den, value: null, low: null, high: null };
  const w = wilson(num, den);
  return { num, den, value: round(num / den), low: round(w.low), high: round(w.high) };
}
function placement(places, minCell) {
  const n = places.length;
  // The distribution of fewer than minCell games is as revealing as the places themselves (LEGAL L-M1): not shown.
  if (n < minCell || n === 0) return { n, mean: null, low: null, high: null, dist: null };
  const dist = [0, 0, 0, 0, 0, 0, 0, 0];
  for (const p of places) dist[p - 1]++;
  const mean = places.reduce((a, b) => a + b, 0) / n;
  if (n < 2) return { n, mean: round(mean), low: null, high: null, dist };
  const s = Math.sqrt(places.reduce((a, p) => a + (p - mean) ** 2, 0) / (n - 1));
  const half = Z * s / Math.sqrt(n);
  return { n, mean: round(mean), low: round(mean - half), high: round(mean + half), dist };
}

function threshold(name, fallback) {
  const raw = process.env[name];
  if (raw === undefined || raw === '') return fallback;
  const value = Number(raw);
  if (!int(value, 1, 1_000_000)) throw new Error('invalid_environment');
  return value;
}
export const thresholds = () => ({ minCell: threshold('ANALYTICS_MIN_CELL', 10), minSources: threshold('ANALYTICS_MIN_SOURCES', 5) });

/**
 * C4: one BG solo game capture is one observation; groups by build, newest first, unknown build last.
 * Sources are installations (owner = hash of the install id, stable across generations; LEGAL L-m1). Every published
 * number needs minSources installations behind it (LEGAL L-M1): the row needs them among those offered the hero,
 * picked/pickRate among those who picked it, placement/top4/first among picks with a result. Below that the value is
 * null (no counts, no distribution) and the row is small_sample.
 */
export function buildBgHeroes(db, { today, minCell, minSources }) {
  const fromDay = today - WINDOW_DAYS;
  const games = db.prepare(`SELECT capture,owner,build,outcome_ok,placement FROM source_fact_game
    WHERE mode='bg' AND offers_ok=1 AND day BETWEEN ? AND ?`).all(fromDay, today);
  const offers = db.prepare(`SELECT o.capture,o.card,o.picked FROM source_fact_offer o JOIN source_fact_game g ON g.capture=o.capture
    WHERE g.mode='bg' AND g.offers_ok=1 AND g.day BETWEEN ? AND ? AND o.kind='bg_hero'`).all(fromDay, today);
  const byCapture = new Map(games.map(g => [g.capture, { ...g, options: new Set(), picked: null }]));
  for (const o of offers) {
    const g = byCapture.get(o.capture);
    g.options.add(o.card);
    if (o.picked) g.picked = o.card;
  }
  const groups = new Map();
  for (const g of byCapture.values()) {
    const key = g.build ?? null;
    if (!groups.has(key)) groups.set(key, { build: key, captures: 0, owners: new Set(), heroes: new Map() });
    const group = groups.get(key);
    group.captures++;
    group.owners.add(g.owner);
    for (const hero of g.options) {
      if (!group.heroes.has(hero)) group.heroes.set(hero, { offered: 0, picked: 0, offeredTo: new Set(), pickedBy: new Set(), placedBy: new Set(), places: [] });
      const h = group.heroes.get(hero);
      h.offered++;
      h.offeredTo.add(g.owner);
      if (g.picked === hero) {
        h.picked++;
        h.pickedBy.add(g.owner);
        if (g.outcome_ok && int(g.placement, 1, 8)) { h.places.push(g.placement); h.placedBy.add(g.owner); }
      }
    }
  }
  const ordered = [...groups.values()].sort((a, b) => (a.build === null) - (b.build === null) || (b.build ?? 0) - (a.build ?? 0));
  return {
    period: { fromDay, toDay: today, days: WINDOW_DAYS },
    groups: ordered.map(group => {
      let suppressed = 0;
      const heroes = [];
      for (const [hero, h] of group.heroes) {
        if (h.offeredTo.size < minSources) { suppressed++; continue; }
        const n = h.places.length;
        const picks = h.pickedBy.size >= minSources;
        const places = h.placedBy.size >= minSources;
        const row = {
          hero, offered: h.offered, picked: picks ? h.picked : null,
          pickRate: picks ? rate(h.picked, h.offered, minCell) : null,
          placement: places ? placement(h.places, minCell) : null,
          top4: places ? rate(h.places.filter(p => p <= 4).length, n, minCell) : null,
          first: places ? rate(h.places.filter(p => p === 1).length, n, minCell) : null,
        };
        const hidden = [row.pickRate?.value, row.placement?.mean, row.top4?.value, row.first?.value].some(v => v == null);
        heroes.push({ ...row, quality: hidden ? 'small_sample' : 'ok' });
      }
      heroes.sort((a, b) => b.offered - a.offered || (a.hero < b.hero ? -1 : a.hero > b.hero ? 1 : 0));
      return { build: group.build, captures: group.captures, sources: group.owners.size, suppressed, heroes };
    }),
  };
}

/** Rebuilds and publishes in the caller's transaction; a failure leaves the previous row untouched. */
export function publishBgHeroes(db, today, now = Date.now()) {
  const body = JSON.stringify(buildBgHeroes(db, { today, ...thresholds() }));
  db.prepare(`INSERT INTO analytics_snap(key,rules_version,built,stale,body) VALUES(?,?,?,0,?)
    ON CONFLICT(key) DO UPDATE SET rules_version=excluded.rules_version,built=excluded.built,stale=0,body=excluded.body`).run(BG_HEROES_KEY, BG_HEROES_RULES, now, body);
}

const head = mode => ({ v: 2, metric: BG_HEROES_METRIC, rulesVersion: BG_HEROES_RULES, mode });
const unavailable = (mode, reason) => ({ ...head(mode), available: false, reason, published: null, stale: false, source: 'source_capture', period: null, groups: [] });

/** GET /v2/analytics/bg/heroes. Throws { status: 400 } for a query outside the contract. */
export function bgHeroesResponse(db, searchParams) {
  const keys = [...searchParams.keys()];
  if (keys.some(k => k !== 'mode') || keys.length > 1) return { status: 400, body: { ok: false, error: 'invalid_query' } };
  const mode = searchParams.get('mode') ?? 'solo';
  if (mode === 'duos') return { status: 200, body: unavailable('duos', 'unsupported_mode') };
  if (mode !== 'solo') return { status: 400, body: { ok: false, error: 'invalid_query' } };
  const row = db.prepare('SELECT built,stale,body FROM analytics_snap WHERE key=?').get(BG_HEROES_KEY);
  if (!row) return { status: 200, body: unavailable('solo', 'not_published') };
  if (row.body === '') return { status: 200, body: unavailable('solo', 'rebuilding') };
  const snap = JSON.parse(row.body);
  return { status: 200, body: { ...head('solo'), available: true, reason: null, published: row.built, stale: row.stale === 1,
    source: 'source_capture', period: snap.period, groups: snap.groups } };
}
