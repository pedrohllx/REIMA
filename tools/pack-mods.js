// Packs every manual ("packFrom") mod of the manifest into .7z files ready to upload (Drive etc.)
// and writes their sha256 back into the manifest.
// usage: node tools/pack-mods.js <MO2 root> <manifest.json> <out dir> [path to 7z.exe]
// Re-running skips archives that already exist (delete one to repack it).
const fs = require('fs'), path = require('path'), crypto = require('crypto');
const { execFileSync } = require('child_process');
const [mo2, manifestPath, outDir, sevenZipArg] = process.argv.slice(2);
if (!outDir) { console.error('usage: node pack-mods.js <MO2 root> <manifest.json> <out dir> [7z.exe]'); process.exit(1); }

const sevenZip = sevenZipArg ||
  [String.raw`C:\Program Files\7-Zip\7z.exe`, path.join(mo2, ".Grok's Modpack Installer", 'resources', '7zip', '7z.exe')]
    .find(p => fs.existsSync(p));
if (!sevenZip) { console.error('7z.exe não encontrado'); process.exit(1); }

const raw = fs.readFileSync(manifestPath, 'utf8');
const header = raw.match(/^(\s*\/\/.*\n)*/)[0];
const manifest = JSON.parse(raw.replace(/^\s*\/\/.*$/gm, ''));
fs.mkdirSync(outDir, { recursive: true });

const sha256 = f => { // chunked: packs can exceed node's 2 GB readFileSync limit
  const h = crypto.createHash('sha256'), fd = fs.openSync(f, 'r'), buf = Buffer.alloc(16 << 20);
  for (let n; (n = fs.readSync(fd, buf, 0, buf.length, null)) > 0;) h.update(buf.subarray(0, n));
  fs.closeSync(fd);
  return h.digest('hex').toUpperCase();
};
const todo = manifest.assets.filter(a => a.packFrom);
let n = 0, totalBytes = 0;
for (const a of todo) {
  n++;
  const src = path.join(mo2, 'mods', a.packFrom);
  const dest = path.join(outDir, a.file);
  if (!fs.existsSync(src)) { console.warn(`[${n}/${todo.length}] pasta não existe, pulei: ${a.packFrom}`); continue; }
  if (!fs.existsSync(dest)) {
    process.stdout.write(`[${n}/${todo.length}] ${a.packFrom} ... `);
    // -mx5: good ratio without taking forever; contents at the archive root (no wrapper folder)
    execFileSync(sevenZip, ['a', '-t7z', '-mx5', '-mmt=on', '-bso0', '-bsp0', dest, path.join(src, '*')], { stdio: 'inherit' });
    console.log('ok');
  }
  a.sha256 = sha256(dest);
  totalBytes += fs.statSync(dest).size;
  fs.writeFileSync(manifestPath, header + JSON.stringify(manifest, null, 2) + '\n'); // save progress
}
console.log(`Pronto: ${todo.length} pacotes, ${(totalBytes / 2 ** 30).toFixed(2)} GB em ${outDir}`);
console.log('Depois de subir no Drive, preencha "url" de cada um com o link de compartilhamento.');
