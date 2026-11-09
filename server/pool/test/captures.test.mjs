import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import path from 'node:path';
import { gzipSync } from 'node:zlib';
import test from 'node:test';
import { ID, record, start, end, choice, jsonl, source, chunk, manifest, setup, workerConfig } from './capture-fixture.mjs';
import { identity, MiB, sha } from '../node/validation.mjs';
import { atomicWrite } from '../node/archive.mjs';

const generationId = { v: 1, iid: ID.iid, generation: ID.generation };
const count = (pool, table) => pool.store.sqlite.prepare(`SELECT COUNT(*) AS n FROM ${table}`).get().n;

test('unknown generation deletion has a durable budget while known and repeated deletion remain available', async t => {
  const f = await setup(t, { archive: { maxNewRevocationsPerDay: 1 } });
  await f.upload();
  const unknown = { v: 1, iid: 'a'.repeat(32), generation: 'b'.repeat(32) };
  assert.equal((await f.post('forget', unknown)).status, 200);
  assert.ok(f.pool.archive.usage() > 0);
  const next = { ...unknown, generation: 'c'.repeat(32) };
  assert.equal((await f.post('forget', next)).status, 503);
  await f.restart();
  assert.equal((await f.post('forget', next)).status, 503);
  assert.equal((await f.post('forget', unknown)).status, 200);
  assert.equal((await f.post('forget', generationId)).status, 200);
  assert.equal(count(f.pool, 'source_captures'), 0);
  assert.equal(count(f.pool, 'source_revoked'), 2);
});

test('unknown tombstones cannot grow past the archive limit without uploading a source', async t => {
  const f = await setup(t, { archive: { maxArchiveBytes: 1024 } });
  let accepted = 0;
  for (let i = 0; i < 30; i++) {
    const response = await f.post('forget', { v: 1, iid: ID.iid, generation: i.toString(16).padStart(32, '0') });
    if (response.status === 200) accepted++;
    else assert.equal(response.status, 503);
  }
  assert.ok(accepted > 0 && accepted < 30);
  assert.equal(count(f.pool, 'source_revoked'), accepted);
  assert.ok(f.pool.archive.usage() <= 1024);
});
async function waitFor(predicate) {
  const deadline = Date.now() + 10000;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error('subprocess_did_not_start');
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}

test('durable receipt replay survives restart without duplicate chunks or quota', async t => {
  const f = await setup(t);
  const c = await f.upload();
  const before = f.pool.store.sqlite.prepare('SELECT * FROM source_quota').get();
  await f.restart();
  const r = await f.post('chunks', c);
  assert.equal(r.status, 200);
  assert.deepEqual(await r.json(), { ok: true, stored: true, duplicate: true, index: 0, sha256: c.sha256 });
  assert.equal(count(f.pool, 'source_chunks'), 1);
  assert.deepEqual(f.pool.store.sqlite.prepare('SELECT * FROM source_quota').get(), before);
  const complete = await f.post('complete', manifest([c]));
  assert.equal(complete.status, 202);
  await f.restart();
  assert.deepEqual(await (await f.post('status', ID)).json(), { ok: true, status: 'stored', captureId: ID.captureId, parserVersion: null, records: 3, unsupported: 0, gaps: 0 });
  assert.equal(await f.pool.archive.tick(), false, 'no configured worker never means parsed');
});

test('concurrent identical and conflicting intake preserves one authoritative receipt', async t => {
  const f = await setup(t);
  const c = chunk();
  const answers = await Promise.all([f.post('chunks', c), f.post('chunks', c)]);
  assert.deepEqual(answers.map(r => r.status).sort(), [200, 201]);
  const other = chunk(jsonl([start(), end(1)]));
  const conflict = await f.post('chunks', other);
  assert.equal(conflict.status, 409);
  assert.equal((await conflict.json()).error, 'chunk_conflict');
  assert.equal(count(f.pool, 'source_chunks'), 1);
});

