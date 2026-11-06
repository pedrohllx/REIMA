// Switch test: install with "optiscaler" selected, then switch to "original" -> OptiScaler files gone, original file restored.
// usage: OptionCheck <recipe.json> <scratch dir> <dir with OptiScaler_DLSSNR_bin.7z>
using System.IO;
using Reima.Models;
using Reima.Services;

var full = Manifest.Load(args[0]);
var m = full with { Assets = full.Assets.Where(a => a.Id == "optiscaler_bin").ToList(), ConfigPatches = [] };
var root = args[1];
if (Directory.Exists(root)) Directory.Delete(root, true);
var anomaly = Path.Combine(root, "Anomaly"); var mo2 = Path.Combine(root, "MO2");
Directory.CreateDirectory(Path.Combine(anomaly, "bin")); Directory.CreateDirectory(Path.Combine(mo2, "profiles", m.Profile));
var orig = Path.Combine(anomaly, "bin", "libxess.dll");
File.WriteAllText(orig, "ORIGINAL"); // a file the pack replaces: must come back

async Task Run(string option)
{
    var s = new InstallSettings { Mo2Root = mo2, AnomalyRoot = anomaly, DownloadsDir = Path.Combine(root, "dl"),
        Selected = new HashSet<string> { option }, CacheDirs = [args[2]], UsePacks = false };
    var ins = new Installer(m, s, new Downloader(new System.Net.Http.HttpClient()), new Extractor(Extractor.Find(null)!));
    ins.Log += l => Console.WriteLine("  " + l);
    await ins.RunAsync(new Progress<StepProgress>(_ => { }), CancellationToken.None);
}
Console.WriteLine("== optiscaler"); await Run("optiscaler");
bool on = File.Exists(Path.Combine(anomaly, "bin", "dxgi.dll")) && File.ReadAllText(orig) != "ORIGINAL";
Console.WriteLine($"dxgi.dll presente e libxess substituída: {on}");
Console.WriteLine("== original"); await Run("original");
bool off = !File.Exists(Path.Combine(anomaly, "bin", "dxgi.dll")) && !Directory.Exists(Path.Combine(anomaly, "bin", "OptiScaler"))
           && File.ReadAllText(orig) == "ORIGINAL";
Console.WriteLine($"OptiScaler removido e original restaurado: {off}");
Console.WriteLine("restou em bin: " + string.Join(", ", Directory.GetFileSystemEntries(Path.Combine(anomaly, "bin")).Select(Path.GetFileName)));
return on && off ? 0 : 1;
