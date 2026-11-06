// Packs the player's own setup and publishes it on the Drive, so a fresh install comes back exactly as it is now:
//   REIMA_Config_anomaly.7z  -> Anomaly root: appdata (user.ltx, presets, imgui, saves), MCM (gamedata/configs/axr_options.ltx), commandline.txt (-dbg)
//   REIMA_Config_overwrite.7z -> MO2\overwrite (settings mods write at runtime)
// and the SSS24 modded exes WITH their gamedata part:
//   SSS24_MT_GrassInstancing_exes.7z -> Anomaly root: bin\Anomaly*.exe, bin\sl.*.dll, gamedata\** (minus axr_options.ltx)
// usage: node tools/save-settings.js <Anomaly root> <MO2 root> <manifest.json> [--exes] [--no-upload]
const fs = require('fs'), path = require('path'), crypto = require('crypto');
const { execFileSync } = require('child_process');
const [anomaly, mo2, manifestPath] = process.argv.slice(2).filter(a => !a.startsWith('--'));
const doExes = process.argv.includes('--exes'), upload = !process.argv.includes('--no-upload');
const here = __dirname;
const sevenZip = [String.raw`C:\Program Files\7-Zip\7z.exe`, String.raw`C:\Program Files (x86)\7-Zip\7z.exe`].find(fs.existsSync);
const rclone = path.join(here, '..', 'src', 'Reima', 'tools', 'rclone.exe');
const out = path.join(require('os').tmpdir(), 'reima_settings');
fs.mkdirSync(out, { recursive: true });

const sha256 = f => { const h = crypto.createHash('sha256'), fd = fs.openSync(f, 'r'), b = Buffer.alloc(8 << 20);
  for (let n; (n = fs.readSync(fd, b, 0, b.length, null)) > 0;) h.update(b.subarray(0, n)); fs.closeSync(fd); return h.digest('hex').toUpperCase(); };

// 7z with an explicit file list (relative to cwd), so the archive keeps appdata\..., gamedata\... paths
function pack(name, cwd, rels) {
  const dest = path.join(out, name);
  if (fs.existsSync(dest)) fs.unlinkSync(dest);
  const list = path.join(out, name + '.txt');
  fs.writeFileSync(list, rels.join('\n'), 'utf8');
  execFileSync(sevenZip, ['a', '-t7z', '-mx5', '-bso0', '-bsp0', '-sccUTF-8', '-scsUTF-8', dest, '@' + list], { cwd, stdio: 'inherit' });
  return dest;
}
const walk = (root, rel = '') => fs.readdirSync(path.join(root, rel), { withFileTypes: true }).flatMap(e =>
  e.isDirectory() ? walk(root, path.join(rel, e.name)) : [path.join(rel, e.name)]);
const exists = (root, rel) => fs.existsSync(path.join(root, rel));

const results = [];
// --- personal settings + saves
{
  const skip = /^appdata[\\/](logs|shaders_cache)[\\/]|console_history\.txt$|\.bak|\.back$/i;
  const rels = [
    ...walk(anomaly, 'appdata').filter(r => !skip.test(r)),
    ...['gamedata\\configs\\axr_options.ltx', 'commandline.txt'].filter(r => exists(anomaly, r)),
  ];
  results.push({ id: 'config_anomaly', name: 'Minhas configurações e saves (appdata, MCM, -dbg)', kind: 'anomaly', target: '',
    file: 'REIMA_Config_anomaly.7z', path: pack('REIMA_Config_anomaly.7z', anomaly, rels), count: rels.length });
}
{
  const ow = path.join(mo2, 'overwrite');
  const rels = fs.existsSync(ow) ? walk(ow).filter(r => !/\.bak$/i.test(r)) : [];
  if (rels.length) results.push({ id: 'config_overwrite', name: 'Minhas configurações de mods (MO2 overwrite)', kind: 'mo2', target: 'overwrite',
    file: 'REIMA_Config_overwrite.7z', path: pack('REIMA_Config_overwrite.7z', ow, rels), count: rels.length });
}
// --- SSS24 modded exes, bin + gamedata
if (doExes) {
  const bin = fs.readdirSync(path.join(anomaly, 'bin')).filter(f => /^Anomaly.*\.exe$|^sl\..*\.dll$/i.test(f)).map(f => 'bin\\' + f);
  const gd = exists(anomaly, 'gamedata') ? walk(anomaly, 'gamedata').filter(r => !/axr_options\.ltx$/i.test(r)) : [];
  results.push({ id: 'modded_exes', kind: 'anomaly', target: '', file: 'SSS24_MT_GrassInstancing_exes.7z',
    path: pack('SSS24_MT_GrassInstancing_exes.7z', anomaly, [...bin, ...gd]), count: bin.length + gd.length });
}

// --- upload + recipe
const raw = fs.readFileSync(manifestPath, 'utf8');
const header = raw.match(/^(\s*\/\/.*\n)*/)[0];
const m = JSON.parse(raw.replace(/^\s*\/\/.*$/gm, ''));
for (const r of results) {
  const sha = sha256(r.path);
  let url = '';
  if (upload) {
    execFileSync(rclone, ['copyto', r.path, 'reima:REIMA/config/' + r.file], { stdio: 'inherit' });
    const id = JSON.parse(execFileSync(rclone, ['lsjson', 'reima:REIMA/config/' + r.file]).toString())[0].ID;
    url = `https://drive.usercontent.google.com/download?id=${id}&export=download&confirm=t`;
  }
  let a = m.assets.find(x => x.id === r.id);
  if (!a) {
    a = { id: r.id, name: r.name, requires: ['config'] };
    m.assets.push(a); // last: applied after every mod
  }
  Object.assign(a, { kind: r.kind, target: r.target, file: r.file, sha256: sha, ...(url ? { url } : {}) });
  delete a.manualHint;
  console.log(`${r.file}: ${r.count} arquivo(s), ${(fs.statSync(r.path).size / 2 ** 20).toFixed(1)} MB${url ? ', publicado' : ''}`);
}
if (!m.optionGroups.some(g => g.id === 'config'))
  m.optionGroups.push({ id: 'config', label: 'Configurações', exclusive: false, options: [
    { id: 'config', label: 'Minhas configurações e saves', default: true,
      description: 'Gráficos, teclas, MCM, presets, saves e -dbg do jeito que estavam quando foram salvos. Só é aplicado quando muda.' }] });
fs.writeFileSync(manifestPath, header + JSON.stringify(m, null, 2) + '\n');
if (upload) execFileSync(rclone, ['copyto', manifestPath, 'reima:REIMA/manifest.json'], { stdio: 'inherit' });
console.log(upload ? 'Receita publicada.' : 'Receita atualizada (sem upload).');