test('manifest checks missing mismatched extra and corrupted physical chunks', async t => {
  const f = await setup(t);
  const c = chunk();
  assert.equal((await f.post('complete', manifest([c]))).status, 409);
  await f.upload();
  const wrong = { ...c, sha256: 'f'.repeat(64) };
  const mismatch = await f.post('complete', manifest([wrong]));
  assert.deepEqual(await mismatch.json(), { ok: false, error: 'missing_chunks', missing: [0] });
  fs.writeFileSync(f.pool.archive.chunkPath(identity(ID), 0), 'broken');
  assert.equal((await f.post('complete', manifest([c]))).status, 409);
  assert.equal((await f.post('chunks', c)).status, 200, 'retry repairs a lost durable file');
  const extra = chunk(jsonl([end(3)]), 1);
  assert.equal((await f.post('chunks', extra)).status, 201);
  assert.equal((await f.post('complete', manifest([c]))).status, 409, 'manifest cannot omit an already stored chunk');
  assert.equal(f.pool.archive.row(identity(ID).key).manifest, null);
});

test('manifest validates cross-chunk sequence start end metadata and immutable sealing', async t => {
  const f = await setup(t);
  const c0 = chunk(jsonl([start(), choice()]));
  const c1 = chunk(jsonl([end()]), 1);
  for (const c of [c1, c0]) assert.equal((await f.post('chunks', c)).status, 201, 'out-of-order upload is supported');
  assert.equal((await f.post('complete', manifest([c0, c1], { mode: 'arena' }))).status, 400);
  assert.equal((await f.post('complete', manifest([c0, c1]))).status, 202);
  assert.equal((await f.post('complete', manifest([c0, c1]))).status, 202, 'manifest replay is idempotent');
  assert.equal((await f.post('complete', manifest([c0, c1], { captureStatus: 'incomplete' }))).status, 409);
  assert.equal((await f.post('chunks', chunk(jsonl([end(4)]), 2))).status, 409);
});

for (const [name, rows, overrides] of [
  ['sequence gap', [start(), choice(2), end(3)], {}],
  ['missing start', [choice(0), end(1)], {}],
  ['missing end', [start(), choice()], {}],
  ['post-game reveal', [start(), end(1), choice(2)], {}],
  ['duplicate start', [start(), { ...start(), seq: 1 }, end()], {}],
  ['false complete', [start(), end(1, { status: 'incomplete', result: null })], {}],
  ['gap mismatch', [start(), record(1, 'gap', { reason: 'limit', count: 2 }), end(2, { status: 'incomplete', result: null })], { captureStatus: 'incomplete', gaps: 1 }],
  ['unknown action completeness', [start(), record(1, 'action', { action: 'unknown', cost: null }, { side: 'local' }), end()], {}],
  ['concealed reference linkage', [start(), record(1, 'entity', { zone: 'HAND', position: 1, tags: {} }, { side: 'opponent', entity: 7 }), record(2, 'entity', { zone: 'PLAY', position: 1, tags: {} }, { side: 'opponent', entity: 7, card: 'PUBLIC_CARD' }), end(3)], {}],
]) test(`whole stream rejects ${name}`, async t => {
  const f = await setup(t);
  const result = await f.complete(jsonl(rows), overrides);
  assert.equal(result.status, 400);
  assert.equal(f.pool.archive.row(identity(ID).key).manifest, null);
});

test('incomplete and independent draft captures persist without invented game results', async t => {
  const f = await setup(t);
  const rows = [start({ captureKind: 'draft', mode: 'arena' }), record(1, 'draft_choice', {
    pick: 0, options: [{ card: 'A', package: ['X', 'X'] }, { card: 'B', package: [] }], picked: 1, underground: null,
  }, { side: 'local' }), record(2, 'gap', { reason: 'shutdown', count: 1 })];
  const result = await f.complete(jsonl(rows), { captureKind: 'draft', mode: 'arena', captureStatus: 'incomplete', gaps: 1 });
  assert.equal(result.status, 202);
  const status = await (await f.post('status', ID)).json();
  assert.equal(status.gaps, 1);
  assert.equal(status.records, 3);
  const invalid = { ...ID, captureId: 'a'.repeat(32) };
  assert.equal((await f.complete(jsonl([start({ captureKind: 'draft' }), end(1)]), { captureKind: 'draft' }, invalid)).status, 400);
});

