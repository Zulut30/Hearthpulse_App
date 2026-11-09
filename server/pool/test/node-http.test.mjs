import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { request, createServer } from 'node:http';
import { gzipSync, gunzipSync } from 'node:zlib';
import test from 'node:test';
import { DAY, ID, setup, source, chunk, manifest, workerConfig } from './capture-fixture.mjs';
import { identity, MiB } from '../node/validation.mjs';
import { openDatabase, ResponseCache } from '../node/sqlite.mjs';
import { startPool } from '../node/server.mjs';

const post = (pool, route, body) => fetch(`${pool.url}${route}`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
const event = id => ({ id: id.toString(16).padStart(32, '0'), day: DAY, mode: 'ranked', fmt: 'standard', res: 'W', turns: 2, secs: 10 });

test('Node HTTP preserves games constructed events reads and durable SQLite restart contracts', async t => {
  const f = await setup(t);
  for (const [route, body] of [
    ['/v1/games', { v: 1, gameId: 'a'.repeat(32), mode: 'solo', bracket: 6500, picked: 'BG_HERO_A', offered: ['BG_HERO_A'], placement: 2 }],
    ['/v1/constructed', { v: 1, games: [{ id: 'b'.repeat(32), day: DAY, format: 'standard', won: true, picks: [[1, 'SOURCE_CARD', ['CARD_A', 'CARD_B'], ['CARD_B']]] }] }],
    ['/v1/events', { v: 1, iid: ID.iid, games: [event(1)] }],
  ]) {
    const answer = await post(f.pool, route, body);
    assert.equal(answer.status, 200, route);
    assert.equal((await answer.json()).stored, 1);
  }
  f.pool.store.sqlite.prepare('INSERT INTO meta(seq,body) VALUES(0,?)').run('{"synthetic":true}');
  for (const [route, property, expected] of [
    ['/v1/stats?mode=solo', 'games', 1], ['/v1/heroes', 'games', 1], ['/v1/choices', 'games', 1],
    ['/v1/events/summary', 'games', 1], ['/v1/meta', 'synthetic', true],
  ]) {
    const answer = await fetch(`${f.pool.url}${route}`);
    assert.equal(answer.status, 200, route);
    assert.equal((await answer.json())[property], expected);
  }
  await f.restart();
  for (const table of ['g', 'c', 'e']) assert.equal(f.pool.store.sqlite.prepare(`SELECT COUNT(*) AS n FROM ${table}`).get().n, 1);
  assert.equal((await fetch(`${f.pool.url}/private/captures`)).status, 404);
  const health = await (await fetch(`${f.pool.url}/healthz`)).json();
  assert.equal(health.ok, true);
  assert.equal(health.workerConfigured, false);
  assert.equal(JSON.stringify(health).includes(ID.iid), false);
});

test('legacy quota reservation is atomic and rolls back with a failed insert', async t => {
  const f = await setup(t, { legacyEnv: { EVENTS_DAILY: 1 }, maxRequests: 8 });
  const results = await Promise.all([post(f.pool, '/v1/events', { v: 1, iid: ID.iid, games: [event(1)] }), post(f.pool, '/v1/events', { v: 1, iid: ID.iid, games: [event(2)] })]);
  assert.deepEqual(results.map(r => r.status).sort(), [200, 503]);
  assert.equal(f.pool.store.sqlite.prepare('SELECT n FROM equota').get().n, 1);
  assert.equal(f.pool.store.sqlite.prepare('SELECT COUNT(*) AS n FROM e').get().n, 1);
  const rollback = await setup(t, { legacyEnv: { EVENTS_DAILY: 1 } });
  rollback.pool.store.sqlite.exec("CREATE TRIGGER forced_disk_error BEFORE INSERT ON e BEGIN SELECT RAISE(ABORT, 'synthetic'); END");
  assert.equal((await post(rollback.pool, '/v1/events', { v: 1, iid: ID.iid, games: [event(1)] })).status, 503);
  assert.equal(rollback.pool.store.sqlite.prepare('SELECT COUNT(*) AS n FROM equota').get().n, 0, 'failed intake did not consume quota');
  rollback.pool.store.sqlite.exec('DROP TRIGGER forced_disk_error');
  assert.equal((await post(rollback.pool, '/v1/events', { v: 1, iid: ID.iid, games: [event(1)] })).status, 200);
});

test('events deletion insertion replacement and day rollover cannot serve stale snapshots or caches', async t => {
  const f = await setup(t);
  const read = async () => {
    const answer = await fetch(`${f.pool.url}/v1/events/summary`);
    assert.equal(answer.headers.get('cache-control'), 'no-store');
    return answer.json();
  };
  await post(f.pool, '/v1/events', { v: 1, iid: ID.iid, games: [event(1)] });
  assert.equal((await read()).games, 1);
  const deletion = await post(f.pool, '/v1/events/forget', { iid: ID.iid });
  assert.equal((await deletion.json()).deleted, 1);
  assert.equal((await read()).games, 0);
  await post(f.pool, '/v1/events', { v: 1, iid: 'f'.repeat(32), games: [event(2)] });
  assert.equal((await read()).games, 1);
  f.pool.store.sqlite.prepare("UPDATE snap SET built=?,body=? WHERE mode='events'").run(Date.now() - 86400000, '{"games":999}');
  assert.equal((await read()).games, 1, 'a previous day snapshot is rebuilt even with the same row count');
  f.pool.store.sqlite.prepare('DELETE FROM e').run();
  assert.equal((await read()).games, 0, 'direct retention or operator deletion invalidates transactionally');
  for (const [mode, route] of [['solo', '/v1/stats'], ['choices-standard', '/v1/choices']]) {
    f.pool.store.sqlite.prepare('INSERT INTO snap(mode,last_row,built,body) VALUES(?,?,?,?)').run(mode, 0, Date.now() - 86400000, '{"games":999}');
    assert.equal((await (await fetch(`${f.pool.url}${route}`)).json()).games, 0, 'legacy day-based windows also expire without new intake');
  }
});

test('source HTTP rejects caller commands malformed envelopes body overflow and forwarded operator authority', async t => {
  const f = await setup(t, { operatorToken: 'operator-test' });
  const wrongMethod = await fetch(`${f.pool.url}/v1/captures/chunks`);
  assert.equal(wrongMethod.status, 405);
  assert.equal((await f.post('chunks', '{')).status, 400);
  assert.equal((await f.post('chunks', { ...chunk(), command: 'shell', path: '/tmp/synthetic' })).status, 400);
  assert.equal((await f.post('status', { ...ID, iid: '../../etc' })).status, 400);
  assert.equal((await f.post('status', ID)).status, 404);
  assert.equal((await f.post('reprocess', ID, { 'x-forwarded-for': '127.0.0.1', 'x-operator-token': 'operator-test' })).status, 403);
  assert.equal((await f.post('reprocess', ID, { authorization: 'Bearer wrong' })).status, 403);
  assert.equal((await f.post('complete', ' '.repeat(65537))).status, 413);
  assert.equal((await f.post('chunks', ' '.repeat(6 * MiB + 1))).status, 413);
  assert.equal((await f.post('chunks', '{"v":1,"v":1}')).status, 400);
  assert.equal((await f.post('chunks', chunk(), { 'content-encoding': 'gzip' })).status, 400);
  const deep = '['.repeat(100) + '0' + ']'.repeat(100);
  assert.equal((await f.post('chunks', deep)).status, 400);
});

test('streamed HTTP body limit is enforced without content length', async t => {
  const f = await setup(t);
  const response = await new Promise((resolve, reject) => {
    const req = request(`${f.pool.url}/v1/captures/status`, { method: 'POST' }, res => {
      const data = [];
      res.on('data', b => data.push(b));
      res.on('end', () => resolve({ code: res.statusCode, body: Buffer.concat(data).toString() }));
    });
    req.on('error', reject);
    req.write(' '.repeat(65536)); req.end('x');
  });
  assert.equal(response.code, 413);
  assert.equal(JSON.parse(response.body).error, 'body_limit');
});

test('gzip metadata is discarded while exact validated uncompressed source bytes are preserved', async t => {
  const f = await setup(t);
  const bytes = source();
  const zipped = gzipSync(bytes);
  const header = Buffer.from(zipped.subarray(0, 10)); header[3] = 8;
  const namedGzip = Buffer.concat([header, Buffer.from('SyntheticPrivateMarker\0'), zipped.subarray(10)]);
  const response = await f.post('chunks', { ...chunk(bytes), data: namedGzip.toString('base64') });
  assert.equal(response.status, 201);
  const stored = fs.readFileSync(f.pool.archive.chunkPath(identity(ID), 0));
  assert.equal(stored.includes(Buffer.from('SyntheticPrivateMarker')), false);
  assert.deepEqual(gunzipSync(stored), bytes);
});

test('D1 adapter batch rolls back atomically and returns real selected rows and changes', t => {
  const dir = fs.mkdtempSync(path.join(tmpdir(), 'hp-d1-'));
  const store = openDatabase(path.join(dir, 'pool.sqlite'));
  t.after(() => { store.close(); fs.rmSync(dir, { force: true, recursive: true }); });
  return (async () => {
    const inserted = await store.DB.prepare('INSERT INTO meta(seq,body) VALUES(?1,?2)').bind(0, 'synthetic').run();
    assert.equal(inserted.meta.changes, 1);
    const rows = await store.DB.batch([store.DB.prepare('SELECT body FROM meta WHERE seq=?1').bind(0)]);
    assert.equal(rows[0].results[0].body, 'synthetic');
    await assert.rejects(store.DB.batch([store.DB.prepare('INSERT INTO meta VALUES(1,?)').bind('new'), store.DB.prepare('INSERT INTO missing_table VALUES(0)')]));
    assert.equal(await store.DB.prepare('SELECT body FROM meta WHERE seq=?1').bind(1).first(), null);
  })();
});

test('response cache clones bodies isolates instances and clears entries', async () => {
  const request = new Request('http://pool.test/v1/stats');
  const cache = new ResponseCache();
  await cache.put(request, new Response('{"games":1}', { headers: { 'cache-control': 'public, max-age=1800' } }));
  assert.equal((await (await cache.match(request)).json()).games, 1);
  assert.equal((await (await cache.match(request)).json()).games, 1, 'reading one clone does not consume the cached response');
  assert.equal(await new ResponseCache().match(request), null);
  cache.clear();
  assert.equal(await cache.match(request), null);
});

test('real Node entrypoint restarts after abrupt exit with persisted manifest and safe health output', async t => {
  const dataDir = fs.mkdtempSync(path.join(tmpdir(), 'hp-node-cli-'));
  let child;
  async function launch() {
    child = spawn(process.execPath, [path.resolve('server/pool/node/server.mjs')], {
      env: { ...process.env, POOL_DATA_DIR: dataDir, POOL_PORT: '0', POOL_HOST: '127.0.0.1', POOL_MIN_FREE_BYTES: '0',
        POOL_WORKER_EXECUTABLE: '', POOL_WORKER_ARGS: '' }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    });
    let output = '';
    const hello = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => { child.kill(); reject(new Error('entrypoint_start_timeout')); }, 10000);
      child.once('exit', () => { clearTimeout(timer); reject(new Error('entrypoint_start_failed')); });
      child.stdout.on('data', data => {
        output += data;
        if (output.includes('\n')) { clearTimeout(timer); resolve(JSON.parse(output.trim())); }
      });
    });
    assert.equal(hello.status, 'listening');
    assert.equal(output.includes(ID.iid), false);
    return `http://127.0.0.1:${hello.port}`;
  }
  t.after(async () => {
    if (child?.exitCode === null) { child.kill('SIGKILL'); await once(child, 'exit'); }
    fs.rmSync(dataDir, { recursive: true, force: true });
  });
  const url = await launch();
  const send = (url, route, body) => fetch(`${url}/v1/captures/${route}`, { method: 'POST', body: JSON.stringify(body) });
  assert.equal((await send(url, 'chunks', chunk())).status, 201);
  assert.equal((await send(url, 'complete', manifest())).status, 202);
  child.kill('SIGKILL'); await once(child, 'exit');
  const restarted = await launch();
  assert.equal((await (await send(restarted, 'status', ID)).json()).status, 'stored');
  assert.equal((await fetch(`${restarted}/healthz`)).status, 200);
  assert.equal((await send(restarted, 'reprocess', ID)).status, 403);
  assert.equal((await send(restarted, 'forget', { v: 1, iid: ID.iid, generation: ID.generation })).status, 200);
});

