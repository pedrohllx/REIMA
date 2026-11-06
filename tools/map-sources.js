// Finds the public page (ModDB) of every packed ("packFrom") mod of the recipe, without touching ModDB:
//  1) MO2 meta.ini "url=" of the installed mod folder
//  2) same title in GAMMA's official list (GitHub) or the local installer's mods.txt
//  3) otherwise a ModDB search link the user opens in their own browser.
// usage: node tools/map-sources.js <MO2 root> <manifest.json> <gamma list.txt> [--write]
// Writes page/pageKind into the recipe with --write; prints a report either way.
const fs = require('fs'), path = require('path');
const [mo2, manifestPath, listPath] = process.argv.slice(2).filter(a => !a.startsWith('--'));
const write = process.argv.includes('--write');

const raw = fs.readFileSync(manifestPath, 'utf8');
const header = raw.match(/^(\s*\/\/.*\n)*/)[0];
const manifest = JSON.parse(raw.replace(/^\s*\/\/.*$/gm, ''));

// "xx-45- Stealth Overhaul v2.31 - xcvb" -> "stealthoverhaul"
const key = s => s
  .replace(/^(xx-)?(\d+-\s*)?(\[[^\]]*\]\s*)?/i, '')
  .replace(/\(.*?\)/g, '')
  .replace(/\s+-\s+[^-]*$/, '')                 // " - author"
  .replace(/\bv?\d+(\.\d+)+[a-z]?\b/gi, '')      // versions
  .toLowerCase().replace(/[^a-z0-9]+/g, '');

const known = new Map(); // key -> {page, title}
const addList = (file, cols) => {
  if (!fs.existsSync(file)) return;
  for (const line of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
    const c = line.split('\t').map(x => x.trim());
    if (c.length < 5) continue;
    const [url, , author, title, page] = c;
    const p = /moddb\.com\/mods\//.test(page) ? page : /github\.com/.test(url) ? url : null;
    if (!p) continue;
    for (const k of [key(title), key(c[1] || '')]) if (k && !known.has(k)) known.set(k, { page: p, title: `${title} ${author}` });
  }
};
addList(listPath);
addList(path.join(mo2, ".Grok's Modpack Installer", 'mods.txt'));

const report = { meta: [], list: [], search: [] };
for (const a of manifest.assets.filter(a => a.packFrom)) {
  let page = null, kind = null;
  const meta = path.join(mo2, 'mods', a.packFrom, 'meta.ini');
  if (fs.existsSync(meta)) {
    const u = (fs.readFileSync(meta, 'utf8').match(/^url=(.+)$/m) || [])[1]?.trim();
    if (u && /moddb\.com\/mods\/|github\.com/.test(u)) { page = u; kind = 'meta'; }
  }
  if (!page) {
    const hit = known.get(key(a.packFrom));
    if (hit) { page = hit.page; kind = 'list'; }
  }
  if (!page) {
    const q = a.packFrom.replace(/^(xx-)?(\d+-\s*)?(\[[^\]]*\]\s*)?/i, '').replace(/\s+-\s+[^-]*$/, '').replace(/\(.*?\)/g, '').trim();
    page = 'https://www.moddb.com/search?q=' + encodeURIComponent(q);
    kind = 'search';
  }
  report[kind].push(`${a.packFrom}  ->  ${page}`);
  if (write) { a.page = page; a.pageKind = kind; }
}
if (write) fs.writeFileSync(manifestPath, header + JSON.stringify(manifest, null, 2) + '\n');
for (const [k, v] of Object.entries(report)) console.log(`${k}: ${v.length}`);
if (process.argv.includes('--verbose')) for (const [k, v] of Object.entries(report)) { console.log('\n## ' + k); v.forEach(x => console.log(x)); }