for (const [name, change] of [
  ['extra raw field', r => ({ ...r, raw: 'SyntheticPrivateMarker' })],
  ['unknown kind', r => ({ ...r, kind: 'raw_log' })],
  ['unknown data field', r => ({ ...r, data: { ...r.data, playerName: 'SyntheticPrivateMarker' } })],
  ['hidden opponent hand', () => record(1, 'entity', { zone: 'HAND', position: 1, tags: {} }, { side: 'opponent', card: 'SECRET_CARD' })],
  ['hidden snapshot deck', () => record(1, 'snapshot', { gold: null, tier: null, cards: [{ entity: 1, side: 'opponent', zone: 'DECK', card: 'SECRET_CARD', position: 0, tags: {} }] })],
  ['hidden numeric tags', () => record(1, 'entity', { zone: 'HAND', position: 1, tags: { ATK: 99 } }, { side: 'opponent' })],
  ['opponent shop', () => record(1, 'entity', { zone: 'SHOP', position: 1, tags: {} }, { side: 'opponent', card: 'SECRET_CARD' })],
  ['nonpublic partner zone', () => record(1, 'entity', { zone: 'HAND', position: 1, tags: {} }, { side: 'partner_public' })],
  ['raw string tag', () => record(1, 'tag', { tag: 'CREATOR_DBID', value: 42 }, { side: 'local' })],
  ['opponent choices', r => ({ ...r, side: 'opponent' })],
  ['hidden opponent draw', () => record(1, 'action', { action: 'draw', cost: null }, { side: 'opponent', card: 'SECRET_CARD' })],
  ['malformed snapshot', () => record(1, 'snapshot', { gold: 0, tier: 1, cards: [null] })],
]) test(`strict intake rejects ${name} before archival`, async t => {
  const f = await setup(t);
  const response = await f.post('chunks', chunk(jsonl([change(choice())])));
  assert.equal(response.status, 400);
  assert.equal(count(f.pool, 'source_chunks'), 0);
  assert.deepEqual(fs.readdirSync(f.pool.archive.captureRoot), []);
});

test('shadowed duplicate keys invalid UTF8 BOM CRLF and partial lines are rejected before archival', async t => {
  const f = await setup(t);
  const safe = JSON.stringify(choice());
  const variants = [
    Buffer.from(safe.replace('"data":', '"data":{"playerName":"SyntheticPrivateMarker"},"data":') + '\n'),
    Buffer.from(safe.replace('"choiceId":7', '"choiceId":7,"choiceId":8') + '\n'),
    Buffer.from([255, 10]), Buffer.concat([Buffer.from([239, 187, 191]), source()]),
    Buffer.from(source().toString().replaceAll('\n', '\r\n')), Buffer.from(safe),
  ];
  for (const bytes of variants) assert.equal((await f.post('chunks', chunk(bytes))).status, 400);
  assert.equal(count(f.pool, 'source_chunks'), 0);
});

test('bounded decompression checksum gzip base64 and hard chunk limits return deterministic errors', async t => {
  const f = await setup(t);
  const c = chunk();
  const bomb = gzipSync(Buffer.alloc(4 * MiB + 1, 65)).toString('base64');
  for (const [changes, expected] of [
    [{ data: bomb }, 413], [{ sha256: 'f'.repeat(64) }, 400], [{ uncompressedBytes: c.uncompressedBytes + 1 }, 400],
    [{ data: Buffer.from('not gzip').toString('base64') }, 400], [{ data: 'AAAA!' }, 400],
    [{ data: 'A'.repeat(Math.ceil(4 * MiB / 3) * 4 + 4) }, 413], [{ index: 64 }, 413], [{ uncompressedBytes: 4 * MiB + 1 }, 413],
    [{ iid: [ID.iid] }, 400], [{ input: '/tmp/arbitrary' }, 400],
  ]) assert.equal((await f.post('chunks', { ...c, ...changes })).status, expected);
  const longLine = Buffer.from(' '.repeat(65536) + JSON.stringify(start()) + '\n');
  assert.equal((await f.post('chunks', chunk(longLine))).status, 413);
  assert.equal(count(f.pool, 'source_chunks'), 0);
});

