using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reima.Services;

/// <summary>One row of GAMMA's modpack_maker_list.txt (url, install folders, author, title, page).</summary>
public sealed record GammaMod(string Url, string Instructions, string Author, string Title, string Page)
{
    /// <summary>Stable identity: GAMMA renumbers folders when mods are inserted, title+author does not change.</summary>
    public string Key => $"{Title} - {Author}".ToLowerInvariant();
    public string Display => $"{Title} - {Author}";
    public List<string> Folders => Instructions is "" or "0" ? [] : Instructions.Split(':').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}

public sealed record GammaChange(GammaMod Mod, GammaMod? Old, bool InReima);

public sealed record GammaReport(string Sha, DateTimeOffset Date, string Message,
    List<GammaChange> Added, List<GammaChange> Removed, List<GammaChange> Updated)
{
    public bool Any => Added.Count + Removed.Count + Updated.Count > 0;
    public int AffectsReima => Updated.Count(c => c.InReima) + Removed.Count(c => c.InReima);
}

/// <summary>
/// Watches the official GAMMA definition (github.com/Grokitach/Stalker_GAMMA) and diffs its mod list
/// against the last version this launcher saw.
/// </summary>
public sealed partial class GammaUpdates(HttpClient http, string stateFile)
{
    const string Repo = "Grokitach/Stalker_GAMMA";
    const string ListPath = "G.A.M.M.A/modpack_data/modpack_maker_list.txt";

    sealed record State(string Sha, string List);

    public static List<GammaMod> Parse(string text) =>
        text.Split('\n')
            .Select(l => l.TrimEnd('\r').Split('\t'))
            .Where(c => c.Length >= 5 && c[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Select(c => new GammaMod(c[0].Trim(), c[1].Trim(), c[2].Trim().TrimStart('-', ' '), c[3].Trim(), c[4].Trim()))
            .ToList();

    /// <summary>Returns null when nothing changed since the last accepted check.</summary>
    public async Task<GammaReport?> CheckAsync(IEnumerable<string> reimaModNames, string? localGammaRoot, CancellationToken ct)
    {
        var commit = JsonNode.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/commits/main", ct))!;
        var sha = (string)commit["sha"]!;
        var date = DateTimeOffset.Parse((string)commit["commit"]!["committer"]!["date"]!);
        var message = ((string?)commit["commit"]!["message"] ?? "").Split('\n')[0];

        var state = Load() ?? Baseline(localGammaRoot);
        if (state is not null && state.Sha == sha) return null;

        var list = await http.GetStringAsync($"https://raw.githubusercontent.com/{Repo}/{sha}/{ListPath}", ct);
        if (state is null) { Save(new(sha, list)); return null; } // first run: this becomes the baseline

        var oldMods = Parse(state.List).GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        var newMods = Parse(list).GroupBy(m => m.Key).ToDictionary(g => g.Key, g => g.First());
        var reima = reimaModNames.Select(n => NumberPrefix().Replace(n, "").ToLowerInvariant()).ToHashSet();

        var report = new GammaReport(sha, date, message,
            newMods.Values.Where(m => !oldMods.ContainsKey(m.Key)).Select(m => new GammaChange(m, null, reima.Contains(m.Key))).ToList(),
            oldMods.Values.Where(m => !newMods.ContainsKey(m.Key)).Select(m => new GammaChange(m, m, reima.Contains(m.Key))).ToList(),
            newMods.Values.Where(m => oldMods.TryGetValue(m.Key, out var o) && (o.Url != m.Url || o.Instructions != m.Instructions))
                .Select(m => new GammaChange(m, oldMods[m.Key], reima.Contains(m.Key))).ToList());
        _pending = new(sha, list);
        return report;
    }

    State? _pending;

    /// <summary>Marks the checked version as seen (call after the user applied or dismissed the report).</summary>
    public void Accept() { if (_pending is not null) Save(_pending); }

    /// <summary>
    /// Updates the REIMA recipe for GAMMA mods that changed upstream: new url and install folders.
    /// The archive name/md5 are unknown until downloaded, so the asset switches to a new file name
    /// (forcing a fresh download) and the assisted download accepts whatever archive ModDB gives.
    /// </summary>
    public static int ApplyToManifest(string manifestPath, GammaReport report)
    {
        var raw = File.ReadAllText(manifestPath);
        var header = LeadingComments().Match(raw).Value;
        var root = JsonNode.Parse(raw, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip })!;
        var byKey = report.Updated.Where(c => c.InReima).ToDictionary(c => c.Mod.Key);
        int n = 0;
        foreach (var a in root["assets"]!.AsArray())
        {
            var name = (string?)a!["name"];
            if (name is null) continue;
            if (!byKey.TryGetValue(NumberPrefix().Replace(name, "").ToLowerInvariant(), out var c)) continue;
            var id = (string)a["id"]!;
            var startId = Regex.Match(c.Mod.Url, @"(\d+)/?$").Groups[1].Value;
            a["url"] = c.Mod.Url;
            a["page"] = c.Mod.Page;
            a["file"] = $"{id}-{(startId.Length > 0 ? startId : DateTime.Now.ToString("yyyyMMdd"))}.pkg";
            a["anyFile"] = true;
            ((JsonObject)a).Remove("md5");
            ((JsonObject)a).Remove("sha256");
            if (c.Mod.Folders.Count > 0) a["archiveFolders"] = new JsonArray(c.Mod.Folders.Select(f => (JsonNode)f).ToArray());
            else ((JsonObject)a).Remove("archiveFolders");
            n++;
        }
        File.Copy(manifestPath, manifestPath + ".bak_gamma_update", true);
        File.WriteAllText(manifestPath, header + root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
        return n;
    }

    /// <summary>First run on a PC with GAMMA installed: start from the definition that install used.</summary>
    static State? Baseline(string? gammaRoot)
    {
        if (gammaRoot is null) return null;
        var git = Path.Combine(gammaRoot, ".Grok's Modpack Installer", "resources", "Stalker_GAMMA");
        var list = Path.Combine(git, "G.A.M.M.A", "modpack_data", "modpack_maker_list.txt");
        var head = Path.Combine(git, ".git", "HEAD");
        if (!File.Exists(list) || !File.Exists(head)) return null;
        var h = File.ReadAllText(head).Trim();
        if (h.StartsWith("ref: "))
        {
            var refFile = Path.Combine(git, ".git", h[5..].Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(refFile)) return null;
            h = File.ReadAllText(refFile).Trim();
        }
        return new(h, File.ReadAllText(list));
    }

    State? Load()
    {
        try { return File.Exists(stateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(stateFile)) : null; }
        catch { return null; }
    }

    void Save(State s) => File.WriteAllText(stateFile, JsonSerializer.Serialize(s));

    [GeneratedRegex(@"^\d+-\s*")]
    private static partial Regex NumberPrefix();

    [GeneratedRegex(@"^(\s*//.*\n)*")]
    private static partial Regex LeadingComments();
}
