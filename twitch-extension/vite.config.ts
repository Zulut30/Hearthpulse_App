import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { gzipSync } from 'node:zlib';
import preact from '@preact/preset-vite';
import type { Plugin } from 'vite';
import { defineConfig } from 'vitest/config';

/**
 * Dev server only: answers GET /api/v1/twitch/ext/snapshot like the site does, with the sample deck in the contract
 * envelope, so the cold start can be checked in the browser with ?mock. A token ending in ".empty" gets 204.
 */
function mockSnapshot(): Plugin {
  return {
    name: 'hearthpulse-mock-snapshot',
    apply: 'serve',
    configureServer(server) {
      server.middlewares.use('/api/v1/twitch/ext/snapshot', (req, res) => {
        const auth = req.headers.authorization ?? '';
        if (!auth.startsWith('Bearer ')) {
          res.statusCode = 401;
          res.end();
          return;
        }
        res.setHeader('Cache-Control', 'no-store');
        if (auth.endsWith('.empty') || auth.endsWith('.none')) {
          res.statusCode = 204;
          res.end();
          return;
        }
        const sample = JSON.parse(readFileSync(resolve(__dirname, 'src/dev/sample-deck.json'), 'utf8'));
        const ru = /locale=ru/i.test(req.headers.referer ?? '');
        sample.deck.name = ru ? sample.deck.nameRu : sample.deck.name;
        delete sample.deck.nameRu;
        sample.deck.cards = sample.deck.cards
          .map(({ nameRu, ...card }: { nameRu: string; name: string; cost: number }) => ({ ...card, name: ru ? nameRu : card.name }))
          .sort((a: { cost: number; name: string }, b: { cost: number; name: string }) => a.cost - b.cost || a.name.localeCompare(b.name));
        const z = gzipSync(Buffer.from(JSON.stringify(sample))).toString('base64');
        res.setHeader('Content-Type', 'application/json');
        res.end(JSON.stringify({ v: 1, seq: 100, at: Date.now() - 60_000, z }));
      });
    },
  };
}

// Three Twitch views, one page each. Twitch serves the zip from its CDN under a versioned path, so every asset URL is
// relative (base './'). The output stays unminified: Twitch review reads the JS, and minified code would need the
// sources sent through a form. No inline scripts and no module-preload polyfill (CSP: script-src is our files only).
export default defineConfig({
  base: './',
  plugins: [preact(), mockSnapshot()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    minify: false,
    sourcemap: false,
    modulePreload: { polyfill: false },
    assetsInlineLimit: 0,
    rollupOptions: {
      input: {
        video_overlay: resolve(__dirname, 'video_overlay.html'),
        panel: resolve(__dirname, 'panel.html'),
        config: resolve(__dirname, 'config.html'),
      },
    },
  },
  server: { port: 8080, strictPort: true },
  test: {
    environment: 'node',
    include: ['test/**/*.test.ts'],
  },
});