test('atomic archive cap and daily quotas reject concurrent intake without partial receipts', async t => {
  const bytes = source();
  const compressed = Buffer.from(chunk(bytes).data, 'base64').length;
  const f = await setup(t, { archive: { maxArchiveBytes: bytes.length + compressed, dailyBytes: bytes.length * 10 } });
  const other = { ...ID, captureId: '4'.repeat(32) };
  const responses = await Promise.all([f.post('chunks', chunk(bytes)), f.post('chunks', chunk(bytes, 0, other))]);
  assert.deepEqual(responses.map(r => r.status).sort(), [201, 503]);
  assert.equal(count(f.pool, 'source_chunks'), 1);
  const denied = responses.find(r => r.status === 503);
  assert.equal(denied.headers.get('retry-after'), '30');
  const quota = await setup(t, { archive: { dailyBytes: bytes.length, dailyCaptures: 1 } });
  await quota.upload();
  assert.equal((await quota.post('chunks', chunk(bytes, 0, other))).status, 503);
  await quota.restart();
  assert.equal((await quota.post('chunks', chunk(bytes, 0, other))).status, 503);
});

test('disk pressure gives retryable failure and no success receipt', async t => {
  const f = await setup(t, { archive: { minFreeBytes: Number.MAX_SAFE_INTEGER } });
  const response = await f.post('chunks', chunk());
  assert.equal(response.status, 503);
  assert.equal((await response.json()).error, 'disk_pressure');
  assert.equal(count(f.pool, 'source_chunks'), 0);
});

test('rename failure and orphan file recovery never acknowledge an unwritten chunk', async t => {
  const f = await setup(t);
  const id = identity(ID);
  fs.mkdirSync(f.pool.archive.chunkPath(id, 0), { recursive: true });
  const failed = await f.post('chunks', chunk());
  assert.equal(failed.status, 503);
  assert.equal(count(f.pool, 'source_chunks'), 0);
  await f.restart();
  assert.equal(fs.existsSync(f.pool.archive.dir(id)), false, 'orphan from pre-receipt write is removed');
  await f.upload();
  fs.writeFileSync(path.join(f.pool.archive.dir(id), 'uncommitted.tmp'), 'synthetic');
  await f.restart();
  assert.deepEqual(fs.readdirSync(f.pool.archive.dir(id)), ['0.jsonl.gz']);
});

test('atomic file writer preserves previous file when rename fails', t => {
  const parent = fs.mkdtempSync(path.join(process.env.TEMP ?? '/tmp', 'hp-atomic-'));
  t.after(() => fs.rmSync(parent, { recursive: true, force: true }));
  const target = path.join(parent, 'target');
  fs.mkdirSync(target);
  assert.throws(() => atomicWrite(target, Buffer.from('synthetic')));
  assert.deepEqual(fs.readdirSync(parent), ['target']);
});

test('forget revokes durably deletes every artifact and rejects all late operations', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('v1') } });
  assert.equal((await f.complete()).status, 202);
  await f.pool.archive.tick();
  assert.equal((await (await f.post('status', ID)).json()).status, 'parsed');
  assert.equal((await f.post('forget', generationId)).status, 200);
  assert.equal(count(f.pool, 'source_captures'), 0);
  assert.equal(count(f.pool, 'source_chunks'), 0);
  assert.deepEqual(fs.readdirSync(f.pool.archive.captureRoot), []);
  assert.equal((await f.post('chunks', chunk())).status, 410);
  assert.equal((await f.post('complete', manifest())).status, 410);
  assert.equal((await (await f.post('status', ID)).json()).status, 'deleted');
  assert.equal((await f.post('forget', generationId)).status, 200);
  await f.restart();
  assert.equal((await f.post('chunks', chunk())).status, 410);
  const newId = { ...ID, generation: 'a'.repeat(32) };
  assert.equal((await f.post('chunks', chunk(source(), 0, newId))).status, 201, 'a new generation remains independent');
});

