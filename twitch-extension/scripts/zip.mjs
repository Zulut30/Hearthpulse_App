// npm run zip: packs dist/ into hearthpulse-twitch-<version>.zip for Twitch Hosted Test (files at the zip root, as
// Twitch expects). No dependencies: deflate and CRC-32 from node:zlib, the ZIP container written by hand.
// Fails when dist/ is missing, has no view pages, or the zip is over 1 MB (task AC8, Twitch mobile first-load limit).
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { crc32, deflateRawSync } from 'node:zlib';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const dist = join(root, 'dist');
const { version } = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));
const out = join(root, `hearthpulse-twitch-${version}.zip`);
const LIMIT = 1024 * 1024;

function walk(dir) {
  return readdirSync(dir)
    .sort()
    .flatMap((name) => {
      const path = join(dir, name);
      return statSync(path).isDirectory() ? walk(path) : [path];
    });
}

let files;
try {
  files = walk(dist);
} catch {
  console.error('dist/ is missing: run npm run build first');
  process.exit(1);
}
for (const page of ['video_overlay.html', 'panel.html', 'config.html']) {
  if (!files.includes(join(dist, page))) {
    console.error(`dist/${page} is missing: run npm run build first`);
    process.exit(1);
  }
}

// DOS time 1980-01-01 00:00 for every entry, so the same build gives the same zip.
const DOS_TIME = 0;
const DOS_DATE = (0 << 9) | (1 << 5) | 1;
const locals = [];
const centrals = [];
let offset = 0;
for (const file of files) {
  const name = Buffer.from(relative(dist, file).split('\\').join('/'), 'utf8');
  const data = readFileSync(file);
  const packed = deflateRawSync(data, { level: 9 });
  const crc = crc32(data) >>> 0;
  const local = Buffer.alloc(30);
  local.writeUInt32LE(0x04034b50, 0);
  local.writeUInt16LE(20, 4); // version needed
  local.writeUInt16LE(0x0800, 6); // UTF-8 names
  local.writeUInt16LE(8, 8); // deflate
  local.writeUInt16LE(DOS_TIME, 10);
  local.writeUInt16LE(DOS_DATE, 12);
  local.writeUInt32LE(crc, 14);
  local.writeUInt32LE(packed.length, 18);
  local.writeUInt32LE(data.length, 22);
  local.writeUInt16LE(name.length, 26);
  local.writeUInt16LE(0, 28);
  const central = Buffer.alloc(46);
  central.writeUInt32LE(0x02014b50, 0);
  central.writeUInt16LE(20, 4); // made by
  central.writeUInt16LE(20, 6);
  central.writeUInt16LE(0x0800, 8);
  central.writeUInt16LE(8, 10);
  central.writeUInt16LE(DOS_TIME, 12);
  central.writeUInt16LE(DOS_DATE, 14);
  central.writeUInt32LE(crc, 16);
  central.writeUInt32LE(packed.length, 20);
  central.writeUInt32LE(data.length, 24);
  central.writeUInt16LE(name.length, 28);
  central.writeUInt32LE(offset, 42);
  locals.push(local, name, packed);
  centrals.push(central, name);
  offset += local.length + name.length + packed.length;
}
const centralSize = centrals.reduce((sum, part) => sum + part.length, 0);
const end = Buffer.alloc(22);
end.writeUInt32LE(0x06054b50, 0);
end.writeUInt16LE(files.length, 8);
end.writeUInt16LE(files.length, 10);
end.writeUInt32LE(centralSize, 12);
end.writeUInt32LE(offset, 16);
const zip = Buffer.concat([...locals, ...centrals, end]);
writeFileSync(out, zip);

console.log(`${relative(root, out)}: ${files.length} files, ${zip.length} bytes (${(zip.length / 1024).toFixed(1)} KB)`);
if (zip.length > LIMIT) {
  console.error(`zip is over ${LIMIT} bytes`);
  process.exit(1);
}
