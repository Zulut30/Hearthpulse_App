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
  fs.writeFileSync(`${output}.tmp`, JSON.stringify(result));
  fs.renameSync(`${output}.tmp`, output);
  console.log(JSON.stringify({ ok: true, status: result.status }));
}
