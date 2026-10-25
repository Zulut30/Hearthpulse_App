// Hearthpulse stats pool. Players who opt in send their Battlegrounds games; anyone can read the totals.
// Records hold no player identity and nothing about other players. The IP address is only seen by Cloudflare's
// rate limiter and is never stored.
//
// Built for Cloudflare's free plan (100k requests/day, 10 ms CPU per request, 50 queries per request, 100k rows
// written and 5M rows read per day):
// - a game is one row, and a whole batch of games is one INSERT statement;
// - the totals are worked out by a cron job at most every 6 hours, and only when new games came in, then kept
//   as one ready-made JSON row that every read returns (one row read, cached at the edge for 30 minutes).

const CARD = /^[A-Za-z0-9_]{3,64}$/;
const TRIBE = /^[A-Z0-9_]{2,24}$/;
const GAME_ID = /^[0-9a-f]{32}$/;
const KINDS = new Set(["discover", "trinket", "choice"]);
const DAY = 86_400_000;
const WINDOW_DAYS = 30;
const MAX_BATCH = 20;
const MAX_BODY = 512 * 1024;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (request.method === "OPTIONS") return reply(null, 204);
    if (request.method === "POST" && url.pathname === "/v1/games") return submit(request, env);
    if (request.method === "GET" && (url.pathname === "/v1/stats" || url.pathname === "/v1/heroes")) return stats(request, env, ctx, url);
    if (request.method === "GET" && url.pathname === "/v1/meta") return meta(request, env, ctx);
    return reply({ error: "not found" }, 404);
  },

  async scheduled(event, env, ctx) {
    ctx.waitUntil(Promise.all(["solo", "duos"].map((mode) => rebuild(env, mode, false))));
  },
};

function reply(body, status = 200, headers = {}) {
  return new Response(body === null ? null : typeof body === "string" ? body : JSON.stringify(body), {
    status,
    headers: {
      "content-type": "application/json",
      "access-control-allow-origin": "*",
      "access-control-allow-headers": "content-type",
      ...headers,
    },
  });
}

// ---- Writing ---------------------------------------------------------------------------------------------

const INSERT = `INSERT OR IGNORE INTO g (id, day, mode, bracket, hero, placement, src, turns, tier, triples, anomaly, tribes, comp,
                                        offers, board, trinkets, tierups, combats, choices, plays)
  SELECT json_extract(value, '$.id'), json_extract(value, '$.day'), json_extract(value, '$.mode'), json_extract(value, '$.bracket'),
         json_extract(value, '$.hero'), json_extract(value, '$.placement'), json_extract(value, '$.src'), json_extract(value, '$.turns'),
         json_extract(value, '$.tier'), json_extract(value, '$.triples'), json_extract(value, '$.anomaly'), json_extract(value, '$.tribes'),
         json_extract(value, '$.comp'), json_extract(value, '$.offers'), json_extract(value, '$.board'), json_extract(value, '$.trinkets'),
         json_extract(value, '$.tierups'), json_extract(value, '$.combats'), json_extract(value, '$.choices'), json_extract(value, '$.plays')
  FROM json_each(?1)`;

async function submit(request, env) {
  if (env.LIMITER) {
    const { success } = await env.LIMITER.limit({ key: request.headers.get("cf-connecting-ip") ?? "unknown" });
    if (!success) return reply({ error: "too many requests, slow down" }, 429);
  }

  if (Number(request.headers.get("content-length") ?? 0) > MAX_BODY) return reply({ error: "too large" }, 413);
  const text = await request.text();
  if (text.length > MAX_BODY) return reply({ error: "too large" }, 413);
  let body;
  try {
    body = JSON.parse(text);
  } catch {
    return reply({ error: "not json" }, 400);
  }

  // Version 3 sends {v:3, games:[...]}; versions 1 and 2 (Hearthpulse 0.8 and 0.9) send one game.
  const today = Math.floor(Date.now() / DAY);
  const batch = body?.v === 3 ? (Array.isArray(body.games) ? body.games : []) : [body];
  if (batch.length === 0 || batch.length > MAX_BATCH) return reply({ error: "send 1 to 20 games" }, 400);
  const rows = batch.map((game) => (body?.v === 3 ? validateGame(game, today) : validateLegacy(game, today))).filter(Boolean);
  if (rows.length === 0) return reply({ error: "invalid game" }, 400);

  await env.DB.prepare(INSERT).bind(JSON.stringify(rows)).run();
  return reply({ ok: true, stored: rows.length });
}

const int = (x, min, max) => Number.isInteger(x) && x >= min && x <= max;
const card = (x) => typeof x === "string" && CARD.test(x);
const tribe = (x) => typeof x === "string" && TRIBE.test(x);
const cards = (list, max) => Array.isArray(list) && list.length <= max && list.every(card);

