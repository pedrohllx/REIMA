// 1) real recipe vs live GAMMA list  2) copy with one mod set to an old ModDB id -> must be reported
//    3) recipe editor: save a Drive link and reload.
using System.IO;
using System.Net.Http;
using Reima.Models;
using Reima.Services;

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("REIMA-Test/0.1");
var v = new ModDbVersions(http);
var real = Manifest.Load(args[0]);
var r1 = await v.FindOutdatedAsync(real, CancellationToken.None);
Console.WriteLine($"real recipe: {real.Assets.Count(a => a.SourceVersion != null)} ModDB mods checked, {r1.Count} outdated");
foreach (var o in r1.Take(5)) Console.WriteLine("  " + o.Name + " | " + o.Versions);

var copy = Path.Combine(Path.GetTempPath(), "reima_outdated_test.json");
File.Copy(args[0], copy, true);
var wct = real.Assets.First(a => a.Name.Contains("Weapon Cover Tilt"));
new RecipeEditor(copy).Update(wct.Id, j => j["sourceVersion"] = "100");
var r2 = await v.FindOutdatedAsync(Manifest.Load(copy), CancellationToken.None);
Console.WriteLine($"simulated: {r2.Count} outdated -> {string.Join(", ", r2.Select(o => o.Name + " [" + o.Versions + "]"))}");

new ModUpdater(copy, Path.GetTempPath(), Path.GetTempPath()).SetLink(wct.Id, "https://drive.google.com/file/d/TESTID1234567/view");
var after = Manifest.Load(copy).Assets.First(a => a.Id == wct.Id);
Console.WriteLine($"link saved: {after.Url}  normalized: {Downloader.Normalize(after.Url!)}");
return 0;
