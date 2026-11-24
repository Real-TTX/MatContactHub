#!/usr/bin/env node
// Writes the logo of MatContactHub in its levels of detail (docs/logo and src/MatContactHub/wwwroot/img/logo, the favicon of wwwroot):
//   mark      the full mark (64 x 64)
//   favicon   the small mark (32 x 32)
//   maskable  the mark without corners, the content inside the safe zone (Android adaptive icons)
//   mono      one colour (currentColor) for places that tint it
//   logo      the mark with the word mark (light surfaces), logo-dark (dark surfaces)
// PLACEHOLDER of the foundation: a neutral mark with the letters "CH" on the provisional accent (teal). The logo module replaces this script,
// the files it writes and the glyph mat-i-matcontacthub of wwwroot/img/app-icons.svg, and fixes the accent.
// The PNG icons are made from these files with scripts/icons.ps1 (Edge headless).
//   node scripts/logo.js
const fs = require('fs');
const path = require('path');

const accent = '#0F766E';
const light = '#5EEAD4';
const root = path.join(__dirname, '..');
const font = `font-family="'Segoe UI','Helvetica Neue',Arial,sans-serif" font-weight="700"`;

const letters = (x, y, size, fill) =>
  `<text x="${x}" y="${y}" ${font} font-size="${size}" text-anchor="middle" letter-spacing="-1" fill="${fill}">CH</text>`;

const files = {
  'matcontacthub-mark.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatContactHub">\n` +
    `  <rect width="64" height="64" rx="14" fill="${accent}"/>\n  ${letters(32, 42, 28, '#fff')}\n</svg>\n`,
  'matcontacthub-favicon.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" role="img" aria-label="MatContactHub">\n` +
    `  <rect width="32" height="32" rx="7" fill="${accent}"/>\n  ${letters(16, 22, 15, '#fff')}\n</svg>\n`,
  'matcontacthub-maskable.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatContactHub">\n` +
    `  <rect width="64" height="64" fill="${accent}"/>\n  ${letters(32, 40, 20, '#fff')}\n</svg>\n`,
  'matcontacthub-mono.svg':
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="MatContactHub">\n` +
    `  <rect x="3" y="3" width="58" height="58" rx="12" fill="none" stroke="currentColor" stroke-width="4"/>\n  ${letters(32, 42, 26, 'currentColor')}\n</svg>\n`,
  'matcontacthub-logo.svg': wordmark('#1F1E1B', accent),
  'matcontacthub-logo-dark.svg': wordmark('#F4F2EF', light),
};

function wordmark(text, second) {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 236 40" width="236" height="40" role="img" aria-label="MatContactHub">\n` +
    `  <rect x="2" y="2" width="36" height="36" rx="8" fill="${accent}"/>${letters(20, 26.5, 16, '#fff')}\n` +
    `  <text x="48" y="28.5" ${font} font-size="22" letter-spacing="-0.4" fill="${text}">Mat<tspan fill="${second}">ContactHub</tspan></text>\n</svg>\n`;
}

for (const dir of [path.join(root, 'docs', 'logo'), path.join(root, 'src', 'MatContactHub', 'wwwroot', 'img', 'logo')]) {
  fs.mkdirSync(dir, { recursive: true });
  for (const [name, svg] of Object.entries(files)) {
    fs.writeFileSync(path.join(dir, name), svg);
  }
}

fs.writeFileSync(path.join(root, 'src', 'MatContactHub', 'wwwroot', 'favicon.svg'), files['matcontacthub-favicon.svg']);
console.log(`wrote ${Object.keys(files).length} files into docs/logo and wwwroot/img/logo, and wwwroot/favicon.svg`);
