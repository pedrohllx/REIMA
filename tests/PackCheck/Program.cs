// Pack import smoke test: stages real mods out of the ROMEO hotfix archive (small) through PackImporter
// and checks the files land where the installer expects them.
// usage: PackCheck <manifest.json> <scratch dir>
using System.IO;
using Reima.Models;
using Reima.Services;

var manifest = Manifest.Load(args[0]);
var scratch = args[1];
var s = new InstallSettings
{
    Mo2Root = Path.Combine(scratch, "mo2"),
    AnomalyRoot = Path.Combine(scratch, "anomaly"),
    DownloadsDir = Path.Combine(scratch, "dl"),
    Selected = new HashSet<string>(),
};
var importer = new PackImporter(manifest, s, new Extractor(Extractor.Find(null)!));
importer.Log += Console.WriteLine;

// a fake asset pointing at a folder of the small hotfix archive (always present in it)
var hf = "CLIKRΔD - ROMEO-2026.10.14-hf1.7z";
var test = new Asset
{
    Id = "t", Name = "t", File = "t.7z",
    FromPack = new PackRef { Pack = "romeo", Archive = hf, Path = @"CLIKRΔD - ROMEO-2026.10.14-hf1\GAMMA\mods\xx-CLIKRΔD ROMEO Menu - Victor" },
};
Console.WriteLine("located: " + importer.Locate(hf));
var n = await importer.StageAsync([test], new Progress<StepProgress>(_ => { }), CancellationToken.None);
var dir = importer.Staged(test);
Console.WriteLine($"staged {n}: {dir}");
Console.WriteLine("files: " + (dir is null ? 0 : Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length));

// recipe sanity: how many assets point at packs, and whether those pack files are on this PC
var withPack = manifest.Assets.Where(a => a.FromPack is not null).ToList();
Console.WriteLine($"recipe: {withPack.Count} mods from packs, {manifest.SourcePacks.Count} packs");
foreach (var g in withPack.GroupBy(a => a.FromPack!.Archive))
    Console.WriteLine($"  {g.Key}: {g.Count()} mods, found={importer.Locate(g.Key) is not null}");
importer.Cleanup();
return dir is null ? 1 : 0;
