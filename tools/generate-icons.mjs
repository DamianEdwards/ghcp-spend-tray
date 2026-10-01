import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Resvg } from '@resvg/resvg-js';

const assets = join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'GHCPSpendTray.App', 'Assets');
const regular = readFileSync(join(assets, 'ghcpspendtray-logo.svg'), 'utf8');
const small = readFileSync(join(assets, 'ghcpspendtray-logo-small.svg'), 'utf8');
const sizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];
const icoSizes = [16, 20, 24, 32, 48, 64, 256];
const check = process.argv.includes('--check');
assert(process.argv.slice(2).every(arg => arg === '--check'), 'Only --check is supported.');
const images = new Map();

function render(svg, size, opaque = false) {
  const result = new Resvg(svg, { fitTo: { mode: 'width', value: size }, font: { loadSystemFonts: false } }).render();
  assert.equal(result.width, size);
  assert.equal(result.height, size);
  const alpha = (x, y) => result.pixels[(y * size + x) * 4 + 3];
  for (const [x, y] of [[0, 0], [size - 1, 0], [0, size - 1], [size - 1, size - 1]]) {
    assert.equal(alpha(x, y), opaque ? 255 : 0, `Incorrect corner alpha at ${size}px`);
  }
  assert.equal(alpha(Math.floor(size / 2), Math.floor(size / 2)), 255);
  return result.asPng();
}

function image(size) {
  if (!images.has(size)) images.set(size, render(size <= 24 ? small : regular, size));
  return images.get(size);
}

function output(name, bytes) {
  const path = join(assets, name);
  if (check) assert(readFileSync(path).equals(bytes), `${name} differs from its SVG master; run npm run generate.`);
  else writeFileSync(path, bytes);
}

output('ghcpspendtray-logo.png', image(512));
output('ghcpspendtray-badge.png', render(
  regular.replace('<g id="disc">', '<rect width="512" height="512" fill="#14171d"/><g id="disc">'), 512, true));
for (const [name, size] of [['Square44x44Logo', 44], ['Square150x150Logo', 150], ['StoreLogo', 50]]) {
  output(`${name}.png`, image(size));
}
for (const size of sizes) {
  for (const form of ['', '_altform-unplated', '_altform-lightunplated']) {
    output(`Square44x44Logo.targetsize-${size}${form}.png`, image(size));
  }
}

// PNG-backed ICO frames share exact pixels with the qualified shell assets.
const directory = Buffer.alloc(6 + icoSizes.length * 16);
directory.writeUInt16LE(1, 2);
directory.writeUInt16LE(icoSizes.length, 4);
let offset = directory.length;
for (const [index, size] of icoSizes.entries()) {
  const entry = 6 + index * 16;
  const png = image(size);
  directory[entry] = directory[entry + 1] = size === 256 ? 0 : size;
  directory.writeUInt16LE(1, entry + 4);
  directory.writeUInt16LE(32, entry + 6);
  directory.writeUInt32LE(png.length, entry + 8);
  directory.writeUInt32LE(offset, entry + 12);
  offset += png.length;
}
output('GHCPSpendTray.ico', Buffer.concat([directory, ...icoSizes.map(image)]));
console.log(`${check ? 'Verified' : 'Generated'} all static artwork, 42 shell variants and ${icoSizes.length} ICO frames from the approved SVG masters.`);
