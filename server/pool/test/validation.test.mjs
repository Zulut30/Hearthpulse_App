import assert from "node:assert/strict";
import test from "node:test";
import worker, { validateDetails, validateGame, validateLegacy } from "../src/index.js";

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
