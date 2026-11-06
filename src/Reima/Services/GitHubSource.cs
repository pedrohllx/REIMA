using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Reima.Services;

/// <summary>Where the newest build of a GitHub-hosted mod lives.</summary>
/// <param name="Version">"release:v1.2", "tag:1.0" or "commit:abc123" — stored as the installed version.</param>
/// <param name="OldVersion">The version string pinned in the recipe url (to rewrite folder names), if any.</param>
public sealed record GitHubLatest(string Url, string File, string Version, string? OldVersion, string? NewVersion);

/// <summary>
/// Resolves GAMMA's pinned GitHub links to the latest release / tag / branch commit, so GitHub mods are
/// always installed at their newest version. Results are cached per run (API limit: 60 calls/hour without login).
/// </summary>
public sealed partial class GitHubSource(HttpClient http)
{
    readonly Dictionary<string, GitHubLatest> _cache = [];

    public static bool Is(string? url) => url is not null && GitHubUrl().IsMatch(url);

    public async Task<GitHubLatest> ResolveAsync(string url, string fileHint, CancellationToken ct)
    {
        if (_cache.TryGetValue(url, out var hit)) return hit;
        var m = GitHubUrl().Match(url);
        if (!m.Success) throw new ArgumentException("Não é um link do GitHub: " + url);
        string owner = m.Groups["o"].Value, repo = m.Groups["r"].Value, rest = m.Groups["rest"].Value;
        GitHubLatest result;

        if (ReleaseAsset().Match(rest) is { Success: true } rel)
        {
            var oldTag = Uri.UnescapeDataString(rel.Groups["tag"].Value);
            var oldName = Uri.UnescapeDataString(rel.Groups["file"].Value);
            var latest = await Api($"repos/{owner}/{repo}/releases/latest", ct);
            var tag = (string)latest["tag_name"]!;
            var assets = latest["assets"]!.AsArray().Select(a => (name: (string)a!["name"]!, url: (string)a["browser_download_url"]!)).ToList();
            // same asset name with the version replaced; otherwise the only asset with the same extension
            var pattern = "^" + Regex.Escape(VersionLess(oldName, oldTag)).Replace("\\{v}", ".+") + "$";
            var pick = assets.FirstOrDefault(a => Regex.IsMatch(a.name, pattern, RegexOptions.IgnoreCase));
            if (pick == default)
            {
                var sameExt = assets.Where(a => a.name.EndsWith(System.IO.Path.GetExtension(oldName), StringComparison.OrdinalIgnoreCase)).ToList();
                if (sameExt.Count == 1) pick = sameExt[0];
            }
            result = pick == default
                ? new(url, fileHint, $"release:{oldTag}", null, null) // can't map the asset: keep the pinned one
                : new(pick.url, pick.name, $"release:{tag}", Bare(oldTag), Bare(tag));
        }
        else if (TagArchive().Match(rest) is { Success: true } t)
        {
            var oldTag = t.Groups["tag"].Value;
            var tags = (await Api($"repos/{owner}/{repo}/tags?per_page=1", ct)).AsArray();
            var tag = tags.Count > 0 ? (string)tags[0]!["name"]! : oldTag;
            result = new($"https://github.com/{owner}/{repo}/archive/refs/tags/{tag}.zip", $"{repo}-{tag}.zip",
                $"tag:{tag}", Bare(oldTag), Bare(tag));
        }
        else if (HeadArchive().Match(rest) is { Success: true } h)
        {
            var branch = h.Groups["branch"].Value;
            var commit = await Api($"repos/{owner}/{repo}/commits/{branch}", ct);
            var sha = ((string)commit["sha"]!)[..12];
            // branch archives always serve the newest commit; the file name carries the commit so a new one re-downloads
            result = new(url, $"{repo}-{branch}-{sha}.zip", $"commit:{sha}", null, null);
        }
        else result = new(url, fileHint, "pinned:" + url, null, null); // e.g. archive/<sha>.zip: fixed on purpose

        _cache[url] = result;
        return result;
    }

    /// <summary>Install folders named after the old version (e.g. "repo-1.0.2.1") follow the new version.</summary>
    public static List<string> RewriteFolders(IEnumerable<string> folders, GitHubLatest g) =>
        g.OldVersion is null || g.NewVersion is null || g.OldVersion == g.NewVersion
            ? folders.ToList()
            : folders.Select(f => f.Replace(g.OldVersion, g.NewVersion)).ToList();

    async Task<JsonNode> Api(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
            throw new InvalidOperationException("Limite da API do GitHub atingido (60 consultas/hora). Tente de novo em alguns minutos.");
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
    }

    static string Bare(string tag) => tag.TrimStart('v', 'V');

    /// <summary>"Sights.Optics-2.9.1.zip" with tag "2.9.1" → "Sights.Optics-{v}.zip".</summary>
    static string VersionLess(string name, string tag)
    {
        var b = Bare(tag);
        if (name.Contains(tag)) return name.Replace(tag, "{v}");
        if (b.Length > 0 && name.Contains(b)) return name.Replace(b, "{v}");
        return name;
    }

    [GeneratedRegex(@"^https://github\.com/(?<o>[^/]+)/(?<r>[^/]+)/(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrl();
    [GeneratedRegex(@"^releases/download/(?<tag>[^/]+)/(?<file>[^/]+)$")]
    private static partial Regex ReleaseAsset();
    [GeneratedRegex(@"^archive/refs/tags/(?<tag>.+)\.(zip|tar\.gz)$")]
    private static partial Regex TagArchive();
    [GeneratedRegex(@"^archive/refs/heads/(?<branch>.+)\.zip$")]
    private static partial Regex HeadArchive();
}
