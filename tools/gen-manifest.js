// Generates manifest.json from an MO2 profile + GAMMA's mods.txt (sources, md5, install folders).
// usage: node tools/gen-manifest.js <MO2 root> <profile name> <out manifest.json> [base manifest for header/options]
// Mods found in GAMMA's mods.txt get their ModDB/GitHub url + md5; everything else becomes a manual
// ("drive") asset to upload to your own storage.
const fs = require('fs'), path = require('path');
const args = process.argv.slice(2);
const keepDisabled = args.includes("--keep-disabled");
const [mo2, profile, out, baseFile] = args.filter(a => !a.startsWith("--"));
if (!out) { console.error('usage: node gen-manifest.js <MO2 root> <profile> <out.json> [base.json]'); process.exit(1); }

const stripComments = s => s.replace(/^\s*\/\/.*$/gm, '');
const base = baseFile ? JSON.parse(stripComments(fs.readFileSync(baseFile, 'utf8'))) : {};

// --- GAMMA sources: line N (1-based, separators included) -> folder "N- <title><author>"
const gammaTxt = path.join(mo2, ".Grok's Modpack Installer", 'mods.txt');
const sources = new Map();
fs.readFileSync(gammaTxt, 'utf8').split(/\r?\n/).forEach((line, i) => {
  const c = line.split('\t');
  if (c.length < 7) return;
  const [url, instr, author, title, page, file, md5] = c.map(x => x.trim());
  const folder = `${i + 1}- ${title} - ${author.replace(/^-\s*/, '')}`;
  sources.set(folder.toLowerCase(), {
    url, page, file, md5,
    folders: instr && instr !== '0' ? instr.split(':').map(x => x.trim()).filter(Boolean) : [],
  });
});

// --- profile, top = highest priority. A separator applies to the mods ABOVE it.
const lines = fs.readFileSync(path.join(mo2, 'profiles', profile, 'modlist.txt'), 'utf8')
  .split(/\r?\n/).filter(l => /^[+-]/.test(l));
const assets = [];
let pending = [];
const slug = s => s.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '').slice(0, 60);
const used = new Set();
const uniq = id => { let u = id, n = 2; while (used.has(u)) u = `${id}_${n++}`; used.add(u); return u; };
const stats = { gamma: 0, manual: 0, disabled: 0, separators: 0, missingFolder: 0 };

for (const l of lines) {
  const enabled = l[0] === '+', name = l.slice(1);
  if (name.endsWith('_separator')) {
    const sep = name.replace(/_separator$/, '');
    pending.forEach(a => (a.separator = sep));
    pending = [];
    stats.separators++;
    continue;
  }
  if (!enabled && !keepDisabled) { stats.disabled++; continue; }
  if (!fs.existsSync(path.join(mo2, 'mods', name))) { stats.missingFolder++; continue; }
  const src = sources.get(name.toLowerCase());
  const a = { id: uniq(slug(name) || 'mod'), name };
  if (src) {
    a.url = src.url;
    a.file = src.file;
    a.md5 = src.md5;
    if (src.folders.length) a.archiveFolders = src.folders;
    a.page = src.page;
    const mid = src.url.match(/moddb\.com\/addons\/start\/(\d+)/);
    if (mid) { a.source = src.url; a.sourceVersion = mid[1]; }
    stats.gamma++;
  } else {
    a.url = '';
    a.file = slug(name) + '.7z';
    a.manualHint = 'Pacote REIMA (seu Drive) - gerado por tools/pack-mods.js';
    a.packFrom = name;
    stats.manual++;
  }
  if (!enabled) a.enabled = false;
  assets.push(a);
  pending.push(a);
}

const manifest = {
  name: base.name ?? 'Realistic and Immersive Modpack for Anomaly',
  version: base.version ?? '0.1.0',
  profile: base.profile ?? 'REIMA',
  manifestUrl: base.manifestUrl ?? '',
  base: base.base,
  optionGroups: base.optionGroups ?? [],
  // engine/bin assets from the base manifest stay in front of the generated mod list
  assets: [...(base.assets ?? []).filter(a => a.kind && a.kind !== 'mod'), ...assets],
  configPatches: base.configPatches ?? [],
};
fs.writeFileSync(out, '// Gerado por tools/gen-manifest.js a partir do perfil "' + profile + '" em ' +
  new Date().toISOString().slice(0, 10) + '\n' + JSON.stringify(manifest, null, 2) + '\n');
console.log(JSON.stringify(stats), 'assets:', manifest.assets.length);