/** A clean row for a version 3 game, or null when its core fields are missing or out of range. */
export function validateGame(g, today) {
  if (!g || typeof g !== "object") return null;
  const id = g.id;
  const mode = g.mode === "solo" || g.mode === "duos" ? g.mode : null;
  const day = g.day ?? today;
  const rawOffered = Array.isArray(g.offered) ? g.offered : [];
  if (!rawOffered.every(card)) return null;
  const offered = [...new Set(rawOffered)];
  if (typeof id !== "string" || !GAME_ID.test(id) || !mode || !int(day, today - 400, today + 1)) return null;
  if (!card(g.picked) || !int(g.placement, 1, 8)) return null;
  if (!int(g.bracket ?? 0, 0, 20000) || (g.bracket ?? 0) % 500 !== 0) return null;
  if (offered.length > 4 || (offered.length > 0 && !offered.includes(g.picked))) return null;

  const row = {
    id, day, mode, bracket: g.bracket ?? 0, hero: g.picked, placement: g.placement, src: g.src === "history" ? "history" : "live",
    offers: offered.length > 0 ? offered : null,
  };
  return { ...row, ...validateDetails(g) };
}

/** The optional details of a game, or {} when they are missing or anything core in them is out of range. */
export function validateDetails(d) {
  if (!d || typeof d !== "object" || Array.isArray(d)) return {};
  if (!int(d.turns, 0, 99) || !int(d.tier, 1, 7) || !int(d.triples ?? 0, 0, 99)) return {};
  const anomaly = d.anomaly ?? null;
  const rawTribes = Array.isArray(d.tribes) ? d.tribes : [];
  if (!rawTribes.every(tribe)) return {};
  const tribes = [...new Set(rawTribes)].sort();
  const comp = d.comp ?? null;
  if ((anomaly !== null && !card(anomaly)) || tribes.length > 8) return {};
  if (comp !== null && !tribe(comp)) return {};

  const board = d.board ?? [];
  if (!Array.isArray(board) || board.length > 7) return {};
  if (!board.every((m) => m && card(m.c) && int(m.a, 0, 1_000_000) && int(m.h, 0, 1_000_000))) return {};

  if (d.trinkets != null && (typeof d.trinkets !== "object" || Array.isArray(d.trinkets))) return {};
  const picked = d.trinkets?.p ?? [];
  const offered = d.trinkets?.o ?? [];
  // Validate before spreading: JSON may contain a number/object here, which is not iterable. Bounding the raw
  // arrays also avoids allocating a large merged list just to reject it after deduplication.
  if (!cards(picked, 4) || !cards(offered, 12)) return {};
  const trinketsOffered = [...new Set([...offered, ...picked])];
  if (trinketsOffered.length > 12) return {};

  const tierups = d.tierups ?? [];
  if (!Array.isArray(tierups) || tierups.length > 7 || !tierups.every((t) => Array.isArray(t) && int(t[0], 0, 99) && int(t[1], 1, 7))) return {};

  const combats = d.combats ?? [];
  if (!Array.isArray(combats) || combats.length > 60) return {};
  const fights = [];
  for (const c of combats) {
    const win = c?.[4] ?? null;
    if (!Array.isArray(c) || !int(c[0], 0, 99) || !["W", "T", "L"].includes(c[1]) || !int(c[2], 0, 200) || !int(c[3], 0, 200)) return {};
    if (win !== null && !(typeof win === "number" && win >= 0 && win <= 1)) return {};
    fights.push([c[0], c[1], c[2], c[3], win === null ? null : Math.round(win * 1000) / 1000]);
  }

  // Picks and plays are long lists: a bad entry is dropped instead of the whole game.
  const choices = (Array.isArray(d.choices) ? d.choices : []).slice(0, 120)
    .filter((c) => Array.isArray(c) && int(c[0], 0, 99) && KINDS.has(c[1]) && (c[2] === null || card(c[2]))
      && cards(c[3], 8) && c[3].length > 0 && cards(c[4], 4) && c[4].every((p) => c[3].includes(p)))
    .map((c) => [c[0], c[1], c[2], c[3], c[4]]);
  const plays = (Array.isArray(d.plays) ? d.plays : []).slice(0, 300)
    .filter((p) => Array.isArray(p) && int(p[0], 0, 99) && typeof p[1] === "string" && CARD.test(p[1]))
    .map((p) => [p[0], p[1]]);

  return {
    turns: d.turns,
    tier: d.tier,
    triples: d.triples ?? 0,
    anomaly,
    tribes: tribes.join(","),
    comp,
    board: board.map((m) => ({ c: m.c, g: m.g ? 1 : 0, a: m.a, h: m.h })),
    trinkets: { o: trinketsOffered, p: picked },
    tierups: tierups.map((t) => [t[0], t[1]]),
    combats: fights,
    choices: choices.length > 0 ? choices : null,
    plays: plays.length > 0 ? plays : null,
  };
}

