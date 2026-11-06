import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import test from "node:test";
import worker from "../src/index.js";
import { rebuildEvents, validateEvent } from "../src/events.js";

const TODAY = Math.floor(Date.now() / 86_400_000);
const IID = "1".repeat(32);

const ranked = (overrides = {}) => ({
  id: "a".repeat(32), day: TODAY, mode: "ranked", fmt: "standard", rank: "diamond", cls: "MAGE", hero: "HERO_08",
  deck: "AAECAf0EBuwH", ocls: "WARRIOR", oarch: "Control Warrior", res: "W", turns: 9, secs: 540, first: true,
  mull: { o: ["CARD_A", "CARD_B", "CARD_C"], k: ["CARD_A"], r: ["CARD_B", "CARD_C"], n: ["CARD_D", "CARD_E"] },
  plays: [[1, "CARD_A", 1, 0], [3, "CARD_X", 2, 1]],
  oplays: [[2, "CARD_O"]],
  choices: [[3, "discover", "CARD_S", ["CARD_X", "CARD_Y", "CARD_Z"], ["CARD_X"]]],
  draws: [[1, "CARD_F", 0], [3, "CARD_X", 1]],
  exc: [[4, "WW_001t"]],
  ...overrides,
});

const bg = (overrides = {}) => ({
  id: "b".repeat(32), day: TODAY, mode: "bg", res: "W", turns: 12, secs: 1300,
  bg: {
    offered: ["BG_HERO_1", "BG_HERO_2"], picked: "BG_HERO_1", placement: 2, mmr: 6500, anomaly: "BG_ANOMALY_1",
    tribes: ["BEAST", "DRAGON"], tierups: [[3, 2], [5, 3]], trinkets: { o: ["T_A", "T_B"], p: ["T_A"] },
    board: [{ c: "MINION_A", g: 1, a: 10, h: 12 }], buys: [[1, "MINION_A"]], sells: [[2, "MINION_B"]],
    choices: [[4, "discover", "SRC", ["A_1", "A_2"], ["A_1"]]], quests: ["QUEST_A"],
  },
  ...overrides,
});

function sqlite() {
  const db = new DatabaseSync(":memory:");
  db.exec(readFileSync(new URL("../schema.sql", import.meta.url), "utf8"));
  const statement = (sql, args = []) => ({
    bind: (...values) => statement(sql, values),
    first: async () => db.prepare(sql).get(...args) ?? null,
    all: async () => ({ results: db.prepare(sql).all(...args) }),
    run: async () => db.prepare(sql).run(...args),
  });
  return { db, DB: { prepare: (sql) => statement(sql), batch: async (list) => Promise.all(list.map((s) => s.run())) } };
}

const post = (path, body, env) => worker.fetch(new Request(`https://pool.test${path}`, {
  method: "POST", headers: { "content-type": "application/json" },
  body: typeof body === "string" ? body : JSON.stringify(body),
}), env, {});

test("a Constructed record keeps every known field and nothing else", () => {
  const row = validateEvent({ ...ranked(), name: "Player#1234", battletag: "x", accountId: 5 }, TODAY);
  assert.equal(row.id, "a".repeat(32));
  assert.equal(row.mode, "ranked");
  assert.equal(row.fmt, "standard");
  const body = JSON.parse(row.body);
  assert.equal(body.name, undefined);
  assert.equal(body.battletag, undefined);
  assert.equal(body.accountId, undefined);
  assert.deepEqual(body.mull, ranked().mull);
  assert.deepEqual(body.plays, ranked().plays);
  assert.deepEqual(body.choices, ranked().choices);
  assert.deepEqual(body.exc, ranked().exc);
  assert.equal(body.oarch, "Control Warrior");
});

