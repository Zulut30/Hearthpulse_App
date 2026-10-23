// Hearthpulse stats pool. Players who opt in send one record per Battlegrounds game; anyone can read the totals.
// Records hold no player identity and nothing about other players. The IP address is only seen by Cloudflare's
// rate limiter and is never stored.

const CARD = /^[A-Za-z0-9_]{3,64}$/;
const TRIBE = /^[A-Z_]{2,24}$/;
const GAME_ID = /^[0-9a-f]{32}$/;
const DAY = 86_400_000;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (request.method === "OPTIONS") return reply(null, 204);
    if (request.method === "POST" && url.pathname === "/v1/games") return submit(request, env);
    if (request.method === "GET" && url.pathname === "/v1/heroes") return cached(request, ctx, () => heroesOnly(request, env));
    if (request.method === "GET" && url.pathname === "/v1/stats") return cached(request, ctx, () => stats(request, env));
    return reply({ error: "not found" }, 404);
  },
};

function reply(body, status = 200, headers = {}) {
  return new Response(body === null ? null : JSON.stringify(body), {
    status,
    headers: {
      "content-type": "application/json",
      "access-control-allow-origin": "*",
      "access-control-allow-headers": "content-type",
      ...headers,
    },
  });
}

async function cached(request, ctx, build) {
  const cache = caches.default;
  const hit = await cache.match(request);
  if (hit) return hit;
  const response = reply(await build(), 200, { "cache-control": "public, max-age=600" });
  ctx.waitUntil(cache.put(request, response.clone()));
  return response;
}

async function submit(request, env) {
  if (env.LIMITER) {
    const { success } = await env.LIMITER.limit({ key: request.headers.get("cf-connecting-ip") ?? "unknown" });
    if (!success) return reply({ error: "too many games, slow down" }, 429);
  }

  if (Number(request.headers.get("content-length") ?? 0) > 16384) return reply({ error: "too large" }, 413);
  let body;
  try {
    body = await request.json();
  } catch {
    return reply({ error: "not json" }, 400);
  }

  const game = validate(body);
  if (!game) return reply({ error: "invalid game" }, 400);

  const d = game.details;
  const now = Date.now();
  const statements = [
    env.DB.prepare(
      `INSERT OR IGNORE INTO games (id, created_at, mode, bracket, hero, placement, version, turns, tier, triples, anomaly, tribes, comp)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`)
      .bind(game.id, now, game.mode, game.bracket, game.picked, game.placement, d ? 2 : 1,
        d?.turns ?? null, d?.tier ?? null, d?.triples ?? null, d?.anomaly ?? null, d ? d.tribes.join(",") : null, d?.comp ?? null),
    ...game.offered.map((hero) =>
      env.DB.prepare("INSERT OR IGNORE INTO offers (game_id, hero, picked) VALUES (?, ?, ?)")
        .bind(game.id, hero, hero === game.picked ? 1 : 0)),
  ];
  if (d) {
    d.board.forEach((m, slot) => statements.push(
      env.DB.prepare("INSERT OR IGNORE INTO boards (game_id, slot, card, golden, attack, health) VALUES (?, ?, ?, ?, ?, ?)")
        .bind(game.id, slot, m.card, m.golden ? 1 : 0, m.atk, m.hp)));
    d.trinkets.offered.forEach((card) => statements.push(
      env.DB.prepare("INSERT OR IGNORE INTO trinkets (game_id, card, picked) VALUES (?, ?, ?)")
        .bind(game.id, card, d.trinkets.picked.includes(card) ? 1 : 0)));
    d.tierUps.forEach(([turn, tier]) => statements.push(
      env.DB.prepare("INSERT OR IGNORE INTO tierups (game_id, tier, turn) VALUES (?, ?, ?)").bind(game.id, tier, turn)));
    d.combats.forEach((c, index) => statements.push(
      env.DB.prepare("INSERT OR IGNORE INTO combats (game_id, idx, turn, result, taken, dealt, p_win, p_tie, p_loss) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)")
        .bind(game.id, index, c.turn, c.result, c.taken, c.dealt, c.win, c.tie, c.loss)));
  }
  await env.DB.batch(statements);
  return reply({ ok: true, details: Boolean(d) });
}

const int = (x, min, max) => Number.isInteger(x) && x >= min && x <= max;
const odds = (x) => (x === null || x === undefined ? null : typeof x === "number" && x >= 0 && x <= 1 ? x : undefined);