/** Games from Hearthpulse 0.8 (v1) and 0.9 (v2), converted to the version 3 shape. */
export function validateLegacy(body, today) {
  if (!body || (body.v !== 1 && body.v !== 2)) return null;
  const d = body.v === 2 && body.details && typeof body.details === "object" ? body.details : null;
  return validateGame({
    id: body.gameId,
    mode: body.mode,
    bracket: body.bracket,
    offered: body.offered,
    picked: body.picked,
    placement: body.placement,
    day: today,
    ...(d && {
      turns: d.turns,
      tier: d.tier,
      triples: d.triples,
      anomaly: d.anomaly,
      tribes: d.tribes,
      comp: d.comp,
      board: Array.isArray(d.board) ? d.board.map((m) => ({ c: m?.card, g: m?.golden === true, a: m?.atk, h: m?.hp })) : [],
      trinkets: { o: d.trinkets?.offered ?? [], p: d.trinkets?.picked ?? [] },
      tierups: d.tierUps,
      combats: Array.isArray(d.combats) ? d.combats.map((c) => [c?.turn, c?.result, c?.taken, c?.dealt, c?.win ?? null]) : [],
    }),
  }, today);
}

// ---- Reading ---------------------------------------------------------------------------------------------

async function stats(request, env, ctx, url) {
  const cache = caches.default;
  const hit = await cache.match(request);
  if (hit) return hit;

  const mode = url.searchParams.get("mode") === "duos" ? "duos" : "solo";
  const row = await env.DB.prepare("SELECT body FROM snap WHERE mode = ?1").bind(mode).first();
  let body = row?.body ?? (await rebuild(env, mode, true));
  if (url.pathname === "/v1/heroes") {
    const s = JSON.parse(body);
    body = JSON.stringify({ mode: s.mode, days: s.days, minRating: 0, updated: s.updated, games: s.games, heroes: s.heroes });
  }

  const response = reply(body, 200, { "cache-control": "public, max-age=1800" });
  ctx.waitUntil(cache.put(request, response.clone()));
  return response;
}

const round = (x) => (x === null || x === undefined ? null : Math.round(x * 1000) / 1000);

/**
 * The Battlegrounds base stats from the meta table (migrations/0004_meta.sql, shipped empty), stored as numbered
 * chunks (one D1 row each). The worker makes no third-party stats calls.
 */
async function meta(request, env, ctx) {
  const cache = caches.default;
  const hit = await cache.match(request);
  if (hit) return hit;

  const { results } = await env.DB.prepare("SELECT body FROM meta ORDER BY seq").all();
  if (results.length === 0) return reply({ error: "meta not imported" }, 503);

  const response = reply(results.map((row) => row.body).join(""), 200, { "cache-control": "public, max-age=1800" });
  ctx.waitUntil(cache.put(request, response.clone()));
  return response;
}

