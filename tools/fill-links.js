// Fills "url" of every asset in manifest.json from an rclone lsjson listing of the Drive folder.
// usage: rclone lsjson -R --files-only reima:REIMA > list.json
//        node tools/fill-links.js <manifest.json> <list.json>
// The REIMA folder must be shared "anyone with the link" (rclone link reima:REIMA); files inherit it.
const fs = require('fs');
const [manifestPath, listPath] = process.argv.slice(2);
if (!listPath) { console.error('usage: node fill-links.js <manifest.json> <list.json>'); process.exit(1); }
const raw = fs.readFileSync(manifestPath, 'utf8');
const header = raw.match(/^(\s*\/\/.*\n)*/)[0];
const manifest = JSON.parse(raw.replace(/^\s*\/\/.*$/gm, ''));
const byName = new Map();
for (const f of JSON.parse(fs.readFileSync(listPath, 'utf8').replace(/^﻿/, ''))) byName.set(f.Name.toLowerCase(), f);
const link = id => `https://drive.usercontent.google.com/download?id=${id}&export=download&confirm=t`;
let set = 0; const missing = [];
for (const a of manifest.assets) {
  if (!a.file) continue;
  if (a.url && /github\.com/.test(a.url)) continue; // comes straight from GitHub
  const f = byName.get(a.file.toLowerCase());
  if (!f) { if (!a.url || a.packFrom) missing.push(a.name); continue; }
  if (!a.source && a.url && /moddb\.com/.test(a.url)) a.source = a.url;
  a.url = link(f.ID);
  delete a.manualHint;
  set++;
}
fs.writeFileSync(manifestPath, header + JSON.stringify(manifest, null, 2) + '\n');
console.log(`links: ${set}, sem arquivo no Drive: ${missing.length}`);
missing.forEach(m => console.log('  - ' + m));