test('single writer OS lock rejects another service and releases after normal shutdown', async t => {
  const f = await setup(t);
  await assert.rejects(startPool({ dataDir: f.dataDir, port: 0, schedule: false }));
  await f.restart();
  assert.equal((await fetch(`${f.pool.url}/healthz`)).status, 200);
});

test('corrupt complete revocation ledger fails closed before HTTP starts', async t => {
  const dir = fs.mkdtempSync(path.join(tmpdir(), 'hp-ledger-corrupt-'));
  t.after(() => fs.rmSync(dir, { force: true, recursive: true }));
  fs.mkdirSync(path.join(dir, 'private'));
  fs.writeFileSync(path.join(dir, 'private', 'revocations.jsonl'), 'not-json\n');
  await assert.rejects(startPool({ dataDir: dir, port: 0, schedule: false }), /revocation_ledger_corrupt/);
  fs.writeFileSync(path.join(dir, 'private', 'revocations.jsonl'), '');
  const recovered = await startPool({ dataDir: dir, port: 0, schedule: false });
  await recovered.close();
});

test('synthetic smoke preserves the stats mount prefix through upload status and deletion', async t => {
  const f = await setup(t, { schedule: true, archive: { worker: workerConfig('v2') } });
  const routes = [];
  const proxy = createServer((req, res) => {
    routes.push(req.url);
    if (!req.url.startsWith('/stats/')) { res.writeHead(404); res.end(); return; }
    const upstream = request(new URL(req.url.slice('/stats'.length), f.pool.url), { method: req.method, headers: req.headers }, response => {
      res.writeHead(response.statusCode, response.headers); response.pipe(res);
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
  });
  await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => proxy.close(resolve)));
  const child = spawn(process.execPath, [path.resolve('server/pool/deploy/synthetic-smoke.mjs'), `http://127.0.0.1:${proxy.address().port}/stats`], {
    env: { ...process.env, POOL_OPERATOR_TOKEN: '' }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  });
  let out = ''; let error = '';
  child.stdout.on('data', b => { out += b; }); child.stderr.on('data', b => { error += b; });
  const timer = setTimeout(() => child.kill('SIGKILL'), 15000);
  const [code] = await once(child, 'exit'); clearTimeout(timer);
  assert.equal(code, 0, error);
  assert.equal(JSON.parse(out).status, 'synthetic_deleted');
  assert.ok(routes.includes('/stats/v1/captures/chunks'));
  assert.ok(routes.includes('/stats/v1/captures/complete'));
  assert.ok(routes.includes('/stats/v1/captures/forget'));
  assert.equal(f.pool.store.sqlite.prepare('SELECT COUNT(*) AS n FROM source_captures').get().n, 0);
});