test("a Battlegrounds record keeps its lobby, buys, sells, picks and placement", () => {
  const row = validateEvent(bg(), TODAY);
  const body = JSON.parse(row.body);
  assert.equal(row.mode, "bg");
  assert.equal(body.bg.placement, 2);
  assert.equal(body.bg.mmr, 6500);
  assert.deepEqual(body.bg.buys, [[1, "MINION_A"]]);
  assert.deepEqual(body.bg.sells, [[2, "MINION_B"]]);
  assert.deepEqual(body.bg.trinkets, { o: ["T_A", "T_B"], p: ["T_A"] });
  assert.deepEqual(body.bg.quests, ["QUEST_A"]);
  assert.equal(JSON.parse(validateEvent(bg({ bg: { placement: 1, mmr: 14500 } }), TODAY).body).bg.mmr, 10_000, "10000+");
});

test("a record with a bad core field is rejected", () => {
  for (const overrides of [
    { id: "short" }, { mode: "mercenaries" }, { mode: "friendly" }, { res: "maybe" }, { day: TODAY - 500 }, { day: TODAY + 3 },
    { fmt: "classicx" }, { turns: -1 }, { secs: 10 ** 8 },
  ]) assert.equal(validateEvent(ranked(overrides), TODAY), null, JSON.stringify(overrides));
  assert.equal(validateEvent(bg({ bg: { placement: 9 } }), TODAY), null);
  assert.equal(validateEvent("junk", TODAY), null);
  assert.equal(validateEvent([], TODAY), null);
});

test("bad optional parts are dropped, not the record", () => {
  const body = JSON.parse(validateEvent(ranked({
    rank: "legend-7", cls: "mage<script>", deck: "not a deck code!", oarch: "<b>x</b>", first: "yes",
    mull: { o: ["CARD_A"], k: ["NOT A CARD"] },
    plays: [[1, "CARD_A", 1, 0], [2, "bad card"], "junk", [500, "CARD_B", 1, 0]],
    choices: [[3, "discover", "CARD_S", ["CARD_X"], ["CARD_Q"]], [3, "spy", "CARD_S", ["A_1", "A_2"], ["A_1"]]],
  }), TODAY).body);
  assert.equal(body.rank, undefined);
  assert.equal(body.cls, undefined);
  assert.equal(body.deck, undefined);
  assert.equal(body.oarch, undefined);
  assert.equal(body.first, undefined);
  assert.equal(body.mull, undefined);
  assert.deepEqual(body.plays, [[1, "CARD_A", 1, 0]]);
  assert.deepEqual(body.choices, [[3, "choice", "CARD_S", ["A_1", "A_2"], ["A_1"]]]);
});

test("lists are capped", () => {
  const plays = Array.from({ length: 900 }, (_, i) => [1 + (i % 40), "CARD_A", 1, 0]);
  const body = JSON.parse(validateEvent(ranked({ plays }), TODAY).body);
  assert.equal(body.plays.length, 400);
});

test("POST /v1/events stores a batch once, and a second send is not counted twice", async () => {
  const env = sqlite();
  const first = await post("/v1/events", { v: 1, iid: IID, games: [ranked(), bg()] }, env);
  assert.equal(first.status, 200);
  assert.deepEqual(await first.json(), { ok: true, stored: 2 });
  await post("/v1/events", { v: 1, iid: IID, games: [ranked()] }, env);
  assert.equal(env.db.prepare("SELECT COUNT(*) AS n FROM e").get().n, 2);
  assert.equal(env.db.prepare("SELECT iid FROM e LIMIT 1").get().iid, IID);
});

test("POST /v1/events refuses a bad envelope", async () => {
  const env = sqlite();
  for (const body of [
    "not json", { v: 2, iid: IID, games: [ranked()] }, { v: 1, iid: "short", games: [ranked()] },
    { v: 1, iid: IID, games: [] }, { v: 1, iid: IID, games: Array.from({ length: 21 }, () => ranked()) },
    { v: 1, iid: IID, games: [{ id: "junk" }] },
  ]) assert.equal((await post("/v1/events", body, env)).status, 400, JSON.stringify(body).slice(0, 60));
});

test("POST /v1/events refuses a body over the size limit", async () => {
  const env = sqlite();
  const big = JSON.stringify({ v: 1, iid: IID, games: [ranked({ pad: "x".repeat(600 * 1024) })] });
  assert.equal((await post("/v1/events", big, env)).status, 413);
});