test('revocation ledger wins over a restored older SQLite backup', async t => {
  const f = await setup(t);
  await f.complete();
  const backup = path.join(f.dataDir, 'old.sqlite');
  f.pool.store.sqlite.prepare('VACUUM INTO ?').run(backup);
  await f.post('forget', generationId);
  await f.pool.close();
  fs.copyFileSync(backup, path.join(f.dataDir, 'pool.sqlite'));
  await f.restart();
  assert.equal(count(f.pool, 'source_captures'), 0);
  assert.equal((await f.post('chunks', chunk())).status, 410);
});

test('worker process writes versioned results and reprocess replaces one contribution', async t => {
  const f = await setup(t, { operatorToken: 'test-operator', archive: { worker: workerConfig('v1') } });
  await f.complete();
  assert.equal(await f.pool.archive.tick(), true);
  let row = f.pool.archive.row(identity(ID).key);
  assert.equal(row.status, 'parsed');
  assert.equal(row.parser_version, 'fixture-v1');
  assert.equal(JSON.parse(row.derived).sourceSha256, sha(source()));
  assert.equal(JSON.parse(row.derived).summary.choices, 1);
  assert.equal((await f.post('reprocess', ID)).status, 403);
  assert.equal((await f.post('reprocess', ID, { authorization: 'Bearer test-operator' })).status, 202);
  await f.pool.archive.tick();
  assert.equal(count(f.pool, 'source_captures'), 1);
  f.pool.archive.worker = workerConfig('v2');
  await f.post('reprocess', ID, { authorization: 'Bearer test-operator' });
  await f.pool.archive.tick();
  row = f.pool.archive.row(identity(ID).key);
  assert.equal(row.parser_version, 'fixture-v2');
  assert.equal(JSON.parse(row.derived).summary.choices, 1);
  await f.restart();
  assert.equal((await (await f.post('status', ID)).json()).parserVersion, 'fixture-v2');
  assert.equal((await f.post('reprocess', { ...ID, command: 'anything', input: '/tmp/raw' }, { authorization: 'Bearer test-operator' })).status, 400);
});

for (const mode of ['crash', 'bad-hash', 'bad-count', 'private', 'noisy', 'false-summary']) test(`worker ${mode} fails safely and supports explicit recovery`, async t => {
  const f = await setup(t, { archive: { worker: workerConfig(mode), workerTimeoutMs: 2000 } });
  await f.complete();
  await f.pool.archive.tick();
  let row = f.pool.archive.row(identity(ID).key);
  assert.equal(row.status, 'failed');
  assert.equal(row.derived, null);
  assert.equal(count(f.pool, 'source_chunks'), 1);
  f.pool.archive.worker = workerConfig('v1');
  await f.pool.archive.reprocess(ID);
  await f.pool.archive.tick();
  row = f.pool.archive.row(identity(ID).key);
  assert.equal(row.status, 'parsed');
  assert.equal(row.derived.includes('SyntheticPrivateMarker'), false);
});

test('forget during a running worker kills it and late completion cannot resurrect the generation', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('wait') } });
  await f.complete();
  const task = f.pool.archive.tick();
  await waitFor(() => {
    const active = f.pool.archive.active.get(identity(ID).key);
    return active && fs.existsSync(`${active.output}.started`);
  });
  assert.equal((await (await f.post('status', ID)).json()).status, 'processing');
  assert.equal((await f.post('forget', generationId)).status, 200);
  await task;
  assert.equal(count(f.pool, 'source_captures'), 0);
  assert.deepEqual(fs.readdirSync(f.pool.archive.captureRoot), []);
  assert.equal((await f.post('chunks', chunk())).status, 410);
});

test('restart recovers processing status and incomplete source parses as partial', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('v1') } });
  const bytes = jsonl([start(), choice(), record(2, 'gap', { reason: 'unsupported', count: 1 }, { turn: 2 })]);
  await f.complete(bytes, { captureStatus: 'incomplete', gaps: 1 });
  f.pool.store.sqlite.prepare("UPDATE source_captures SET status='processing'").run();
  await f.restart();
  assert.equal((await (await f.post('status', ID)).json()).status, 'stored');
  await f.pool.archive.tick();
  const status = await (await f.post('status', ID)).json();
  assert.equal(status.status, 'partial');
  assert.equal(status.gaps, 1);
});

