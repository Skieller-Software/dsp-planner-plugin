// Generates a 256x256 icon.png (Thunderstore requirement) — a Dyson-sphere ring
// motif in the planner's cyan on a dark background. No deps: hand-rolled PNG via zlib.
import zlib from 'zlib';
import { writeFileSync } from 'fs';

const S = 256, cx = 127.5, cy = 127.5;
const bg = [10, 15, 26], cyan = [63, 208, 255], purple = [124, 92, 255];
const px = Buffer.alloc(S * S * 4);

const mix = (a, b, t) => a + (b - a) * t;
function set(x, y, c, alpha) {
  const i = (y * S + x) * 4;
  for (let k = 0; k < 3; k++) px[i + k] = Math.round(mix(px[i + k], c[k], alpha));
  px[i + 3] = 255;
}
// fill background
for (let y = 0; y < S; y++) for (let x = 0; x < S; x++) { const i = (y * S + x) * 4; px[i] = bg[0]; px[i + 1] = bg[1]; px[i + 2] = bg[2]; px[i + 3] = 255; }

// soft coverage for a ring of given radius/width, and for a disc
const ringCov = (d, R, w) => { const e = Math.abs(d - R) - (w / 2 - 1); return e <= 0 ? 1 : Math.max(0, 1 - e); };
const discCov = (d, R) => { const e = d - (R - 1); return e <= 0 ? 1 : Math.max(0, 1 - e); };

for (let y = 0; y < S; y++) for (let x = 0; x < S; x++) {
  const dx = x - cx, dy = y - cy, d = Math.hypot(dx, dy);
  set(x, y, cyan, ringCov(d, 92, 10) * 0.95);   // outer Dyson ring
  set(x, y, cyan, ringCov(d, 58, 4) * 0.6);      // inner orbit
  set(x, y, purple, discCov(d, 16) * 0.95);      // central star
  // three "sail/node" dots on the outer ring
  for (const a of [-Math.PI / 2, Math.PI / 6, Math.PI * 5 / 6]) {
    const nx = cx + Math.cos(a) * 92, ny = cy + Math.sin(a) * 92;
    set(x, y, cyan, discCov(Math.hypot(x - nx, y - ny), 9) * 0.95);
  }
}

// PNG encode: signature + IHDR + IDAT + IEND
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const tb = Buffer.from(type, 'ascii');
  const body = Buffer.concat([tb, data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body) >>> 0);
  return Buffer.concat([len, body, crc]);
}
function crc32(buf) {
  let c = ~0;
  for (let i = 0; i < buf.length; i++) { c ^= buf[i]; for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xEDB88320 & -(c & 1)); }
  return ~c;
}
const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(S, 0); ihdr.writeUInt32BE(S, 4); ihdr[8] = 8; ihdr[9] = 6;  // 8-bit, RGBA
// filtered scanlines (filter byte 0 per row)
const raw = Buffer.alloc(S * (S * 4 + 1));
for (let y = 0; y < S; y++) { raw[y * (S * 4 + 1)] = 0; px.copy(raw, y * (S * 4 + 1) + 1, y * S * 4, (y + 1) * S * 4); }
const idat = zlib.deflateSync(raw, { level: 9 });
const sig = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const png = Buffer.concat([sig, chunk('IHDR', ihdr), chunk('IDAT', idat), chunk('IEND', Buffer.alloc(0))]);
writeFileSync(new URL('./icon.png', import.meta.url), png);
console.log('wrote icon.png', png.length, 'bytes');