/** Returns a clean record, or null when anything is missing or out of range. */
export function validate(body) {
  if (!body || typeof body !== "object" || (body.v !== 1 && body.v !== 2)) return null;
  const id = String(body.gameId ?? "");
  const mode = body.mode === "solo" || body.mode === "duos" ? body.mode : null;
  const placement = Number(body.placement);
  const bracket = Number(body.bracket ?? 0);
  const picked = String(body.picked ?? "");
  const offered = Array.isArray(body.offered) ? [...new Set(body.offered.map(String))] : [];

  if (!GAME_ID.test(id) || !mode || !CARD.test(picked)) return null;
  if (!int(placement, 1, 8)) return null;
  if (!int(bracket, 0, 20000) || bracket % 500 !== 0) return null;
  if (offered.length > 4 || offered.some((hero) => !CARD.test(hero))) return null;
  if (offered.length > 0 && !offered.includes(picked)) return null;
  // Details that do not check out are dropped; the placement still counts.
  const details = body.v === 2 ? validateDetails(body.details) : null;
  return { id, mode, placement, bracket, picked, offered, details };
}

/** The v2 part of a game, or null when it is missing or anything in it is out of range. */
export function validateDetails(d) {
  if (!d || typeof d !== "object") return null;
  if (!int(d.turns, 0, 99) || !int(d.tier, 1, 7) || !int(d.triples ?? 0, 0, 99)) return null;
  const anomaly = d.anomaly === null || d.anomaly === undefined || d.anomaly === "" ? null : String(d.anomaly);
  if (anomaly !== null && !CARD.test(anomaly)) return null;
  const tribes = Array.isArray(d.tribes) ? [...new Set(d.tribes.map(String))].sort() : [];
  if (tribes.length > 8 || tribes.some((t) => !TRIBE.test(t))) return null;
  const comp = d.comp === null || d.comp === undefined ? null : String(d.comp);
  if (comp !== null && !TRIBE.test(comp)) return null;

  const board = Array.isArray(d.board) ? d.board : [];
  if (board.length > 7) return null;
  for (const m of board) {
    if (!m || typeof m !== "object" || !CARD.test(String(m.card ?? "")) || !int(m.atk, 0, 1_000_000) || !int(m.hp, 0, 1_000_000)) return null;
  }

  const trinkets = d.trinkets && typeof d.trinkets === "object" ? d.trinkets : {};
  const pickedTrinkets = Array.isArray(trinkets.picked) ? [...new Set(trinkets.picked.map(String))] : [];
  const offeredTrinkets = [...new Set([...(Array.isArray(trinkets.offered) ? trinkets.offered.map(String) : []), ...pickedTrinkets])];
  if (pickedTrinkets.length > 4 || offeredTrinkets.length > 12 || offeredTrinkets.some((t) => !CARD.test(t))) return null;

  const tierUps = Array.isArray(d.tierUps) ? d.tierUps : [];
  if (tierUps.length > 7 || tierUps.some((t) => !Array.isArray(t) || !int(t[0], 0, 99) || !int(t[1], 1, 7))) return null;

  const combats = Array.isArray(d.combats) ? d.combats : [];
  if (combats.length > 60) return null;
  const cleanCombats = [];
  for (const c of combats) {
    if (!c || typeof c !== "object" || !int(c.turn, 0, 99) || !["W", "T", "L"].includes(c.result)) return null;
    if (!int(c.taken, 0, 200) || !int(c.dealt, 0, 200)) return null;
    const [win, tie, loss] = [odds(c.win), odds(c.tie), odds(c.loss)];
    if (win === undefined || tie === undefined || loss === undefined) return null;
    cleanCombats.push({ turn: c.turn, result: c.result, taken: c.taken, dealt: c.dealt, win, tie, loss });
  }

  return {
    turns: d.turns,
    tier: d.tier,
    triples: d.triples ?? 0,
    anomaly,
    tribes,
    comp,
    board: board.map((m) => ({ card: String(m.card), golden: m.golden === true, atk: m.atk, hp: m.hp })),
    trinkets: { offered: offeredTrinkets, picked: pickedTrinkets },
    tierUps: tierUps.map(([turn, tier]) => [turn, tier]),
    combats: cleanCombats,
  };
}

function filters(request) {
  const url = new URL(request.url);
  const mode = url.searchParams.get("mode") === "duos" ? "duos" : "solo";
  const days = clamp(url.searchParams.get("days"), 1, 365, 30);
  const minRating = clamp(url.searchParams.get("minRating"), 0, 20000, 0);
  return { mode, days, minRating, since: Date.now() - days * DAY, top: mode === "duos" ? 2 : 4 };
}

const round = (x) => (x === null || x === undefined ? null : Math.round(x * 1000) / 1000);

