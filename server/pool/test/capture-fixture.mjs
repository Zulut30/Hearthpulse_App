import { gzipSync } from 'node:zlib';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { startPool } from '../node/server.mjs';
import { sha } from '../node/validation.mjs';

export const ID = { v: 1, iid: '1'.repeat(32), generation: '2'.repeat(32), captureId: '3'.repeat(32) };
export const DAY = Math.floor(Date.now() / 86_400_000);
export const record = (seq, kind, data, extra = {}) => ({ v: 1, seq, kind, turn: 0, side: 'none', entity: 0, card: null, target: 0, data, ...extra });
export const start = (extra = {}) => record(0, 'start', { captureKind: 'game', mode: 'ranked', day: DAY, build: null, ...extra });
export const end = (seq = 2, data = {}) => record(seq, 'end', { status: 'complete', result: 'W', placement: null, reason: 'normal', ...data }, { turn: 2 });
export const choice = (seq = 1) => record(seq, 'choice', { choiceId: 7, sourceCard: null, options: ['A', 'B', 'C'], picked: ['B'], resolved: true }, { turn: 2, side: 'local' });
export const jsonl = rows => Buffer.from(rows.map(r => JSON.stringify(r)).join('\n') + '\n');
export const source = () => jsonl([start(), choice(), end()]);
export function chunk(bytes = source(), index = 0, id = ID) {
  return { ...id, index, sha256: sha(bytes), encoding: 'gzip', uncompressedBytes: bytes.length, data: gzipSync(bytes).toString('base64') };
}
export function manifest(chunks = [chunk()], overrides = {}, id = ID) {
  return { ...id, captureKind: 'game', mode: 'ranked', day: DAY, sourceVersion: 1, captureStatus: 'complete',
    chunks: chunks.map(c => ({ index: c.index, sha256: c.sha256, uncompressedBytes: c.uncompressedBytes })), gaps: 0, ...overrides };
}
export const workerConfig = mode => ({ executable: process.execPath, args: [path.resolve('server/pool/test/worker-fixture.mjs'), mode] });
export async function setup(t, options = {}) {
  const dataDir = mkdtempSync(path.join(tmpdir(), 'hp-source-p2-'));
  const config = { dataDir, port: 0, schedule: false, ...options, archive: { minFreeBytes: 0, ...options.archive } };
  let pool = await startPool(config);
  t.after(async () => { await pool.close(); rmSync(dataDir, { recursive: true, force: true }); });
  return {
    get pool() { return pool; }, dataDir,
    async restart() { await pool.close(); pool = await startPool(config); return pool; },
    async post(method, body, headers = {}) {
      return fetch(`${pool.url}/v1/captures/${method}`, { method: 'POST', headers: { 'content-type': 'application/json', ...headers }, body: typeof body === 'string' ? body : JSON.stringify(body) });
    },
    async upload(bytes = source(), id = ID) {
      const c = chunk(bytes, 0, id);
      const r = await this.post('chunks', c);
      if (r.status !== 201) throw new Error(`fixture_upload_${r.status}:${await r.text()}`);
      return c;
    },
    async complete(bytes = source(), overrides = {}, id = ID) {
      const c = await this.upload(bytes, id);
      return this.post('complete', manifest([c], overrides, id));
    },
  };
}