test("a record over the per-game size limit is dropped", () => {
  const plays = Array.from({ length: 400 }, (_, i) => [1 + (i % 40), "CARD_" + "A".repeat(55) + i, 10, 1]);
  const choices = Array.from({ length: 150 }, () => [3, "discover", "C".repeat(60), ["A".repeat(60), "B".repeat(60), "D".repeat(60)], ["A".repeat(60)]]);
  assert.equal(validateEvent(ranked({ plays, choices, draws: plays.map((p) => [p[0], p[1], 1]) }), TODAY), null);
});

test("the daily cap answers 503 past it", async () => {
  const env = { ...sqlite(), EVENTS_DAILY: "1" };
  assert.equal((await post("/v1/events", { v: 1, iid: IID, games: [ranked()] }, env)).status, 200);
  assert.equal((await post("/v1/events", { v: 1, iid: IID, games: [bg()] }, env)).status, 503);
});

test("POST /v1/events/forget deletes every record of the install and nobody else's", async () => {
  const env = sqlite();
  await post("/v1/events", { v: 1, iid: IID, games: [ranked()] }, env);
  await post("/v1/events", { v: 1, iid: "2".repeat(32), games: [bg()] }, env);
  const response = await post("/v1/events/forget", { iid: IID }, env);
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { ok: true, deleted: 1 });
  assert.equal(env.db.prepare("SELECT COUNT(*) AS n FROM e").get().n, 1);
  assert.equal((await post("/v1/events/forget", { iid: "nope" }, env)).status, 400);
});

test("the summary counts games per day and builds Discover and mulligan totals from 10 games", async () => {
  const env = sqlite();
  const games = Array.from({ length: 12 }, (_, i) => ranked({
    id: i.toString(16).padStart(32, "0"),
    mull: { o: ["CARD_A", "CARD_B"], k: i % 4 === 0 ? ["CARD_B"] : ["CARD_A", "CARD_B"], r: [], n: [] },
    choices: [[3, "discover", "CARD_S", ["CARD_X", "CARD_Y", "CARD_Z"], [i < 9 ? "CARD_X" : "CARD_Y"]]],
  }));
  // Twelve games from six installs, two each; the Battlegrounds game is one install's alone.
  for (let i = 0; i < 12; i += 2) await post("/v1/events", { v: 1, iid: (i + 1).toString(16).padStart(32, "0"), games: games.slice(i, i + 2) }, env);
  await post("/v1/events", { v: 1, iid: IID, games: [bg()] }, env);
  const summary = JSON.parse(await rebuildEvents(env, true));
  assert.deepEqual(summary.days, [{ day: TODAY, mode: "ranked", fmt: "standard", games: 12 }], "a day cell needs five installs");
  const x = summary.discover.find((r) => r.card === "CARD_X");
  assert.deepEqual(x, { kind: "discover", card: "CARD_X", offered: 12, picked: 9, rate: 0.75 });
  assert.equal(summary.discover[0].card, "CARD_X");
  const a = summary.mulligan.find((r) => r.card === "CARD_A");
  assert.deepEqual(a, { card: "CARD_A", offered: 12, kept: 9, rate: 0.75 });
  assert.ok(summary.mulligan.find((r) => r.card === "CARD_B").rate === 1);

  const read = await worker.fetch(new Request("https://pool.test/v1/events/summary"), env, { waitUntil() {} });
  assert.equal(read.status, 200);
  assert.equal((await read.json()).games, 13);
});

test("the summary leaves out cards seen in fewer than 10 games, or from fewer than 5 installs", async () => {
  const env = sqlite();
  await post("/v1/events", { v: 1, iid: IID, games: [ranked()] }, env);
  const one = Array.from({ length: 12 }, (_, i) => ranked({ id: (100 + i).toString(16).padStart(32, "0") }));
  await post("/v1/events", { v: 1, iid: IID, games: one.slice(0, 12) }, env);
  const summary = JSON.parse(await rebuildEvents(env, true));
  assert.deepEqual(summary.discover, []);
  assert.deepEqual(summary.mulligan, []);
});
