// Adds the "Upscaler" choice (Original / OptiScaler + DLSS) and the OptiScaler bin asset to a recipe.
// usage: node tools/add-optiscaler.js <manifest.json> <driveFileId> <sha256>
const fs = require('fs');
const [p, id, sha] = process.argv.slice(2);
const raw = fs.readFileSync(p, 'utf8');
const header = raw.match(/^(\s*\/\/.*\n)*/)[0];
const m = JSON.parse(raw.replace(/^\s*\/\/.*$/gm, ''));

m.optionGroups = (m.optionGroups || []).filter(g => g.id !== 'upscaler');
m.optionGroups.push({
  id: 'upscaler', label: 'Upscaler', exclusive: true,
  options: [
    { id: 'original', label: 'Original (sem OptiScaler)', description: 'Jogo como vem, sem DLL extra na pasta bin. Mais estável.', default: true },
    { id: 'optiscaler', label: 'OptiScaler + DLSS (experimental)', description: 'OptiScaler (fork DLSSNR) com DLSS/XeSS/FSR no DX11. Menu no jogo: Insert. Exige modo borderless.' },
  ],
});

if (id) {
  m.assets = m.assets.filter(a => a.id !== 'optiscaler_bin');
  const at = m.assets.findIndex(a => a.id === 'modded_exes') + 1; // right after the engine exes
  m.assets.splice(at, 0, {
    id: 'optiscaler_bin',
    name: 'OptiScaler (DLSSNR fork) + DLSS/XeSS/FSR DLLs',
    kind: 'anomaly',
    target: 'bin',
    url: `https://drive.usercontent.google.com/download?id=${id}&export=download&confirm=t`,
    file: 'OptiScaler_DLSSNR_bin.7z',
    sha256: sha.toUpperCase(),
    requires: ['optiscaler'],
    removeWhenOff: true,
  });
}
fs.writeFileSync(p, header + JSON.stringify(m, null, 2) + '\n');
console.log('ok', m.optionGroups.length, 'grupo(s)');
