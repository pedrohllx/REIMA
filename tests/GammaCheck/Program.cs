// Smoke test: fake an older GAMMA baseline (one mod changed, one removed) and diff against live GitHub.
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Reima.Models;
using Reima.Services;

var gammaList = args[0];       // a modpack_maker_list.txt to use as the "old" version
var manifestPath = args[1];    // copy of manifest.json (gets modified)
var lines = File.ReadAllLines(gammaList).ToList();
int iWct = lines.FindIndex(l => l.Contains("Weapon Cover Tilt"));
lines[iWct] = lines[iWct].Replace("addons/start/", "addons/start/9");    // pretend the url changed
int iRm = lines.FindIndex(l => l.StartsWith("http") && l.Contains("Grokitach"));
var removedNote = lines[iRm].Split('\t')[3];
lines.Insert(iRm, lines[iRm].Replace(removedNote, " Fake Old Mod"));        // pretend a mod existed before
var state = Path.GetTempFileName();
File.WriteAllText(state, JsonSerializer.Serialize(new { Sha = "old", List = string.Join("\n", lines) }));

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("REIMA-Test/0.1");
var g = new GammaUpdates(http, state);
var m = Manifest.Load(manifestPath);
var r = await g.CheckAsync(m.Assets.Select(a => a.Name), null, CancellationToken.None);
if (r is null) { Console.WriteLine("no report"); return 1; }
Console.WriteLine($"sha {r.Sha[..8]} {r.Date:yyyy-MM-dd} '{r.Message}'");
Console.WriteLine($"added {r.Added.Count}, updated {r.Updated.Count}, removed {r.Removed.Count}, affects REIMA {r.AffectsReima}");
foreach (var c in r.Updated) Console.WriteLine($"  updated: {c.Mod.Display} inReima={c.InReima}");
foreach (var c in r.Removed) Console.WriteLine($"  removed: {c.Mod.Display}");
foreach (var c in r.Added.Take(5)) Console.WriteLine($"  added: {c.Mod.Display}");
Console.WriteLine("applied: " + GammaUpdates.ApplyToManifest(manifestPath, r));
var after = Manifest.Load(manifestPath).Assets.First(a => a.Name.Contains("Weapon Cover Tilt"));
Console.WriteLine($"after: url={after.Url} file={after.File} anyFile={after.AnyFile} md5={after.Md5 ?? "-"} folders={after.ArchiveFolders.Count}");
return 0;
