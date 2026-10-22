// Hearthpulse stats pool. Players who opt in send one small record per Battlegrounds game; anyone can read the
// totals per hero. Records hold no player identity, and the IP address is only seen by Cloudflare's rate limiter.

const HERO = /^[A-Za-z0-9_]{3,64}$/;
const GAME_ID = /^[0-9a-f]{32}$/;
const DAY = 86_400_000;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (request.method === "OPTIONS") return reply(null, 204);
    if (request.method === "POST" && url.pathname === "/v1/games") return submit(request, env);
    if (request.method === "GET" && url.pathname === "/v1/heroes") return heroes(request, env, ctx);
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

async function submit(request, env) {
  if (env.LIMITER) {
    const { success } = await env.LIMITER.limit({ key: request.headers.get("cf-connecting-ip") ?? "unknown" });
    if (!success) return reply({ error: "too many games, slow down" }, 429);
  }

  if (Number(request.headers.get("content-length") ?? 0) > 2048) return reply({ error: "too large" }, 413);
  let body;
  try {
    body = await request.json();
  } catch {
    return reply({ error: "not json" }, 400);
  }

  const game = validate(body);
  if (!game) return reply({ error: "invalid game" }, 400);

  const statements = [
    env.DB.prepare("INSERT OR IGNORE INTO games (id, created_at, mode, bracket, hero, placement) VALUES (?, ?, ?, ?, ?, ?)")
      .bind(game.id, Date.now(), game.mode, game.bracket, game.picked, game.placement),
    ...game.offered.map((hero) =>
      env.DB.prepare("INSERT OR IGNORE INTO offers (game_id, hero, picked) VALUES (?, ?, ?)")
        .bind(game.id, hero, hero === game.picked ? 1 : 0)),
  ];
  await env.DB.batch(statements);
  return reply({ ok: true });
}

/** Returns a clean record, or null when anything is missing or out of range. */
export function validate(body) {
  if (!body || typeof body !== "object" || body.v !== 1) return null;
  const id = String(body.gameId ?? "");
  const mode = body.mode === "solo" || body.mode === "duos" ? body.mode : null;
  const placement = Number(body.placement);
  const bracket = Number(body.bracket ?? 0);
  const picked = String(body.picked ?? "");
  const offered = Array.isArray(body.offered) ? [...new Set(body.offered.map(String))] : [];

  if (!GAME_ID.test(id) || !mode || !HERO.test(picked)) return null;
  if (!Number.isInteger(placement) || placement < 1 || placement > 8) return null;
  if (!Number.isInteger(bracket) || bracket < 0 || bracket > 20000 || bracket % 500 !== 0) return null;
  if (offered.length > 4 || offered.some((hero) => !HERO.test(hero))) return null;
  if (offered.length > 0 && !offered.includes(picked)) return null;
  return { id, mode, placement, bracket, picked, offered };
}

async function heroes(request, env, ctx) {
  const cache = caches.default;
  const cached = await cache.match(request);
  if (cached) return cached;

  const url = new URL(request.url);
  const mode = url.searchParams.get("mode") === "duos" ? "duos" : "solo";
  const days = clamp(url.searchParams.get("days"), 1, 365, 30);
  const minRating = clamp(url.searchParams.get("minRating"), 0, 20000, 0);
  const since = Date.now() - days * DAY;
  const top = mode === "duos" ? 2 : 4;

  const placed = await env.DB.prepare(
    `SELECT hero, COUNT(*) AS games, AVG(placement) AS avg, SUM(placement <= ?) AS top, SUM(placement = 1) AS first
     FROM games WHERE mode = ? AND created_at >= ? AND bracket >= ? GROUP BY hero`)
    .bind(top, mode, since, minRating).all();
  const offers = await env.DB.prepare(
    `SELECT o.hero AS hero, COUNT(*) AS offered, SUM(o.picked) AS picked
     FROM offers o JOIN games g ON g.id = o.game_id
     WHERE g.mode = ? AND g.created_at >= ? AND g.bracket >= ? GROUP BY o.hero`)
    .bind(mode, since, minRating).all();

  const byHero = new Map(offers.results.map((row) => [row.hero, row]));
  const list = placed.results.map((row) => {
    const offer = byHero.get(row.hero);
    return {
      hero: row.hero,
      games: row.games,
      avgPlacement: Math.round(row.avg * 1000) / 1000,
      top: row.top / row.games,
      first: row.first / row.games,
      offered: offer?.offered ?? 0,
      picked: offer?.picked ?? 0,
    };
  });

  const response = reply(
    { mode, days, minRating, updated: new Date().toISOString(), games: list.reduce((sum, h) => sum + h.games, 0), heroes: list },
    200,
    { "cache-control": "public, max-age=600" });
  ctx.waitUntil(cache.put(request, response.clone()));
  return response;
}

function clamp(value, min, max, fallback) {
  const n = Number.parseInt(value ?? "", 10);
  return Number.isNaN(n) ? fallback : Math.min(max, Math.max(min, n));
}