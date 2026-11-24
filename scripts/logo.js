#!/usr/bin/env node
// Writes the logo of MatContactHub in its levels of detail (docs/logo and src/MatContactHub/wwwroot/img/logo, the favicon of wwwroot):
//   mark      the full mark (64 x 64): an address card with a person, and a directory tree that branches from it to three nodes
//             (the contacts go out to phones, mail clients and apps)
//   favicon   the small mark (32 x 32, every edge on the 16 px grid so it is sharp in a browser tab): a wider card with a bigger person,
//             the tree reduced to three short links with their nodes (a trunk with branches is mush at 16 px; the nodes are
//             squares with small round corners: a circle of 2 px would only be pale pink)
//   maskable  the mark without corners, the content inside the safe zone (Android adaptive icons)
//   mono      one colour (currentColor) for places that tint it; the person is cut out of the card
//   logo      the mark with the word mark (light surfaces), logo-dark (dark surfaces)
// It also writes the glyph of the menu (24 x 24, a line icon like the toolkit's), the symbol mat-i-matcontacthub of wwwroot/img/app-icons.svg,
// and docs/logo/preview.html, the sheet of all variants that scripts/icons.ps1 draws into docs/logo/preview.png.
// The accent is a fuchsia #b81d97 (white text on it 5.7:1); keep it equal to Ui.Accent and wwwroot/css/app.css.
// The PNG icons are made from these files with scripts/icons.ps1 (Edge headless).
//   node scripts/logo.js
const fs = require('fs');
const path = require('path');

const accent = '#B81D97';
const tint = '#F6CFE9';
const light = '#E98BCB';
const root = path.join(__dirname, '..');
const font = `font-family="'Segoe UI','Helvetica Neue',Arial,sans-serif" font-weight="700"`;

// the person on the card (64 grid): the head and the shoulders
const person = (fill) =>
  `<circle cx="23" cy="26" r="6.5" fill="${fill}"/><path d="M12 45C12 38.6 16.9 34.5 23 34.5S34 38.6 34 45Z" fill="${fill}"/>`;

// the directory tree (64 grid): a link from the card to the trunk, three branches, three nodes
const tree = (line, node) =>
  `<path d="M39 32h5.5M44.5 19v26M44.5 19h4M44.5 32h4M44.5 45h4" stroke="${line}" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" fill="none"/>` +
  `<circle cx="52.5" cy="19" r="4.5" fill="${node}"/><circle cx="52.5" cy="32" r="4.5" fill="${node}"/><circle cx="52.5" cy="45" r="4.5" fill="${node}"/>`;

// the inside of the 64 x 64 mark (without its background), so the logo and the maskable icon draw the same shapes
const markShapes = `<rect x="7" y="12" width="32" height="40" rx="6" fill="#fff"/>${person(accent)}${tree(tint, '#fff')}`;

// the glyph of the menu: 24 x 24, a line of 2 with round ends (the class mat-icon of the toolkit), the colour is the text colour
const glyph = `<rect x='2' y='3.5' width='14' height='17' rx='2.5'/><circle cx='9' cy='9.5' r='2.25'/><path d='M5 17.5a4 4 0 0 1 8 0'/>` +
  `<path d='M16 12h2M18 7v10M18 7h.5M18 17h.5'/><path d='M21.25 7h.01M21.25 12h.01M21.25 17h.01' stroke-width='3.5'/>`;

const svg = (viewBox, body, extra = '') =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${viewBox}"${extra} role="img" aria-label="MatContactHub">\n  ${body}\n</svg>\n`;

const files = {
  'matcontacthub-mark.svg': svg('0 0 64 64', `<rect width="64" height="64" rx="14" fill="${accent}"/>\n  ${markShapes}`),
  'matcontacthub-favicon.svg': svg('0 0 32 32',
    `<rect width="32" height="32" rx="7" fill="${accent}"/>
` +
    `  <rect x="2" y="4" width="20" height="24" rx="3" fill="#fff"/>
` +
    `  <circle cx="12" cy="12" r="4" fill="${accent}"/><path d="M4 24C4 20 7.6 18 12 18S20 20 20 24Z" fill="${accent}"/>
` +
    `  <path d="M22 8h2M22 16h2M22 24h2" stroke="${tint}" stroke-width="2"/>
` +
    `  <rect x="24" y="6" width="4" height="4" rx="1" fill="#fff"/><rect x="24" y="14" width="4" height="4" rx="1" fill="#fff"/><rect x="24" y="22" width="4" height="4" rx="1" fill="#fff"/>`),
  'matcontacthub-maskable.svg': svg('0 0 64 64',
    `<rect width="64" height="64" fill="${accent}"/>\n  <g transform="translate(9.6 9.6) scale(0.7)">${markShapes}</g>`),
  'matcontacthub-mono.svg': svg('0 0 64 64',
    `<mask id="person"><rect width="64" height="64" fill="#fff"/>${person('#000')}</mask>\n` +
    `  <rect x="7" y="12" width="32" height="40" rx="6" fill="currentColor" mask="url(#person)"/>${tree('currentColor', 'currentColor')}`),
  'matcontacthub-logo.svg': wordmark('#1F1E1B', accent),
  'matcontacthub-logo-dark.svg': wordmark('#F4F2EF', light),
};

function wordmark(text, second) {
  return svg('0 0 216 40',
    `<rect x="2" y="2" width="36" height="36" rx="8" fill="${accent}"/><g transform="translate(2 2) scale(0.5625)">${markShapes}</g>\n` +
    `  <text x="48" y="28.5" ${font} font-size="22" letter-spacing="-0.4" fill="${text}">Mat<tspan fill="${second}">ContactHub</tspan></text>`,
    ' width="216" height="40"');
}

