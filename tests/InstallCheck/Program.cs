// Real mini install: a few mods of each source (Drive pack, GAMMA archive on Drive, GitHub) into a scratch MO2, from scratch.
// usage: InstallCheck <recipe.json> <scratch dir>
using System.IO;
using Reima.Models;
using Reima.Services;

var full = Manifest.Load(args[0]);
var pick = new List<Asset>();
pick.Add(full.Assets.First(a => a.PackFrom is not null && a.Name.StartsWith("On The Fly")));                   // Drive pack, files at root
pick.Add(full.Assets.First(a => a.PackFrom is not null && a.FromPack is not null));                            // Drive pack (packs off)
pick.AddRange(full.Assets.Where(a => a.Md5 is not null && a.ArchiveFolders.Count > 0).Take(2));                // GAMMA archives (Drive copy)
pick.Add(full.Assets.First(a => a.Url?.Contains("github.com") == true));                                        // GitHub
var m = full with { Assets = pick, ConfigPatches = [], OptionGroups = [] };
var root = args[1];
if (Directory.Exists(root)) Directory.Delete(root, true);
var mo2 = Path.Combine(root, "MO2");
Directory.CreateDirectory(Path.Combine(mo2, "profiles", m.Profile));
var s = new InstallSettings { Mo2Root = mo2, AnomalyRoot = Path.Combine(root, "Anomaly"), DownloadsDir = Path.Combine(root, "dl"),
    Selected = new HashSet<string>(), UsePacks = false };
using var http = new System.Net.Http.HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("REIMA/0.1");
var ins = new Installer(m, s, new Downloader(http), new Extractor(Extractor.Find(null)!)) { GitHub = new GitHubSource(http) };
ins.Log += l => Console.WriteLine("  " + l);
await ins.RunAsync(new Progress<StepProgress>(_ => { }), CancellationToken.None);
int ok = 0;
foreach (var a in pick)
{
    var d = Path.Combine(mo2, "mods", a.ModFolder);
    var n = Directory.Exists(d) ? Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length : 0;
    var gd = Directory.Exists(Path.Combine(d, "gamedata")) || Directory.GetFiles(d, "*.ltx", SearchOption.TopDirectoryOnly).Length > 0 || n > 1;
    Console.WriteLine($"{(n > 1 ? "OK " : "FALHA")} {a.Name}: {n} arquivos");
    if (n > 1) ok++;
}
Console.WriteLine("modlist: " + File.ReadAllLines(Path.Combine(mo2, "profiles", m.Profile, "modlist.txt")).Length + " linhas");
return ok == pick.Count ? 0 : 1;