/** Works the totals out again and stores them. Skipped when no game came in and the totals are under a day old. */
export async function rebuild(env, mode, force) {
  const last = (await env.DB.prepare("SELECT MAX(rowid) AS r FROM g").first())?.r ?? 0;
  if (!force) {
    const snap = await env.DB.prepare("SELECT last_row, built FROM snap WHERE mode = ?1").bind(mode).first();
    if (snap && snap.last_row === last && Date.now() - snap.built < DAY) return null;
  }

  const since = Math.floor(Date.now() / DAY) - WINDOW_DAYS;
  const top = mode === "duos" ? 2 : 4;
  const w = "g.mode = ?1 AND g.day >= ?2";
  const q = (sql) => env.DB.prepare(sql).bind(mode, since);
  const q3 = (sql) => env.DB.prepare(sql).bind(mode, since, top);
  const [totals, heroes, offers, boards, trinketOffers, trinketPicks, comps, tierups, odds, choiceOffers, choicePicks, plays] = (await env.DB.batch([
    q(`SELECT COUNT(*) AS games, SUM(g.turns IS NOT NULL) AS detailed FROM g WHERE ${w}`),
    q3(`SELECT g.hero AS hero, COUNT(*) AS n, AVG(g.placement) AS avg, SUM(g.placement <= ?3) AS top, SUM(g.placement = 1) AS first
        FROM g WHERE ${w} GROUP BY g.hero`),
    q(`SELECT j.value AS hero, COUNT(*) AS n, SUM(j.value = g.hero) AS p FROM g, json_each(g.offers) j WHERE ${w} GROUP BY j.value`),
    q(`SELECT c, COUNT(*) AS n, AVG(pl) AS avg FROM
         (SELECT DISTINCT g.rowid AS r, json_extract(j.value, '$.c') AS c, g.placement AS pl FROM g, json_each(g.board) j WHERE ${w})
       GROUP BY c HAVING n >= 3`),
    q(`SELECT j.value AS card, COUNT(*) AS n FROM g, json_each(g.trinkets, '$.o') j WHERE ${w} GROUP BY j.value`),
    q(`SELECT j.value AS card, COUNT(*) AS n, AVG(g.placement) AS avg FROM g, json_each(g.trinkets, '$.p') j WHERE ${w} GROUP BY j.value`),
    q3(`SELECT g.comp AS comp, COUNT(*) AS n, AVG(g.placement) AS avg, SUM(g.placement <= ?3) AS top, SUM(g.placement = 1) AS first
        FROM g WHERE ${w} AND g.comp IS NOT NULL GROUP BY g.comp`),
    q(`SELECT json_extract(j.value, '$[1]') AS tier, COUNT(*) AS n, AVG(json_extract(j.value, '$[0]')) AS turn, AVG(g.placement) AS avg
       FROM g, json_each(g.tierups) j WHERE ${w} GROUP BY 1`),
    q(`SELECT COUNT(*) AS n, AVG(json_extract(j.value, '$[4]')) AS predicted, AVG(json_extract(j.value, '$[1]') = 'W') AS won
       FROM g, json_each(g.combats) j WHERE ${w} AND json_extract(j.value, '$[4]') IS NOT NULL`),
    q(`SELECT json_extract(c.value, '$[1]') AS kind, o.value AS card, COUNT(*) AS n
       FROM g, json_each(g.choices) c, json_each(c.value, '$[3]') o WHERE ${w} GROUP BY kind, card HAVING n >= 3`),
    q(`SELECT json_extract(c.value, '$[1]') AS kind, p.value AS card, COUNT(*) AS n, AVG(g.placement) AS avg
       FROM g, json_each(g.choices) c, json_each(c.value, '$[4]') p WHERE ${w} GROUP BY kind, card`),
    q(`SELECT card, COUNT(*) AS n, AVG(pl) AS avg, AVG(t) AS turn FROM
         (SELECT g.rowid AS r, json_extract(j.value, '$[1]') AS card, MIN(json_extract(j.value, '$[0]')) AS t, g.placement AS pl
          FROM g, json_each(g.plays) j WHERE ${w} GROUP BY g.rowid, card)
       GROUP BY card HAVING n >= 3`),
  ])).map((r) => r.results);

  const offerBy = new Map(offers.map((r) => [r.hero, r]));
  const pickBy = new Map(trinketPicks.map((r) => [r.card, r]));
  const choicePickBy = new Map(choicePicks.map((r) => [`${r.kind}|${r.card}`, r]));
  const body = JSON.stringify({
    mode,
    days: WINDOW_DAYS,
    updated: new Date().toISOString(),
    games: totals[0]?.games ?? 0,
    detailedGames: totals[0]?.detailed ?? 0,
    heroes: heroes.map((r) => ({
      hero: r.hero, games: r.n, avgPlacement: round(r.avg), top: round(r.top / r.n), first: round(r.first / r.n),
      offered: offerBy.get(r.hero)?.n ?? 0, picked: offerBy.get(r.hero)?.p ?? 0,
    })),
    cards: boards.map((r) => ({ card: r.c, games: r.n, avgPlacement: round(r.avg) })),
    trinkets: trinketOffers.map((r) => ({
      card: r.card, offered: r.n, picked: pickBy.get(r.card)?.n ?? 0, avgPlacement: round(pickBy.get(r.card)?.avg),
    })),
    comps: comps.map((r) => ({ comp: r.comp, games: r.n, avgPlacement: round(r.avg), top: round(r.top / r.n), first: round(r.first / r.n) })),
    tierUps: tierups.map((r) => ({ tier: r.tier, games: r.n, avgTurn: round(r.turn), avgPlacement: round(r.avg) })),
    combatOdds: { fights: odds[0]?.n ?? 0, predictedWin: round(odds[0]?.predicted), actualWin: round(odds[0]?.won) },
    choices: choiceOffers.map((r) => {
      const pick = choicePickBy.get(`${r.kind}|${r.card}`);
      return { kind: r.kind, card: r.card, offered: r.n, picked: pick?.n ?? 0, avgPlacement: round(pick?.avg) };
    }),
    plays: plays.map((r) => ({ card: r.card, games: r.n, avgPlacement: round(r.avg), avgTurn: round(r.turn) })),
  });

  await env.DB.prepare(
    `INSERT INTO snap (mode, last_row, built, body) VALUES (?1, ?2, ?3, ?4)
     ON CONFLICT(mode) DO UPDATE SET last_row = ?2, built = ?3, body = ?4`)
    .bind(mode, last, Date.now(), body).run();
  return body;
}
