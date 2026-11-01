import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { DatabaseSync } from "node:sqlite";
import test from "node:test";
import worker, { percentileFloors, rebuild, rebuildChoices, validateConstructed, validateDetails, validateGame, validateLegacy } from "../src/index.js";

const TODAY = Math.floor(Date.now() / 86_400_000);
const HERO = "BG_HERO_1";
const game = (overrides = {}) => ({
  id: "a".repeat(32), day: TODAY, mode: "solo", bracket: 6500, picked: HERO,
  offered: [HERO, "BG_HERO_2"], placement: 3, ...overrides,
});
const details = (overrides = {}) => ({ turns: 10, tier: 5, ...overrides });
const ids = (count) => Array.from({ length: count }, (_, i) => `TRINKET_${i}`);

function database() {
  const writes = [];
  return {
    writes,
    DB: {
      prepare(sql) {
        assert.match(sql, /^INSERT OR IGNORE INTO g /);
        return {
          bind(json) {
            return { async run() { writes.push(JSON.parse(json)); } };
          },
        };
      },
    },
  };
}

async function submit(body, env = database()) {
  const request = new Request("https://pool.test/v1/games", {
    method: "POST", headers: { "content-type": "application/json" },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
  return { response: await worker.fetch(request, env, {}), writes: env.writes };
}

test("valid details keep chosen trinkets and deduplicate the offer", () => {
  const row = validateGame(game(details({
    trinkets: { o: ["TRINKET_A", "TRINKET_A"], p: ["TRINKET_B"] },
    tribes: ["BEAST", "DRAGON", "BEAST"],
    board: [{ c: "MINION_A", a: 10, h: 20, g: 1 }],
    combats: [[8, "W", 0, 12, 0.12345]],
  })), TODAY);
  assert.deepEqual(row.trinkets, { o: ["TRINKET_A", "TRINKET_B"], p: ["TRINKET_B"] });
  assert.equal(row.tribes, "BEAST,DRAGON");
  assert.deepEqual(row.board, [{ c: "MINION_A", g: 1, a: 10, h: 20 }]);
  assert.deepEqual(row.combats, [[8, "W", 0, 12, 0.123]]);
  assert.equal(row.hero, HERO);
});

for (const [name, trinkets] of [
  ["numeric picks", { p: 1 }], ["object offers", { o: {} }],
  ["string picks", { p: "TRINKET_A" }], ["string offers", { o: "TRINKET_A" }],
  ["invalid chosen card", { p: [{}] }], ["invalid offered card", { o: [null] }],
  ["primitive container", true], ["array container", []],
]) {
  test(`invalid trinkets discard details without losing the game: ${name}`, () => {
    assert.deepEqual(validateGame(game(details({ trinkets })), TODAY), validateGame(game(), TODAY));
  });
}

test("missing and null optional trinkets remain empty", () => {
  for (const trinkets of [undefined, null, {}, { o: null, p: null }]) {
    assert.deepEqual(validateDetails(details({ trinkets })).trinkets, { o: [], p: [] });
  }
});

test("trinket list limits include the boundary and reject the next item", () => {
  assert.equal(validateDetails(details({ trinkets: { p: ids(4) } })).trinkets.p.length, 4);
  assert.deepEqual(validateDetails(details({ trinkets: { p: ids(5) } })), {});
  assert.equal(validateDetails(details({ trinkets: { o: ids(12), p: ids(4) } })).trinkets.o.length, 12);
  assert.deepEqual(validateDetails(details({ trinkets: { o: ids(13) } })), {});
  assert.deepEqual(validateDetails(details({ trinkets: { o: ids(12), p: ["EXTRA_TRINKET"] } })), {});
});

test("raw trinket lists are bounded before deduplication", () => {
  assert.deepEqual(validateDetails(details({ trinkets: { o: Array(13).fill("TRINKET_A") } })), {});
});

test("non-object details return an empty result", () => {
  for (const input of [undefined, null, false, 5, "details", []]) assert.deepEqual(validateDetails(input), {});
});

test("malformed identifier objects cannot invoke string coercion", () => {
  const bad = JSON.parse('{"toString":null}');
  assert.equal(validateGame(game({ id: bad }), TODAY), null);
  assert.equal(validateGame(game({ offered: [bad] }), TODAY), null);
  for (const fields of [
    { anomaly: bad }, { tribes: [bad] }, { comp: bad },
    { board: [{ c: bad, a: 1, h: 1 }] },
  ]) assert.deepEqual(validateDetails(details(fields)), {});
  assert.equal(validateDetails(details({ choices: [[1, "discover", bad, ["CARD_A"], ["CARD_A"]]] })).choices, null);
});

test("invalid choices and plays do not remove valid entries", () => {
  const row = validateDetails(details({
    choices: [[1, "discover", null, ["CARD_A"], ["CARD_A"]], {}, [1, "discover", {}, ["CARD_A"], ["CARD_A"]]],
    plays: [null, [1, {}], [2, "CARD_B"]],
  }));
  assert.deepEqual(row.choices, [[1, "discover", null, ["CARD_A"], ["CARD_A"]]]);
  assert.deepEqual(row.plays, [[2, "CARD_B"]]);
});

test("legacy trinket fields use the same validation", () => {
  const legacy = { v: 2, gameId: "b".repeat(32), mode: "solo", picked: HERO, placement: 2 };
  const valid = validateLegacy({ ...legacy, details: details({ trinkets: { offered: ["TRINKET_A"], picked: ["TRINKET_B"] } }) }, TODAY);
  assert.deepEqual(valid.trinkets, { o: ["TRINKET_A", "TRINKET_B"], p: ["TRINKET_B"] });
  assert.deepEqual(validateLegacy({ ...legacy, details: details({ trinkets: { picked: 1 } }) }, TODAY), validateLegacy(legacy, TODAY));
});

test("POST stores valid games and drops invalid details in a mixed batch", async () => {
  const { response, writes } = await submit({ v: 3, games: [
    game(details({ trinkets: { p: ["TRINKET_A"] } })),
    game({ ...details({ trinkets: { p: 1 } }), id: "b".repeat(32) }),
    game({ id: "invalid" }),
  ] });
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { ok: true, stored: 2 });
  assert.equal(writes.length, 1);
  assert.deepEqual(writes[0].map((row) => row.id), ["a".repeat(32), "b".repeat(32)]);
  assert.deepEqual(writes[0][0].trinkets, { o: ["TRINKET_A"], p: ["TRINKET_A"] });
  assert.equal(Object.hasOwn(writes[0][1], "trinkets"), false);
  assert.equal(Object.hasOwn(writes[0][1], "turns"), false);
});

test("POST rejects malformed JSON and batches without valid games before writing", async () => {
  for (const body of ["{", null, { v: 3, games: [] }, { v: 3, games: [game({ picked: {} })] }, { v: 3, games: Array(21).fill(game()) }]) {
    const { response, writes } = await submit(body);
    assert.equal(response.status, 400);
    assert.deepEqual(writes, []);
  }
});

test("POST accepts a batch at the twenty game boundary", async () => {
  const { response, writes } = await submit({ v: 3, games: Array.from({ length: 20 }, (_, i) => game({ id: i.toString(16).padStart(32, "0") })) });
  assert.equal(response.status, 200);
  assert.equal(writes.length, 1);
  assert.equal(writes[0].length, 20);
});

// ---- Constructed picks ----------------------------------------------------------------------------------------

const DISCOVER = "CORE_YOP_001";
const constructed = (overrides = {}) => ({
  id: "c".repeat(32), day: TODAY, format: "standard", won: true,
  picks: [[4, DISCOVER, ["CARD_A", "CARD_B", "CARD_C"], ["CARD_B"]]], ...overrides,
});

/** The pool's tables in an in-memory SQLite, behind the few D1 calls the worker makes. */
function sqlite() {
  const db = new DatabaseSync(":memory:");
  db.exec(readFileSync(new URL("../schema.sql", import.meta.url), "utf8"));
  const statement = (sql, args = []) => ({
    bind: (...values) => statement(sql, values),
    first: async () => db.prepare(sql).get(...args) ?? null,
    all: async () => ({ results: db.prepare(sql).all(...args) }),
    run: async () => db.prepare(sql).run(...args),
  });
  return { db, DB: { prepare: (sql) => statement(sql), batch: async (list) => Promise.all(list.map((s) => s.all())) } };
}

async function submitConstructed(body, env) {
  const request = new Request("https://pool.test/v1/constructed", {
    method: "POST", headers: { "content-type": "application/json" },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
  return worker.fetch(request, env, {});
}

test("a Constructed game keeps only valid picks and holds no deck, class or opponent", () => {
  const row = validateConstructed(constructed({
    class: "MAGE", deck: "AAEC...", picks: [
      [4, DISCOVER, ["CARD_A", "CARD_B", "CARD_A"], ["CARD_B"]],
      [5, DISCOVER, ["CARD_A", "CARD_B"], ["CARD_X"]],
      [6, null, ["CARD_A", "CARD_B"], ["CARD_A"]],
      [7, DISCOVER, ["CARD_A"], ["CARD_A"]],
      "junk",
    ],
  }), TODAY);
  assert.deepEqual(row, { id: "c".repeat(32), day: TODAY, fmt: "standard", won: 1, src: "live", picks: [[4, DISCOVER, ["CARD_A", "CARD_B"], ["CARD_B"]]] });
});

test("a Constructed game without a valid core field or any valid pick is rejected", () => {
  for (const overrides of [
    { id: "short" }, { format: "classic" }, { won: "yes" }, { day: TODAY - 500 }, { picks: [] },
    { picks: [[4, DISCOVER, ["CARD_A", "CARD_B"], []]] }, { picks: [[4, { toString: null }, ["CARD_A", "CARD_B"], ["CARD_A"]]] },
  ]) assert.equal(validateConstructed(constructed(overrides), TODAY), null, JSON.stringify(overrides));
});

test("POST /v1/constructed stores a batch once and the totals count offers, picks and wins", async () => {
  const env = sqlite();
  const lost = constructed({ id: "d".repeat(32), won: false, picks: [[3, DISCOVER, ["CARD_A", "CARD_B", "CARD_D"], ["CARD_A"]]] });
  const third = constructed({ id: "e".repeat(32), picks: [[2, DISCOVER, ["CARD_A", "CARD_B", "CARD_C"], ["CARD_B"]]] });
  for (let i = 0; i < 2; i++) {
    const response = await submitConstructed({ v: 1, games: [constructed(), lost, third] }, env);
    assert.equal(response.status, 200);
  }

  assert.equal(env.db.prepare("SELECT COUNT(*) AS n FROM c").get().n, 3);
  const totals = JSON.parse(await rebuildChoices(env, "standard", true));
  assert.equal(totals.games, 3);
  assert.equal(totals.sources.length, 1);
  assert.equal(totals.sources[0].source, DISCOVER);
  assert.equal(totals.sources[0].choices, 3);
  // CARD_D was offered once: under the three-offer floor, so it is left out.
  const options = Object.fromEntries(totals.sources[0].options.map((o) => [o[0], o.slice(1)]));
  assert.deepEqual(options, { CARD_A: [3, 1, 0], CARD_B: [3, 2, 2] });
  assert.equal(JSON.parse(await rebuildChoices(env, "wild", true)).games, 0);
});

test("POST /v1/constructed rejects malformed bodies before writing", async () => {
  const env = sqlite();
  for (const body of ["{", { v: 2, games: [constructed()] }, { v: 1, games: [] }, { v: 1, games: [constructed({ won: null })] },
    { v: 1, games: Array(21).fill(constructed()) }]) {
    assert.equal((await submitConstructed(body, env)).status, 400);
  }

  assert.equal(env.db.prepare("SELECT COUNT(*) AS n FROM c").get().n, 0);
});

// ---- Totals per rating bracket and lobby tribe (roadmap 2.1) ---------------------------------------------------

test("percentile floors walk down from the highest rating and need enough rated games", () => {
  assert.deepEqual(percentileFloors([{ b: 6000, n: 10 }]), []);
  const rows = [{ b: 6000, n: 50 }, { b: 7000, n: 30 }, { b: 8000, n: 15 }, { b: 9000, n: 5 }];
  assert.deepEqual(percentileFloors(rows), [
    { top: 50, bracket: 7000 }, { top: 20, bracket: 8000 }, { top: 5, bracket: 9000 }, { top: 1, bracket: 9000 },
  ]);
});

test("the totals split heroes and trinkets by rating bracket and heroes by lobby tribe", async () => {
  const env = sqlite();
  const games = Array.from({ length: 60 }, (_, i) => game({
    id: i.toString(16).padStart(32, "0"),
    bracket: i < 30 ? 6000 : i < 48 ? 7000 : i < 57 ? 8000 : 9500,
    picked: i % 2 ? HERO : "BG_HERO_2",
    placement: i % 2 ? 2 : 6,
    ...details({ tribes: i % 3 ? ["MURLOC", "BEAST"] : ["DEMON", "BEAST"], trinkets: { o: ["T_A", "T_B"], p: [i % 2 ? "T_A" : "T_B"] } }),
  }));
  for (let i = 0; i < games.length; i += 20) {
    const request = new Request("https://pool.test/v1/games", {
      method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ v: 3, games: games.slice(i, i + 20) }),
    });
    assert.equal((await worker.fetch(request, env, {})).status, 200);
  }

  const totals = JSON.parse(await rebuild(env, "solo", true));
  assert.equal(totals.games, 60);
  assert.deepEqual(totals.percentiles.map((p) => p.top), [50, 20, 5, 1]);
  assert.equal(totals.percentiles[0].bracket, 7000);
  assert.equal(totals.percentiles[3].bracket, 9500);
  // The top 1 % bracket holds the three 9500 games only.
  assert.equal(totals.heroesByBracket["1"].reduce((n, h) => n + h.games, 0), 3);
  assert.equal(totals.heroesByBracket["50"].reduce((n, h) => n + h.games, 0), 30);
  const winner = totals.heroesByBracket["50"].find((h) => h.hero === HERO);
  assert.deepEqual([winner.games, winner.avgPlacement, winner.top, winner.first], [15, 2, 1, 0]);
  assert.equal(totals.trinketsByBracket["50"].find((t) => t.card === "T_A").picked, 15);
  // Heroes by lobby tribe: Beast was in every lobby; Murloc in two thirds.
  const beast = totals.heroTribes.find((t) => t.hero === HERO && t.tribe === "BEAST");
  assert.deepEqual([beast.games, beast.avgPlacement], [30, 2]);
  assert.equal(totals.heroTribes.find((t) => t.hero === HERO && t.tribe === "MURLOC").games, 20);
});

test("quest and reward picks are accepted choice kinds", () => {
  const row = validateDetails(details({ choices: [[5, "quest", null, ["Q_A", "Q_B", "Q_C"], ["Q_B"]], [9, "reward", "Q_B", ["R_A"], ["R_A"]]] }));
  assert.equal(row.choices.length, 2);
});
