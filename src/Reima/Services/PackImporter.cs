using System.IO;
using Reima.Models;

namespace Reima.Services;

/// <summary>
/// Pulls mods out of community packs the user already downloaded (ROMEO, GeorgePack) instead of the Drive copy.
/// All mods that live in the same archive come out in a single 7-Zip run into a staging folder; the installer
/// then takes each mod from there. Packs that are not found are simply skipped (the Drive pack is used).
/// </summary>
public sealed class PackImporter(Manifest manifest, InstallSettings s, Extractor extractor)
{
    public event Action<string>? Log;
    public string StageDir => Path.Combine(s.DownloadsDir, ".packs");

    /// <summary>Folders searched for pack files: launcher downloads, cache folders, user's Downloads, chosen pack folder.</summary>
    IEnumerable<string> SearchDirs() =>
        new[] { s.PackDir, s.DownloadsDir, AssistedDownload.UserDownloads }.Concat(s.CacheDirs)
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))!;

    public string? Locate(string archive) =>
        SearchDirs().SelectMany(d => new[] { Path.Combine(d!, archive) }
                .Concat(SafeFiles(d!).Where(f => Path.GetFileName(f).Equals(archive, StringComparison.OrdinalIgnoreCase))))
            .FirstOrDefault(File.Exists);

    static IEnumerable<string> SafeFiles(string dir)
    {
        // pack files may sit one level down (e.g. "GeorgePack 1-001\GeorgePack 1-001.1")
        try { return Directory.EnumerateFiles(dir).Concat(Directory.EnumerateDirectories(dir).SelectMany(Directory.EnumerateFiles)).ToList(); }
        catch { return []; }
    }

    /// <summary>Version stamp of a pack-imported mod: which pack file it came from.</summary>
    public static string VersionOf(PackRef r) => "pack:" + r.Archive + (r.Inner is null ? "" : "|" + r.Inner);

    /// <summary>Staged folder of a mod, or null when it was not imported.</summary>
    public string? Staged(Asset a) =>
        a.FromPack is { } r && Directory.Exists(StagedPath(r)) ? StagedPath(r) : null;

    string StagedPath(PackRef r) => Path.Combine(StageDir, Key(r), r.Path);
    static string Key(PackRef r) => Safe(r.Archive + (r.Inner is null ? "" : "_" + Path.GetFileNameWithoutExtension(r.Inner)));
    static string Safe(string n) => string.Concat(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Extracts every needed mod, grouped per archive. Returns how many mods were staged.</summary>
    public async Task<int> StageAsync(IEnumerable<Asset> needed, IProgress<StepProgress> progress, CancellationToken ct)
    {
        var groups = needed.Where(a => a.FromPack is not null)
            .GroupBy(a => (a.FromPack!.Archive, a.FromPack.Inner)).ToList();
        int staged = 0, g = 0;
        var missingPacks = new HashSet<string>();
        foreach (var grp in groups)
        {
            ct.ThrowIfCancellationRequested();
            double frac = (double)g++ / Math.Max(1, groups.Count);
            var archive = Locate(grp.Key.Archive);
            if (archive is null) { missingPacks.Add(grp.Key.Archive); continue; }
            var first = grp.First().FromPack!;
            var outDir = Path.Combine(StageDir, Key(first));
            var label = Path.GetFileName(archive) + (grp.Key.Inner is null ? "" : " → " + Path.GetFileName(grp.Key.Inner));

            var src = archive;
            if (grp.Key.Inner is not null)
            {
                Log?.Invoke($"Abrindo {label}");
                src = await extractor.ExtractFileAsync(archive, grp.Key.Inner, Path.Combine(StageDir, ".inner"),
                    new Progress<int>(p => progress.Report(new("Importando dos pacotes", $"{label}: separando {p}%", frac, p / 200.0))), ct);
            }
            Log?.Invoke($"Importando {grp.Count()} mod(s) de {label}");
            await extractor.ExtractPathsAsync(src, outDir, grp.Select(a => a.FromPack!.Path),
                new Progress<int>(p => progress.Report(new("Importando dos pacotes", $"{label}: {grp.Count()} mods  {p}%", frac, 0.5 + p / 200.0))), ct);
            if (src != archive) File.Delete(src); // nested archive copy: tens of GB, drop it right away
            staged += grp.Count(a => Staged(a) is not null);
        }
        foreach (var m in missingPacks)
        {
            var pack = manifest.SourcePacks.FirstOrDefault(p => p.Files.Contains(m, StringComparer.OrdinalIgnoreCase));
            Log?.Invoke($"Pacote não encontrado: {m}{(pack?.Hint is { } h ? " (" + h + ")" : "")} — esses mods vêm do Drive.");
        }
        return staged;
    }

    public void Cleanup()
    {
        try { if (Directory.Exists(StageDir)) Directory.Delete(StageDir, true); } catch { }
    }
}