test('worker timeout and missing executable persist failure while retaining the source', async t => {
  for (const worker of [workerConfig('wait'), { executable: path.join(process.env.TEMP ?? '/tmp', 'hp-missing-executable'), args: [] }]) {
    const f = await setup(t, { archive: { worker, workerTimeoutMs: 100 } });
    await f.complete();
    await f.pool.archive.tick();
    assert.equal((await (await f.post('status', ID)).json()).status, 'failed');
    assert.equal(count(f.pool, 'source_chunks'), 1);
  }
});

test('retention expires unfinished and complete archives with persistent capture tombstones', async t => {
  const f = await setup(t, { archive: { retentionDays: 1, unfinishedHours: 1 } });
  await f.complete();
  const unfinished = { ...ID, captureId: '4'.repeat(32) };
  await f.upload(source(), unfinished);
  await f.pool.archive.maintain(Date.now() + 2 * 3600000);
  assert.equal((await (await f.post('status', unfinished)).json()).status, 'deleted');
  assert.equal((await (await f.post('status', ID)).json()).status, 'stored');
  await f.pool.archive.maintain(Date.now() + 2 * 86400000);
  assert.equal((await f.post('chunks', chunk())).status, 410);
  assert.equal(count(f.pool, 'source_chunks'), 0);
  await f.restart();
  assert.equal((await (await f.post('status', ID)).json()).status, 'deleted');
});

test('pending physical deletion returns 202 remains revoked and recovers on restart', async t => {
  const f = await setup(t);
  await f.complete();
  const remove = f.pool.archive.removeDirectory;
  f.pool.archive.removeDirectory = () => { throw new Error('synthetic_unlink_failure'); };
  const pending = await f.post('forget', generationId);
  assert.equal(pending.status, 202);
  assert.deepEqual(await pending.json(), { ok: true, status: 'deleting' });
  assert.equal(fs.existsSync(f.pool.archive.dir(identity(ID))), true, 'failure did not pretend the file was gone');
  assert.equal(count(f.pool, 'source_captures'), 0, 'facts removed despite pending filesystem cleanup');
  assert.equal((await f.post('chunks', chunk())).status, 410);
  f.pool.archive.removeDirectory = remove;
  await f.restart();
  assert.equal(fs.existsSync(f.pool.archive.dir(identity(ID))), false);
  assert.equal((await f.post('forget', generationId)).status, 200);
});

test('concurrent upload complete reprocess and forget converge to durable deletion', async t => {
  const f = await setup(t, { operatorToken: 'operator-test' });
  await f.complete();
  const responses = await Promise.all([
    f.post('chunks', chunk()), f.post('complete', manifest()),
    f.post('reprocess', ID, { authorization: 'Bearer operator-test' }), f.post('forget', generationId),
  ]);
  assert.equal(responses[3].status, 200);
  assert.ok([200, 410].includes(responses[0].status));
  assert.ok([202, 410].includes(responses[1].status));
  assert.ok([202, 410].includes(responses[2].status));
  await f.restart();
  assert.equal(count(f.pool, 'source_chunks'), 0);
  assert.equal((await f.post('reprocess', ID, { authorization: 'Bearer operator-test' })).status, 410);
});

test('SQL receipt failure rolls back quota cleans file and allows a genuine retry', async t => {
  const f = await setup(t);
  f.pool.store.sqlite.exec("CREATE TRIGGER fail_receipt BEFORE INSERT ON source_chunks BEGIN SELECT RAISE(ABORT,'synthetic'); END");
  assert.equal((await f.post('chunks', chunk())).status, 503);
  assert.equal(count(f.pool, 'source_chunks'), 0);
  assert.equal(count(f.pool, 'source_captures'), 0);
  assert.equal(count(f.pool, 'source_quota'), 0);
  assert.equal(fs.existsSync(f.pool.archive.chunkPath(identity(ID), 0)), false);
  f.pool.store.sqlite.exec('DROP TRIGGER fail_receipt');
  assert.equal((await f.post('chunks', chunk())).status, 201);
});

