using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reima.Models;

namespace Reima.Services;

/// <summary>A mod whose build in the recipe is older than the one GAMMA's official list points to.</summary>
public sealed record OutdatedMod(Asset Asset, GammaMod Latest, string LatestVersion)
{
    public string Name => Asset.Name;
    public string Versions => $"no REIMA: ModDB #{Asset.SourceVersion}  →  atual no GAMMA: ModDB #{LatestVersion}";
}

/// <summary>
/// Knows the latest ModDB build of every GAMMA mod from the official list on GitHub — no ModDB access needed:
/// each new upload on ModDB gets a new download id, and GAMMA's list records it (addons/start/&lt;id&gt;).
/// </summary>
public sealed partial class ModDbVersions(HttpClient http)
{
    const string ListUrl = "https://raw.githubusercontent.com/Grokitach/Stalker_GAMMA/main/G.A.M.M.A/modpack_data/modpack_maker_list.txt";

    public static string? IdOf(string? url) => url is null ? null : StartId().Match(url) is { Success: true } m ? m.Groups[1].Value : null;

    public async Task<List<OutdatedMod>> FindOutdatedAsync(Manifest manifest, CancellationToken ct)
    {
        var latest = GammaUpdates.Parse(await http.GetStringAsync(ListUrl, ct))
            .GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        var result = new List<OutdatedMod>();
        foreach (var a in manifest.Assets.Where(a => a.SourceVersion is not null))
        {
            var key = NumberPrefix().Replace(a.Name, "").ToLowerInvariant();
            if (!latest.TryGetValue(key, out var m)) continue; // removed from GAMMA: reported by the GAMMA banner
            var id = IdOf(m.Url);
            if (id is not null && id != a.SourceVersion) result.Add(new(a, m, id));
        }
        return result;
    }

    [GeneratedRegex(@"moddb\.com/addons/start/(\d+)")]
    private static partial Regex StartId();
    [GeneratedRegex(@"^\d+-\s*")]
    private static partial Regex NumberPrefix();
}

/// <summary>Edits manifest.json in place (keeps the header comment and unknown fields).</summary>
public sealed partial class RecipeEditor(string path)
{
    public void Update(string assetId, Action<JsonObject> change)
    {
        var raw = File.ReadAllText(path);
        var header = Header().Match(raw).Value;
        var root = JsonNode.Parse(raw, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip })!;
        var asset = root["assets"]!.AsArray().OfType<JsonObject>().FirstOrDefault(a => (string?)a["id"] == assetId)
                    ?? throw new KeyNotFoundException("Asset não encontrado na receita: " + assetId);
        change(asset);
        File.Copy(path, path + ".bak", true);
        File.WriteAllText(path, header + root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
    }

    [GeneratedRegex(@"^(\s*//.*\n)*")]
    private static partial Regex Header();
}

/// <summary>
/// "Atualizar este mod": user downloads the new build in their browser, REIMA hashes it, updates the recipe,
/// and leaves the file in the "Subir pro Drive" folder (url stays empty until the Drive link is pasted).
/// </summary>
public sealed class ModUpdater(string manifestPath, string cacheDir, string uploadDir)
{
    public async Task<string> UpdateAsync(OutdatedMod o, IProgress<StepProgress> progress, CancellationToken ct)
    {
        var a = o.Asset;
        var temp = a with { Url = o.Latest.Url, File = $"{a.Id}-{o.LatestVersion}", AnyFile = true };
        var got = await new AssistedDownload(cacheDir).WaitAsync(temp, progress, 0, ct);

        progress.Report(new("Calculando hash", Path.GetFileName(got), 0.9, null));
        var sha = await Downloader.Sha256Async(got, null, ct);
        var md5 = await Downloader.Md5Async(got, ct);
        Directory.CreateDirectory(uploadDir);
        File.Copy(got, Path.Combine(uploadDir, Path.GetFileName(got)), true);

        new RecipeEditor(manifestPath).Update(a.Id, j =>
        {
            j["url"] = "";                         // pending: paste the Drive link
            j["manualHint"] = "Aguardando link do Drive (arquivo em \"Subir pro Drive\")";
            j["file"] = Path.GetFileName(got);
            j["sha256"] = sha;
            j["md5"] = md5;
            j["source"] = o.Latest.Url;
            j["sourceVersion"] = o.LatestVersion;
            j["page"] = o.Latest.Page;
            j.Remove("anyFile");
            if (o.Latest.Folders.Count > 0) j["archiveFolders"] = new JsonArray(o.Latest.Folders.Select(f => (JsonNode)f).ToArray());
            else j.Remove("archiveFolders");
        });
        progress.Report(new("Mod atualizado na receita", Path.GetFileName(got), 1, 1));
        return got;
    }

    /// <summary>Saves the Drive share link of an uploaded pack into the recipe.</summary>
    public void SetLink(string assetId, string link) =>
        new RecipeEditor(manifestPath).Update(assetId, j => { j["url"] = link.Trim(); j.Remove("manualHint"); });
}
