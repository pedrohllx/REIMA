using System.Diagnostics;
using System.IO;
using Reima.Models;

namespace Reima.Services;

/// <summary>
/// For sites that block automated downloads (ModDB/Cloudflare): opens the download page in the user's own
/// browser and waits for the file to land in their Downloads folder, then moves it into the launcher cache.
/// The user clicks; nothing here bypasses the site's protection.
/// </summary>
public sealed class AssistedDownload(string cacheDir)
{
    public static string UserDownloads =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    static readonly HashSet<string> ArchiveExt = [".7z", ".zip", ".rar"];

    public static bool Needs(string? url) =>
        url is not null && url.Contains("moddb.com", StringComparison.OrdinalIgnoreCase);

    public static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public async Task<string> WaitAsync(Asset a, IProgress<StepProgress> progress, double overall, CancellationToken ct)
    {
        OpenInBrowser(a.Url!);
        var name = Path.GetFileNameWithoutExtension(a.File);
        var ext = Path.GetExtension(a.File);
        var started = DateTime.Now;
        long lastSize = -1;
        int stableTicks = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var waited = DateTime.Now - started;
            progress.Report(new($"Baixe no navegador: {a.Name}",
                $"Arquivo esperado: {a.File}  ·  aguardando há {(int)waited.TotalMinutes:0}:{waited.Seconds:00}  ·  pastas: {UserDownloads} ou {cacheDir}",
                overall, null));

            // AnyFile: name unknown (GAMMA update) -> newest archive created after the page was opened
            // the user's Downloads, or the launcher's own downloads folder if the browser was set to save there
            string? hit = null;
            foreach (var dir in new[] { UserDownloads, cacheDir }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                hit = a.AnyFile
                    // AnyFile: name unknown (GAMMA update) -> newest archive created after the page was opened
                    ? Directory.EnumerateFiles(dir)
                        .Where(f => ArchiveExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && File.GetCreationTime(f) >= started.AddSeconds(-5))
                        .OrderByDescending(File.GetCreationTime)
                        .FirstOrDefault()
                    // exact name, or the browser's duplicate naming "file (1).zip"
                    : Directory.EnumerateFiles(dir, name + "*" + ext)
                        .Where(f =>
                        {
                            var n = Path.GetFileNameWithoutExtension(f);
                            return n.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                   (n.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase) && n.EndsWith(')'));
                        })
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();
                if (hit is not null) break;
            }

            // complete = no partial file next to it and size unchanged for 2 polls
            if (hit is not null && !File.Exists(hit + ".crdownload") && !File.Exists(hit + ".part") && !File.Exists(hit + ".download"))
            {
                var size = new FileInfo(hit).Length;
                stableTicks = size == lastSize && size > 0 ? stableTicks + 1 : 0;
                lastSize = size;
                if (stableTicks >= 2)
                {
                    Directory.CreateDirectory(cacheDir);
                    // AnyFile with a bare name: keep the real extension of what the browser saved
                    var name2 = a.AnyFile && Path.GetExtension(a.File) is "" ? a.File + Path.GetExtension(hit) : a.File;
                    var dest = Path.Combine(cacheDir, name2);
                    if (!string.Equals(Path.GetFullPath(hit), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                        File.Move(hit, dest, true);
                    return dest;
                }
            }
            await Task.Delay(1000, ct);
        }
    }
}