test('durable revoke survives a failed SQL tombstone insert in the running process', async t => {
  const f = await setup(t);
  await f.complete();
  f.pool.store.sqlite.exec("CREATE TRIGGER fail_revoke BEFORE INSERT ON source_revoked BEGIN SELECT RAISE(ABORT,'synthetic'); END");
  assert.equal((await f.post('forget', generationId)).status, 503);
  assert.equal((await f.post('chunks', chunk())).status, 410, 'fsynced ledger already revoked the generation');
  f.pool.store.sqlite.exec('DROP TRIGGER fail_revoke');
  await f.restart();
  assert.equal(count(f.pool, 'source_captures'), 0);
  assert.equal((await f.post('forget', generationId)).status, 200);
});

test('torn unacknowledged ledger append is truncated without losing earlier revocations', async t => {
  const f = await setup(t);
  await f.post('forget', generationId);
  fs.appendFileSync(f.pool.archive.ledger, '{"generation":');
  await f.restart();
  assert.equal((await f.post('chunks', chunk())).status, 410);
  const text = fs.readFileSync(f.pool.archive.ledger, 'utf8');
  assert.equal(text.split('\n').filter(Boolean).length, 1);
  assert.equal(JSON.parse(text).generation, identity(ID).generation);
});

test('corrupt source after manifest fails the worker without fabricating empty results', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('v1') } });
  await f.complete();
  fs.writeFileSync(f.pool.archive.chunkPath(identity(ID), 0), 'broken');
  await f.pool.archive.tick();
  assert.equal((await (await f.post('status', ID)).json()).status, 'failed');
  assert.equal(f.pool.archive.row(identity(ID).key).derived, null);
  assert.equal((await f.post('chunks', chunk())).status, 200);
  await f.pool.archive.reprocess(ID);
  await f.pool.archive.tick();
  assert.equal((await (await f.post('status', ID)).json()).status, 'parsed');
});

test('sixty-four ordered chunks form one whole stream and a sixty-fifth is refused', async t => {
  const f = await setup(t);
  const chunks = [];
  for (let i = 0; i < 64; i++) {
    const row = i === 0 ? start() : i === 63 ? end(63) : record(i, 'tag', { tag: 'TURN', value: i });
    const c = chunk(jsonl([row]), i);
    chunks.push(c);
    assert.equal((await f.post('chunks', c)).status, 201);
  }
  assert.equal((await f.post('complete', manifest(chunks))).status, 202);
  assert.equal((await (await f.post('status', ID)).json()).records, 64);
  assert.equal((await f.post('complete', manifest([...chunks, { ...chunks[0], index: 64 }]))).status, 413);
});

test('bounded worker output rejects oversized files without storing derived data', async t => {
  const f = await setup(t, { archive: { worker: workerConfig('v1'), maxWorkerOutputBytes: 64 } });
  await f.complete();
  await f.pool.archive.tick();
  assert.equal((await (await f.post('status', ID)).json()).status, 'failed');
  assert.equal(f.pool.archive.row(identity(ID).key).derived, null);
  assert.equal(count(f.pool, 'source_chunks'), 1);
});

test('public opponent resource and card tags remain usable while unobserved hidden tags fail completion', async t => {
  const f = await setup(t);
  const publicRows = [start(), record(1, 'tag', { tag: 'RESOURCES', value: 5 }, { side: 'opponent' }),
    record(2, 'entity', { zone: 'PLAY', position: 1, tags: { ATK: 2 } }, { side: 'opponent', entity: 7, card: 'PUBLIC_CARD' }),
    record(3, 'tag', { tag: 'ATK', value: 3 }, { side: 'opponent', entity: 7 }), end(4)];
  assert.equal((await f.complete(jsonl(publicRows))).status, 202);
  const hiddenId = { ...ID, captureId: 'a'.repeat(32) };
  const hiddenRows = [start(), record(1, 'entity', { zone: 'HAND', position: 1, tags: {} }, { side: 'opponent', entity: 7 }),
    record(2, 'tag', { tag: 'ATK', value: 99 }, { side: 'opponent', entity: 7 }), end(3)];
  assert.equal((await f.complete(jsonl(hiddenRows), {}, hiddenId)).status, 400);
  assert.equal(f.pool.archive.row(identity(hiddenId).key).manifest, null);
});