for (const dir of [path.join(root, 'docs', 'logo'), path.join(root, 'src', 'MatContactHub', 'wwwroot', 'img', 'logo')]) {
  fs.mkdirSync(dir, { recursive: true });
  for (const [name, content] of Object.entries(files)) {
    fs.writeFileSync(path.join(dir, name), content);
  }
}

fs.writeFileSync(path.join(root, 'src', 'MatContactHub', 'wwwroot', 'favicon.svg'), files['matcontacthub-favicon.svg']);

// the glyph of the menu in the sprite of the app
const spritePath = path.join(root, 'src', 'MatContactHub', 'wwwroot', 'img', 'app-icons.svg');
const sprite = fs.readFileSync(spritePath, 'utf8');
const symbol = `<symbol id="mat-i-matcontacthub" viewBox="0 0 24 24">${glyph}</symbol>`;
const pattern = /<symbol id="mat-i-matcontacthub"[^>]*>[\s\S]*?<\/symbol>/;
if (!pattern.test(sprite)) {
  throw new Error('app-icons.svg has no symbol mat-i-matcontacthub');
}
fs.writeFileSync(spritePath, sprite.replace(pattern, symbol));

// the preview sheet: every variant on light and dark surfaces, small and large (scripts/icons.ps1 draws it into preview.png)
const icon = (name, size) => `<img src="${name}" width="${size}" height="${size}" alt="">`;
const tile = (size) => `<span class="tile" style="width:${size}px;height:${size}px"><svg viewBox="0 0 24 24" style="width:56%;height:56%">${glyph}</svg></span>`;
const strip = (theme) => `<section class="${theme}">
  <h2>${theme === 'light' ? 'Light surface' : 'Dark surface'}</h2>
  <div class="row"><span class="label">mark</span>${[16, 24, 32, 48, 64, 96, 192].map(s => icon('matcontacthub-mark.svg', s)).join('')}</div>
  <div class="row"><span class="label">favicon</span>${[16, 32].map(s => icon('matcontacthub-favicon.svg', s)).join('')}
    <img class="px" src="../../src/MatContactHub/wwwroot/img/favicon-16.png" width="128" height="128" alt="">
    <img class="px" src="../../src/MatContactHub/wwwroot/img/favicon-32.png" width="128" height="128" alt=""><span class="note">16 and 32 px, eight times</span></div>
  <div class="row"><span class="label">maskable</span>${icon('matcontacthub-maskable.svg', 96)}<img class="mask circle" src="matcontacthub-maskable.svg" width="96" height="96" alt=""><img class="mask squircle" src="matcontacthub-maskable.svg" width="96" height="96" alt=""><span class="note">full, circle, squircle</span></div>
  <div class="row"><span class="label">mono</span><span class="mono">${[16, 24, 48, 96].map(s => files['matcontacthub-mono.svg'].replace('<svg ', `<svg width="${s}" height="${s}" `)).join('')}</span></div>
  <div class="row"><span class="label">menu glyph</span>${[28, 38, 56, 88].map(tile).join('')}<span class="glyph">${[16, 20, 24, 48].map(s => `<svg viewBox="0 0 24 24" width="${s}" height="${s}">${glyph}</svg>`).join('')}</span></div>
  <div class="row"><span class="label">logo</span><img src="matcontacthub-logo${theme === 'light' ? '' : '-dark'}.svg" height="40" alt=""><img src="matcontacthub-logo${theme === 'light' ? '' : '-dark'}.svg" height="80" alt=""></div>
</section>`;
const preview = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>MatContactHub logo</title>
<style>
  body { margin: 0; font: 13px 'Segoe UI', Arial, sans-serif; width: 1100px; }
  section { padding: 18px 24px; }
  .light { background: #f5f4f1; color: #1f1e1b; }
  .dark { background: #151514; color: #f4f2ef; }
  h1 { margin: 0; padding: 18px 24px 0; font-size: 20px; background: #f5f4f1; }
  h1 + p { margin: 0; padding: 4px 24px 0; background: #f5f4f1; color: #6b6862; }
  h2 { margin: 0 0 10px; font-size: 14px; }
  .row { display: flex; align-items: center; gap: 16px; margin: 10px 0; }
  .label { width: 90px; color: #8a867f; }
  .note { color: #8a867f; }
  .px { image-rendering: pixelated; }
  .mask.circle { clip-path: circle(50%); }
  .mask.squircle { border-radius: 30%; }
  .mono { display: flex; gap: 16px; align-items: center; color: ${accent}; }
  .dark .mono { color: ${light}; }
  .tile { display: inline-grid; place-items: center; border-radius: 22%; background: ${accent}; color: #fff; }
  .dark .tile { background: color-mix(in oklab, ${accent} 74%, white); color: #0b1020; }
  .tile svg, .glyph svg { fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
  .glyph { display: flex; gap: 16px; align-items: center; margin-left: 16px; }
</style></head>
<body>
<h1>MatContactHub</h1>
<p>Accent ${accent.toLowerCase()} (white text 5.7:1), links on dark surfaces ${light.toLowerCase()}. Written by scripts/logo.js, drawn by scripts/icons.ps1.</p>
${strip('light')}
${strip('dark')}
</body></html>
`;
fs.writeFileSync(path.join(root, 'docs', 'logo', 'preview.html'), preview);

console.log(`wrote ${Object.keys(files).length} files into docs/logo and wwwroot/img/logo, wwwroot/favicon.svg, the glyph in app-icons.svg and docs/logo/preview.html`);