async function heroList(env, f) {
  const placed = await env.DB.prepare(
    `SELECT hero, COUNT(*) AS games, AVG(placement) AS avg, SUM(placement <= ?) AS top, SUM(placement = 1) AS first
     FROM games WHERE mode = ? AND created_at >= ? AND bracket >= ? GROUP BY hero`)
    .bind(f.top, f.mode, f.since, f.minRating).all();
  const offers = await env.DB.prepare(
    `SELECT o.hero AS hero, COUNT(*) AS offered, SUM(o.picked) AS picked
     FROM offers o JOIN games g ON g.id = o.game_id
     WHERE g.mode = ? AND g.created_at >= ? AND g.bracket >= ? GROUP BY o.hero`)
    .bind(f.mode, f.since, f.minRating).all();
  const byHero = new Map(offers.results.map((row) => [row.hero, row]));
  return placed.results.map((row) => ({
    hero: row.hero,
    games: row.games,
    avgPlacement: round(row.avg),
    top: row.top / row.games,
    first: row.first / row.games,
    offered: byHero.get(row.hero)?.offered ?? 0,
    picked: byHero.get(row.hero)?.picked ?? 0,
  }));
}

async function heroesOnly(request, env) {
  const f = filters(request);
  const heroes = await heroList(env, f);
  return { mode: f.mode, days: f.days, minRating: f.minRating, updated: new Date().toISOString(), games: heroes.reduce((s, h) => s + h.games, 0), heroes };
}

async function stats(request, env) {
  const f = filters(request);
  const where = "g.mode = ? AND g.created_at >= ? AND g.bracket >= ?";
  const args = [f.mode, f.since, f.minRating];
  const [heroes, cards, trinkets, comps, tierUps, combats, totals] = await Promise.all([
    heroList(env, f),
    env.DB.prepare(
      `SELECT card, COUNT(*) AS games, AVG(placement) AS avg FROM
         (SELECT DISTINCT b.game_id, b.card, g.placement FROM boards b JOIN games g ON g.id = b.game_id WHERE ${where})
       GROUP BY card`).bind(...args).all(),
    env.DB.prepare(
      `SELECT t.card AS card, COUNT(*) AS offered, SUM(t.picked) AS picked, AVG(CASE WHEN t.picked = 1 THEN g.placement END) AS avg
       FROM trinkets t JOIN games g ON g.id = t.game_id WHERE ${where} GROUP BY t.card`).bind(...args).all(),
    env.DB.prepare(
      `SELECT g.comp AS comp, COUNT(*) AS games, AVG(g.placement) AS avg, SUM(g.placement <= ?) AS top, SUM(g.placement = 1) AS first
       FROM games g WHERE g.comp IS NOT NULL AND ${where} GROUP BY g.comp`).bind(f.top, ...args).all(),
    env.DB.prepare(
      `SELECT u.tier AS tier, COUNT(*) AS games, AVG(u.turn) AS turn, AVG(g.placement) AS avg
       FROM tierups u JOIN games g ON g.id = u.game_id WHERE ${where} GROUP BY u.tier`).bind(...args).all(),
    env.DB.prepare(
      `SELECT COUNT(*) AS fights, AVG(c.p_win) AS predicted, AVG(c.result = 'W') AS won
       FROM combats c JOIN games g ON g.id = c.game_id WHERE c.p_win IS NOT NULL AND ${where}`).bind(...args).all(),
    env.DB.prepare(`SELECT COUNT(*) AS games, SUM(g.version >= 2) AS detailed FROM games g WHERE ${where}`).bind(...args).all(),
  ]);

  return {
    mode: f.mode,
    days: f.days,
    minRating: f.minRating,
    updated: new Date().toISOString(),
    games: totals.results[0]?.games ?? 0,
    detailedGames: totals.results[0]?.detailed ?? 0,
    heroes,
    cards: cards.results.map((r) => ({ card: r.card, games: r.games, avgPlacement: round(r.avg) })),
    trinkets: trinkets.results.map((r) => ({ card: r.card, offered: r.offered, picked: r.picked, avgPlacement: round(r.avg) })),
    comps: comps.results.map((r) => ({ comp: r.comp, games: r.games, avgPlacement: round(r.avg), top: r.top / r.games, first: r.first / r.games })),
    tierUps: tierUps.results.map((r) => ({ tier: r.tier, games: r.games, avgTurn: round(r.turn), avgPlacement: round(r.avg) })),
    combatOdds: { fights: combats.results[0]?.fights ?? 0, predictedWin: round(combats.results[0]?.predicted), actualWin: round(combats.results[0]?.won) },
  };
}

function clamp(value, min, max, fallback) {
  const n = Number.parseInt(value ?? "", 10);
  return Number.isNaN(n) ? fallback : Math.min(max, Math.max(min, n));
}