// Maps every packed ("packFrom") mod of the recipe to the same folder inside the community packs (ROMEO, GeorgePack),
// and only accepts it when the content is IDENTICAL to the installed mod (same files, same sizes) — so mods that were
// patched/edited after install (Romeo Extended, my fixes...) keep coming from the Drive.
// Input: "7z l -slt -sccUTF-8" listings of each pack archive (nested archives listed separately).
// usage: node tools/map-packs.js <MO2 root> <manifest.json> <packs.json> [--write]
// packs.json: { "sourcePacks": [...SourcePack], "listings": [ { "pack", "archive", "inner"?, "slt", "modsPrefix" } ] }
//   modsPrefix = folder inside the listing that holds the mod folders ("" for GeorgePack modsNN, "<root>\GAMMA\mods" for ROMEO)
const fs = require('fs'), path = require('path');
const [mo2, manifestPath, packsPath] = process.argv.slice(2).filter(a => !a.startsWith('--'));
const write = process.argv.includes('--write');
const cfg = JSON.parse(fs.readFileSync(packsPath, 'utf8'));

const raw = fs.readFileSync(manifestPath, 'utf8');
const header = raw.match(/^(\s*\/\/.*\n)*/)[0];
const manifest = JSON.parse(raw.replace(/^\s*\/\/.*$/gm, ''));

// --- parse listings: mod folder -> { files: Map(relpath -> size), ref }
const norm = s => s.normalize('NFC').toLowerCase();
const index = new Map(); // norm(mod) -> candidate (later listings override earlier: hotfixes win)
for (const L of cfg.listings) {
  const text = fs.readFileSync(L.slt, 'utf8');
  const prefix = L.modsPrefix ? L.modsPrefix.replace(/[\\/]+$/, '') + '\\' : '';
  const mods = new Map();
  for (const block of text.split(/\r?\n\r?\n/)) {
    const p = block.match(/^Path = (.*)$/m)?.[1]; if (!p) continue;
    if (/^Folder = \+$/m.test(block) || /Attributes = D/.test(block)) continue;
    const size = Number(block.match(/^Size = (\d+)$/m)?.[1] ?? 0);
    if (!p.startsWith(prefix)) continue;
    const rest = p.slice(prefix.length), i = rest.indexOf('\\');
    if (i <= 0) continue;
    const mod = rest.slice(0, i), rel = rest.slice(i + 1);
    if (!mods.has(mod)) mods.set(mod, new Map());
    mods.get(mod).set(norm(rel), size);
  }
  for (const [mod, files] of mods)
    index.set(norm(mod), { files, ref: { pack: L.pack, archive: L.archive, ...(L.inner ? { inner: L.inner } : {}), path: prefix + mod } });
}

// content fingerprint (files + sizes, meta.ini ignored): finds mods that were renamed after copying (e.g. "[GP] ...")
const sig = files => [...files].filter(([r]) => r !== 'meta.ini').sort(([a], [b]) => a < b ? -1 : 1).map(([r, s]) => r + ':' + s).join('|');
const bySig = new Map();
for (const c of index.values()) { const k = sig(c.files); if (k && !bySig.has(k)) bySig.set(k, c); }

// --- installed folder content
function scan(dir, base = dir, out = new Map()) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const f = path.join(dir, e.name);
    if (e.isDirectory()) scan(f, base, out);
    else out.set(norm(path.relative(base, f)), fs.statSync(f).size);
  }
  return out;
}
const IGNORE = new Set(['meta.ini']);

const stats = { identical: 0, differs: 0, notInPack: 0 };
const report = { identical: [], differs: [], notInPack: [] };
for (const a of manifest.assets.filter(a => a.packFrom)) {
  let cand = index.get(norm(a.packFrom));
  if (!cand) cand = bySig.get(sig(scan(path.join(mo2, 'mods', a.packFrom))));
  if (!cand) { stats.notInPack++; report.notInPack.push(a.packFrom); if (write) delete a.fromPack; continue; }
  const local = scan(path.join(mo2, 'mods', a.packFrom));
  const diffs = [];
  for (const [rel, size] of local) if (!IGNORE.has(rel) && cand.files.get(rel) !== size) diffs.push(rel);
  for (const rel of cand.files.keys()) if (!IGNORE.has(rel) && !local.has(rel)) diffs.push('(só no pacote) ' + rel);
  if (diffs.length === 0) {
    stats.identical++; report.identical.push(`${a.packFrom}  <-  ${cand.ref.archive}${cand.ref.inner ? ' > ' + cand.ref.inner : ''}`);
    if (write) a.fromPack = cand.ref;
  } else {
    stats.differs++; report.differs.push(`${a.packFrom}  (${diffs.length} arquivo(s) diferentes, ex.: ${diffs.slice(0, 2).join(' | ')})`);
    if (write) delete a.fromPack;
  }
}
if (write) {
  manifest.sourcePacks = cfg.sourcePacks;
  fs.writeFileSync(manifestPath, header + JSON.stringify(manifest, null, 2) + '\n');
}
console.log(JSON.stringify(stats));
if (process.argv.includes('--verbose'))
  for (const [k, v] of Object.entries(report)) { console.log(`\n## ${k} (${v.length})`); v.forEach(x => console.log(x)); }
