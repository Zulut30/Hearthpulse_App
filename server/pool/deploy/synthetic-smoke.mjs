// Explicit operator command. Only generated typed facts; never opens a game log or personal data folder.
import { randomBytes, createHash } from 'node:crypto';
import { gzipSync } from 'node:zlib';
const endpoint = process.argv[2];
const url = new URL(endpoint);
url.pathname = `${url.pathname.replace(/\/+$/, '')}/`;
if (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname))) throw new Error('HTTPS or loopback required');
const hex = () => randomBytes(16).toString('hex');
const id = { v: 1, iid: hex(), generation: hex(), captureId: hex() };
const generation = { v: 1, iid: id.iid, generation: id.generation };
const day = Math.floor(Date.now() / 86400000);
const record = (seq, kind, data, extra = {}) => ({ v: 1, seq, kind, turn: 0, side: 'none', entity: 0, card: null, target: 0, data, ...extra });
const rows = [
  record(0, 'start', { captureKind: 'game', mode: 'ranked', day, build: null }),
  record(1, 'choice', { choiceId: 1, sourceCard: null, options: ['A', 'B', 'C'], picked: ['B'], resolved: true }, { side: 'local', turn: 2 }),
  record(2, 'end', { status: 'complete', result: 'W', placement: null, reason: 'normal' }, { turn: 2 }),
];
const bytes = Buffer.from(rows.map(JSON.stringify).join('\n') + '\n');
const sha256 = createHash('sha256').update(bytes).digest('hex');
async function post(route, body, operator = false) {
  const headers = { 'content-type': 'application/json' };
  if (operator) headers.authorization = `Bearer ${process.env.POOL_OPERATOR_TOKEN ?? ''}`;
  const response = await fetch(new URL(`v1/captures/${route}`, url), { method: 'POST', headers, body: JSON.stringify(body), signal: AbortSignal.timeout(150000) });
  const result = await response.json();
  if (!response.ok) throw new Error(`smoke_${route}_${response.status}`);
  return { code: response.status, result };
}
async function waitParsed() {
  for (let i = 0; i < 120; i++) {
    const { result } = await post('status', id);
    if (['parsed', 'partial', 'failed'].includes(result.status)) {
      if (result.status !== 'parsed' || result.records !== 3 || result.gaps !== 0) throw new Error('smoke_parse_failed');
      return result;
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error('smoke_worker_not_ready');
}
let forgotten = false;
try {
  const receipt = await post('chunks', { ...id, index: 0, sha256, encoding: 'gzip', uncompressedBytes: bytes.length, data: gzipSync(bytes).toString('base64') });
  if (receipt.result.index !== 0 || receipt.result.sha256 !== sha256 || !receipt.result.stored) throw new Error('smoke_bad_receipt');
  await post('complete', { ...id, captureKind: 'game', mode: 'ranked', day, sourceVersion: 1, captureStatus: 'complete', chunks: [{ index: 0, sha256, uncompressedBytes: bytes.length }], gaps: 0 });
  const parsed = await waitParsed();
  // Reprocess only through the direct loopback origin, with a separately configured operator secret.
  if (process.env.POOL_OPERATOR_TOKEN) { await post('reprocess', id, true); await waitParsed(); }
  const deletion = await post('forget', generation);
  forgotten = deletion.code === 200 && deletion.result.status === 'deleted';
  if (!forgotten) throw new Error('smoke_deletion_pending');
  if ((await post('status', id)).result.status !== 'deleted') throw new Error('smoke_stale_status');
  console.log(JSON.stringify({ ok: true, status: 'synthetic_deleted', parserVersion: parsed.parserVersion, records: parsed.records }));
} finally {
  if (!forgotten) {
    const deletion = await post('forget', generation);
    if (deletion.code !== 200 || deletion.result.status !== 'deleted') console.error(JSON.stringify({ ok: false, error: 'synthetic_deletion_pending' }));
  }
}
