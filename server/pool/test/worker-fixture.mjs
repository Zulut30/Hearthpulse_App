// A deterministic subprocess test double. Production never imports or configures this file.
import * as fs from 'node:fs';
import { createHash } from 'node:crypto';
const mode = process.argv[2];
const input = process.argv[process.argv.indexOf('--input') + 1];
const output = process.argv[process.argv.indexOf('--output') + 1];
if (mode === 'crash') process.exit(7);
if (mode === 'noisy') { process.stdout.write('x'.repeat(32768)); setInterval(() => {}, 1000); }
else if (mode === 'wait') { fs.writeFileSync(`${output}.started`, 'ready'); setInterval(() => {}, 1000); }
else {
  const bytes = fs.readFileSync(input);
  const rows = bytes.toString('utf8').trimEnd().split('\n').map(JSON.parse);
  const choices = rows.filter(r => r.kind === 'choice');
  const gaps = rows.filter(r => r.kind === 'gap').reduce((n, r) => n + r.data.count, 0);
  const result = { v: 1, parserVersion: mode === 'v2' ? 'fixture-v2' : 'fixture-v1',
    sourceSha256: createHash('sha256').update(bytes).digest('hex'), status: gaps ? 'partial' : 'parsed', records: rows.length, unsupported: 0, gaps,
    events: choices.map(r => ({ seq: r.seq, kind: 'choice', turn: r.turn, card: r.card, data: {} })),
    summary: { plays: 0, buys: 0, sells: 0, choices: choices.length, turns: 2 } };
  if (mode === 'bad-hash') result.sourceSha256 = 'a'.repeat(64);
  if (mode === 'private') result.events[0].data = { playerName: 'SyntheticPrivateMarker' };
  if (mode === 'bad-count') result.records = 0;
  if (mode === 'false-summary') Object.assign(result.summary, { shopSnapshots: 0, resources: 0, draftChoices: 0,
    captureKind: 'game', mode: 'ranked', captureStatus: 'complete', hasEnd: true, completeForAggregation: true, result: 'L', placement: null });
  if (mode.startsWith('result-v2') || mode.startsWith('lie-')) Object.assign(result, resultV2(rows, result, mode));
  const write = () => {
    fs.writeFileSync(`${output}.tmp`, JSON.stringify(result));
    fs.renameSync(`${output}.tmp`, output);
    console.log(JSON.stringify({ ok: true, status: result.status }));
  };
  if (mode === 'result-v2-slow') setTimeout(write, 3000); else write();
}

// Result v2 per contract C2 of TASK-2026-11-09, written independently of the server's checks; "lie-*" modes then
// falsify one claim each, as a faulty worker would.
function resultV2(rows, base, mode) {
  const start = rows[0].data;
  const end = rows.findLast(r => r.kind === 'end')?.data ?? null;
  const v1 = rows[0].v === 1;
  const bg = ['bg', 'duos'].includes(start.mode);
  const complete = base.gaps === 0 && end?.status === 'complete';
  const summary = { ...base.summary, shopSnapshots: 0, resources: 0, draftChoices: rows.filter(r => r.kind === 'draft_choice').length,
    captureKind: start.captureKind, mode: start.mode, captureStatus: complete ? 'complete' : 'incomplete', hasEnd: !!end,
    completeForAggregation: start.captureKind === 'game' && complete && !!end,
    result: complete ? end?.result ?? null : null, placement: complete ? end?.placement ?? null : null };
  const reasons = new Set();
  let outcome = false;
  let offers = false;
  let offer = null;
  if (start.captureKind === 'draft') reasons.add('draft');
  else if (v1) {
    reasons.add('source_v1');
    outcome = !bg && summary.completeForAggregation;
  } else {
    let lifecycle = true;
    if (!end) { reasons.add('no_end'); lifecycle = false; }
    else if (end.reason !== 'normal') { reasons.add('end_reason'); lifecycle = false; }
    if (rows.some(r => r.kind === 'gap' && ['late_attach', 'rotation', 'shutdown'].includes(r.data.reason))) { reasons.add('lifecycle_gap'); lifecycle = false; }
    const value = bg ? Number.isInteger(end?.placement) && end.placement <= (start.mode === 'duos' ? 4 : 8) : ['W', 'L', 'T'].includes(end?.result);
    if (lifecycle && !value) reasons.add('no_outcome');
    outcome = lifecycle && value;
    const choiceRows = rows.filter(r => r.kind === 'choice');
    const firstId = Math.min(...choiceRows.map(r => r.data.choiceId));
    const first = choiceRows.filter(r => r.data.choiceId === firstId).at(-1);
    if (!bg || !first || first.data.choiceType !== 'mulligan') reasons.add('no_offer');
    else if (!first.data.resolved) reasons.add('offer_unresolved');
    else if (first.data.options.length < 2 || first.data.options.length > 4 || first.data.picked.length !== 1) reasons.add('offer_shape');
    else if (lifecycle) { offers = true; offer = { seq: first.seq, kind: 'bg_hero', options: first.data.options, picked: first.data.picked }; }
  }
  const order = ['source_v1', 'draft', 'no_end', 'end_reason', 'lifecycle_gap', 'no_outcome', 'no_offer', 'offer_unresolved', 'offer_shape'];
  const r = {
    v: 2, parserVersion: mode === 'result-v2-other' ? 'fixture-r2b' : 'fixture-r2', sourceVersion: rows[0].v, summary,
    context: { mode: start.mode, day: start.day, build: start.build, format: start.format ?? null, arena: start.arena ?? null },
    eligibility: { outcome, offers, reasons: order.filter(x => reasons.has(x)) },
    facts: { outcome: outcome ? { result: end.result, placement: end.placement } : null, offers: offer ? [offer] : [] },
  };
  if (mode === 'lie-placement' && r.facts.outcome) r.facts.outcome.placement = r.facts.outcome.placement === 1 ? 2 : 1;
  if (mode === 'lie-offer-seq' && offer) offer.seq -= 1;
  if (mode === 'lie-offer-options' && offer) offer.options = [...offer.options].reverse();
  if (mode === 'lie-offer-picked' && offer) offer.picked = [offer.options.find(c => c !== offer.picked[0])];
  if (mode === 'lie-outcome') { r.eligibility.outcome = true; r.facts.outcome = { result: end?.result ?? null, placement: end?.placement ?? null }; }
  if (mode === 'lie-offers') {
    const first = rows.find(c => c.kind === 'choice');
    r.eligibility.offers = true;
    r.facts.offers = [{ seq: first.seq, kind: 'bg_hero', options: first.data.options, picked: first.data.picked }];
  }
  if (mode === 'lie-context') r.context.build = (r.context.build ?? 0) + 1;
  if (mode === 'lie-source-version') r.sourceVersion = 3 - r.sourceVersion;
  return r;
}
