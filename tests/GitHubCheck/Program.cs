// Resolves every GitHub mod of a manifest to its latest version (live API) and prints old -> new.
using System.IO;
using System.Net.Http;
using Reima.Models;
using Reima.Services;

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("REIMA-Test/0.1");
var gh = new GitHubSource(http);
foreach (var a in Manifest.Load(args[0]).Assets.Where(a => GitHubSource.Is(a.Url)))
{
    try
    {
        var g = await gh.ResolveAsync(a.Url!, a.File, CancellationToken.None);
        var folders = GitHubSource.RewriteFolders(a.ArchiveFolders, g);
        Console.WriteLine($"{a.Name}\n   {g.Version}  file={g.File}  newer={(g.Url != a.Url)}  folders={string.Join(" | ", folders)}");
    }
    catch (Exception ex) { Console.WriteLine($"{a.Name}: ERRO {ex.Message}"); }
}
