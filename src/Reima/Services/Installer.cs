using System.IO;
using System.Text;
using System.Text.Json;
using Reima.Models;

namespace Reima.Services;

public sealed record InstallSettings
{
    public required string Mo2Root { get; init; }
    public required string AnomalyRoot { get; init; }
    public required string DownloadsDir { get; init; }
    /// <summary>Existing MO2 profile to copy when the REIMA profile does not exist yet (e.g. "CLIKRΔD - ROMEO").</summary>
    public string? BaseProfile { get; init; }
    public required IReadOnlySet<string> Selected { get; init; }
    /// <summary>Extra folders searched for archives by file name before downloading (e.g. GAMMA\downloads).</summary>
    public List<string> CacheDirs { get; init; } = [];
    /// <summary>Get GAMMA's mods from their ModDB page (user clicks in the browser, queued) instead of the Drive copy.</summary>
    public bool PreferModDb { get; init; }
    /// <summary>Folder where the user keeps the ROMEO / GeorgePack files (also searched: Downloads and cache folders).</summary>
    public string? PackDir { get; init; }
    /// <summary>Import mods from those packs when found (otherwise Drive).</summary>
    public bool UsePacks { get; init; } = true;
}

public sealed record StepProgress(string Title, string Detail, double Overall, double? Current);

public sealed class MissingFilesException(IReadOnlyList<Asset> missing)
    : Exception($"{missing.Count} arquivo(s) precisam ser colocados manualmente na pasta downloads.")
{
    public IReadOnlyList<Asset> Missing { get; } = missing;
}

/// <summary>download → verify → extract → place → profile → config. Skips assets whose hash is already installed.</summary>
public sealed class Installer(Manifest manifest, InstallSettings s, Downloader downloader, Extractor extractor)
{
    public event Action<string>? Log;
    const string StateFile = "reima_state.json";
    string StatePath => Path.Combine(s.Mo2Root, StateFile);

    public List<Asset> ActiveAssets() => manifest.Assets.Where(a => a.AppliesTo(s.Selected)).ToList();

    public GitHubSource? GitHub { get; init; }

    PackImporter? _packs;
    PackImporter Packs => _packs ??= CreatePacks();
    PackImporter CreatePacks() { var p = new PackImporter(manifest, s, extractor); p.Log += m => Log?.Invoke(m); return p; }
    /// <summary>The mod comes out of a pack the user has on disk.</summary>
    bool FromLocalPack(Asset a) => s.UsePacks && a.FromPack is { } r && Packs.Locate(r.Archive) is not null;

    public IReadOnlyList<Asset> FindMissingManual() =>
        ActiveAssets().Where(a => a.IsManual && Locate(a.File) is null).ToList();

    /// <summary>The archive on disk: launcher downloads folder first, then cache folders.</summary>
    string? Locate(string file) =>
        new[] { s.DownloadsDir }.Concat(s.CacheDirs)
            .Select(d => Path.Combine(d, file))
            .FirstOrDefault(File.Exists);

    /// <summary>What the newest version of an asset is and where to get it.</summary>
    public sealed record Plan(Asset Asset, string? Url, string File, List<string> Folders, string? Expected, bool PinnedChecksums);

    public enum Status { NotInstalled, UpToDate, Outdated }
    public sealed record AssetStatus(Asset Asset, Status Status, string? Installed, string? Latest);

    async Task<Plan> PlanAsync(Asset a, CancellationToken ct)
    {
        if (GitHub is not null && GitHubSource.Is(a.Url))
        {
            var g = await GitHub.ResolveAsync(a.Url!, a.File, ct);
            bool same = g.Url == a.Url;
            bool branch = g.Version.StartsWith("commit:"); // branch archives change with every commit: GAMMA's md5 can't apply
            return new(a, g.Url, same && !branch ? a.File : g.File, GitHubSource.RewriteFolders(a.ArchiveFolders, g), g.Version, same && !branch);
        }
        if (FromLocalPack(a))
            return new(a, null, a.File, [], PackImporter.VersionOf(a.FromPack!), false);
        // Drive/manual packs: the recipe's sha256 IS the version. ModDB: GAMMA's md5 is the version.
        var expected = !string.IsNullOrWhiteSpace(a.Sha256) ? "sha256:" + a.Sha256.ToUpperInvariant()
                     : !string.IsNullOrWhiteSpace(a.Md5) ? "md5:" + a.Md5.ToUpperInvariant()
                     : null;
        // ModDB preferred: the original file (validated by GAMMA's md5) instead of the Drive copy
        var url = s.PreferModDb && AssistedDownload.Needs(a.Source) && !string.IsNullOrWhiteSpace(a.Md5) ? a.Source : a.Url;
        return new(a, url, a.File, a.ArchiveFolders, expected, true);
    }

    /// <summary>
    /// Download queue: before installing, every file that has to come from ModDB is fetched one after another —
    /// the next page opens in the browser as soon as the previous file lands. The install then runs unattended.
    /// </summary>
    async Task QueueAssistedAsync(IProgress<StepProgress> progress, Dictionary<string, string> state, CancellationToken ct)
    {
        var queue = new List<(Asset Asset, Plan Plan)>();
        foreach (var a in ActiveAssets())
        {
            var p = await PlanAsync(a, ct);
            if (FromLocalPack(a) || !AssistedDownload.Needs(p.Url) || Locate(p.File) is not null) continue;
            if (p.Expected is not null && state.TryGetValue(a.Id, out var v) && v == p.Expected && TargetExists(a)) continue;
            queue.Add((a, p));
        }
        if (queue.Count == 0) return;
        Log?.Invoke($"Fila do ModDB: {queue.Count} arquivo(s). Clique em baixar em cada página que abrir no navegador.");
        for (int i = 0; i < queue.Count; i++)
        {
            var (a, p) = queue[i];
            var shown = a with { Name = $"[{i + 1}/{queue.Count}] {a.Name}", Url = p.Url };
            await new AssistedDownload(s.DownloadsDir).WaitAsync(shown, progress, (double)i / queue.Count, ct);
            Log?.Invoke($"Recebido ({i + 1}/{queue.Count}): {a.File}");
        }
    }

    /// <summary>Version check like GAMMA's launcher: compares what is installed with the newest version, without downloading.</summary>
    public async Task<List<AssetStatus>> CheckAsync(CancellationToken ct)
    {
        var state = LoadState();
        var list = new List<AssetStatus>();
        foreach (var a in ActiveAssets())
        {
            Plan p;
            try { p = await PlanAsync(a, ct); }
            catch (Exception ex) { Log?.Invoke($"Não consegui checar {a.Name}: {ex.Message}"); p = new(a, a.Url, a.File, a.ArchiveFolders, null, true); }
            state.TryGetValue(a.Id, out var inst);
            var st = inst is null || !TargetExists(a) ? Status.NotInstalled
                   : p.Expected is null || inst == p.Expected ? Status.UpToDate
                   : Status.Outdated;
            list.Add(new(a, st, inst, p.Expected));
        }
        return list;
    }

    public async Task RunAsync(IProgress<StepProgress> progress, CancellationToken ct)
    {
        var missing = FindMissingManual();
        if (missing.Count > 0) throw new MissingFilesException(missing);

        var state = LoadState();
        RemoveDeselected(state);
        await QueueAssistedAsync(progress, state, ct);
        var assets = ActiveAssets();

        // ROMEO / GeorgePack: every mod that has to be (re)installed comes out of its pack in one pass per archive
        var fromPacks = new List<Asset>();
        foreach (var a in assets.Where(FromLocalPack))
        {
            var v = PackImporter.VersionOf(a.FromPack!);
            if (!(state.TryGetValue(a.Id, out var iv) && iv == v && TargetExists(a))) fromPacks.Add(a);
        }
        if (fromPacks.Count > 0)
        {
            Packs.Cleanup();
            Log?.Invoke($"{await Packs.StageAsync(fromPacks, progress, ct)} de {fromPacks.Count} mod(s) importados dos pacotes.");
        }
        var backupDir = Path.Combine(s.AnomalyRoot, "_reima_backup", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        int i = 0, updated = 0, skipped = 0;

        foreach (var a in assets)
        {
            ct.ThrowIfCancellationRequested();
            double baseFrac = (double)i++ / assets.Count, span = 1.0 / assets.Count;
            void Report(string detail, double? cur) =>
                progress.Report(new(a.Name, detail, baseFrac + span * (cur ?? 0), cur));

            Report("Checando versão", null);
            var plan = await PlanAsync(a, ct);
            state.TryGetValue(a.Id, out var installedVersion);
            if (plan.Expected is not null && installedVersion == plan.Expected && TargetExists(a))
            {
                skipped++;
                continue; // up to date: nothing to download
            }
            if (installedVersion is not null) Log?.Invoke($"Atualização: {a.Name} ({installedVersion} → {plan.Expected ?? "nova"})");

            if (Packs.Staged(a) is { } stagedDir && a.Kind == AssetKind.Mod)
            {
                Report("Instalando (do pacote)", 0.9);
                var pdest = Path.Combine(s.Mo2Root, "mods", a.ModFolder);
                if (Directory.Exists(pdest)) Directory.Delete(pdest, true);
                MoveDir(stagedDir, pdest);
                if (!File.Exists(Path.Combine(pdest, "meta.ini")))
                    File.WriteAllText(Path.Combine(pdest, "meta.ini"),
                        $"[General]\r\nversion={manifest.Version}\r\ninstalled=true\r\nnotes=Installed by REIMA ({a.Id}) from {a.FromPack!.Archive}\r\n");
                state[a.Id] = PackImporter.VersionOf(a.FromPack!);
                SaveState(state);
                updated++;
                Log?.Invoke($"Instalado do pacote: {a.Name}");
                continue;
            }
            if (FromLocalPack(a))
            {
                // pack on disk but the folder was not in it: fall back to the Drive copy
                plan = plan with { Url = a.Url, Expected = !string.IsNullOrWhiteSpace(a.Sha256) ? "sha256:" + a.Sha256.ToUpperInvariant() : null, PinnedChecksums = true };
                Log?.Invoke($"{a.Name} não estava no pacote: baixando do Drive.");
            }

            var file = Locate(plan.File) ?? Path.Combine(s.DownloadsDir, plan.File);
            if (!File.Exists(file) && AssistedDownload.Needs(plan.Url))
            {
                Log?.Invoke($"ModDB: abrindo {a.Name} no navegador, aguardando o download...");
                file = await new AssistedDownload(s.DownloadsDir).WaitAsync(a with { Url = plan.Url }, progress, baseFrac, ct);
                Log?.Invoke($"Recebido: {a.File}");
            }
            else if (!File.Exists(file))
            {
                Log?.Invoke($"Baixando {a.Name}{(plan.Url != a.Url ? " (versão mais nova do GitHub)" : "")}");
                await downloader.DownloadAsync(plan.Url!, file, new Progress<TransferProgress>(p =>
                    Report($"Baixando {Fmt(p.Done)}{(p.Total is long t ? " / " + Fmt(t) : "")}  {Fmt((long)p.BytesPerSecond)}/s",
                           p.Total is long t2 && t2 > 0 ? 0.6 * p.Done / t2 : null)), ct);
            }

            // Validate the download IS the version the recipe says (Drive: sha256, ModDB: md5).
            Report("Validando versão (hash)", 0.6);
            var hash = await Downloader.Sha256Async(file, null, ct);
            if (!string.IsNullOrWhiteSpace(a.Sha256) && !hash.Equals(a.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (file.StartsWith(s.DownloadsDir, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
                throw new InvalidDataException(
                    $"{a.File} não é a versão da receita (hash diferente). Se o arquivo do Drive foi trocado, rode tools/pack-mods.js e publique a receita nova.");
            }
            if (plan.PinnedChecksums && !string.IsNullOrWhiteSpace(a.Md5))
            {
                var md5 = await Downloader.Md5Async(file, ct);
                if (!md5.Equals(a.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    if (file.StartsWith(s.DownloadsDir, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
                    throw new InvalidDataException($"MD5 não confere em {a.File} (arquivo corrompido ou versão diferente). Rode de novo para baixar outra vez.");
                }
            }

            var tmp = Path.Combine(s.DownloadsDir, ".extract", a.Id);
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            await extractor.ExtractAsync(file, tmp, new Progress<int>(p => Report($"Extraindo {p}%", 0.65 + 0.3 * p / 100.0)), ct);
            var root = ResolveRoot(tmp, a);

            Report("Instalando", 0.97);
            switch (a.Kind)
            {
                case AssetKind.Mod:
                    var dest = Path.Combine(s.Mo2Root, "mods", a.ModFolder);
                    if (Directory.Exists(dest)) Directory.Delete(dest, true);
                    if (plan.Folders.Count == 0) MoveDir(root, dest);
                    else
                        foreach (var sub in plan.Folders) // GAMMA instructions: merge these folders in order
                        {
                            var srcSub = FindSub(tmp, sub)
                                ?? throw new DirectoryNotFoundException($"Pasta \"{sub}\" não existe dentro de {plan.File}.");
                            CopyDir(srcSub, dest);
                        }
                    File.WriteAllText(Path.Combine(dest, "meta.ini"),
                        $"[General]\r\nversion={manifest.Version}\r\ninstalled=true\r\nnotes=Installed by REIMA ({a.Id})\r\n");
                    break;
                case AssetKind.Anomaly:
                    var adest = Path.Combine(s.AnomalyRoot, a.Target ?? "");
                    var aback = Path.Combine(backupDir, a.Target ?? "");
                    OverlayWithBackup(root, adest, aback);
                    // remembered so the option can be switched off later (files removed, originals restored)
                    state["files:" + a.Id] = string.Join("|", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(s.AnomalyRoot, Path.Combine(adest, Path.GetRelativePath(root, f)))));
                    state["backup:" + a.Id] = Path.GetRelativePath(s.AnomalyRoot, aback);
                    break;
                case AssetKind.Mo2:
                    OverlayWithBackup(root, Path.Combine(s.Mo2Root, a.Target ?? ""), Path.Combine(backupDir, "MO2", a.Target ?? ""));
                    break;
            }
            Directory.Delete(tmp, true);
            state[a.Id] = plan.Expected ?? "sha256:" + hash;
            SaveState(state);
            updated++;
            Log?.Invoke($"Instalado: {a.Name}");
        }
        Log?.Invoke($"{updated} instalado(s)/atualizado(s), {skipped} já estavam na versão mais nova.");

        Packs.Cleanup();
        progress.Report(new("Perfil do MO2", manifest.Profile, 0.99, null));
        WriteProfile(assets);
        ApplyConfigPatches();
        progress.Report(new("Concluído", $"{assets.Count} itens", 1, 1));
        Log?.Invoke("Instalação concluída.");
    }

    /// <summary>
    /// Resolves a GAMMA install path inside the extracted archive. Leading slashes are ignored, and when a
    /// segment is missing (version in the folder name changed) the sibling with the same name minus digits is used.
    /// </summary>
    static string? FindSub(string root, string sub)
    {
        var dir = root;
        foreach (var seg in sub.Split('\\', '/').Where(x => x.Length > 0))
        {
            var exact = Path.Combine(dir, seg);
            if (Directory.Exists(exact)) { dir = exact; continue; }
            static string Shape(string n) => System.Text.RegularExpressions.Regex.Replace(n.ToLowerInvariant(), @"v?\d+([._-]?\d+)*[a-z]?", "#");
            var near = Directory.GetDirectories(dir).FirstOrDefault(d => Shape(Path.GetFileName(d)) == Shape(seg));
            if (near is null) return null;
            dir = near;
        }
        return dir;
    }

    bool TargetExists(Asset a) => a.Kind != AssetKind.Mod || Directory.Exists(Path.Combine(s.Mo2Root, "mods", a.ModFolder));

    /// <summary>Uses ArchiveRoot if given; otherwise unwraps a single top folder that is not itself gamedata.</summary>
    static string ResolveRoot(string tmp, Asset a)
    {
        if (!string.IsNullOrWhiteSpace(a.ArchiveRoot)) return Path.Combine(tmp, a.ArchiveRoot);
        var dir = tmp;
        while (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir) is [var only]
               && !Path.GetFileName(only).Equals("gamedata", StringComparison.OrdinalIgnoreCase)
               && a.Kind == AssetKind.Mod)
            dir = only;
        return dir;
    }

    static void MoveDir(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (Path.GetPathRoot(src) == Path.GetPathRoot(dest)) Directory.Move(src, dest);
        else { CopyDir(src, dest); Directory.Delete(src, true); }
    }

    static void CopyDir(string src, string dest)
    {
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, d)));
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(dest, Path.GetRelativePath(src, f)), true);
    }

    /// <summary>
    /// Option switched off (e.g. OptiScaler → Original): files the asset put in the Anomaly folder are removed
    /// and whatever they had replaced comes back from the backup taken when it was installed.
    /// </summary>
    void RemoveDeselected(Dictionary<string, string> state)
    {
        var active = ActiveAssets().Select(a => a.Id).ToHashSet();
        foreach (var a in manifest.Assets.Where(a => a.Kind == AssetKind.Anomaly && a.RemoveWhenOff && !active.Contains(a.Id)))
        {
            if (!state.TryGetValue("files:" + a.Id, out var files)) continue;
            state.TryGetValue("backup:" + a.Id, out var backupRel);
            var backupRoot = backupRel is null ? null : Path.Combine(s.AnomalyRoot, backupRel);
            var target = Path.Combine(s.AnomalyRoot, a.Target ?? "");
            foreach (var rel in files.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = Path.Combine(s.AnomalyRoot, rel);
                if (File.Exists(f)) File.Delete(f);
                var orig = backupRoot is null ? null : Path.Combine(backupRoot, Path.GetRelativePath(target, f));
                if (orig is not null && File.Exists(orig)) File.Copy(orig, f, true);
            }
            // drop folders the asset created that are now empty
            foreach (var d in files.Split('|').Select(r => Path.GetDirectoryName(Path.Combine(s.AnomalyRoot, r))).Distinct()
                         .OrderByDescending(d => d!.Length))
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d!).Any() && d != target) Directory.Delete(d!); } catch { }
            state.Remove("files:" + a.Id); state.Remove("backup:" + a.Id); state.Remove(a.Id);
            SaveState(state);
            Log?.Invoke($"Removido (opção desligada): {a.Name}");
        }
    }

    void OverlayWithBackup(string src, string dest, string backup)
    {
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dest, rel);
            if (File.Exists(target))
            {
                var b = Path.Combine(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(b)!);
                if (!File.Exists(b)) File.Copy(target, b);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, true);
        }
        Log?.Invoke($"Arquivos substituídos têm backup em {backup}");
    }

    /// <summary>
    /// MO2 modlist.txt: first line = highest priority; a separator sits BELOW its mods.
    /// The REIMA block goes on top; every other line of the existing profile is kept below it.
    /// </summary>
    void WriteProfile(List<Asset> assets)
    {
        var dir = Path.Combine(s.Mo2Root, "profiles", manifest.Profile);
        if (!Directory.Exists(dir))
        {
            var basePath = s.BaseProfile is null ? null : Path.Combine(s.Mo2Root, "profiles", s.BaseProfile);
            if (basePath is not null && Directory.Exists(basePath)) CopyDir(basePath, dir);
            else Directory.CreateDirectory(dir);
        }
        var listPath = Path.Combine(dir, "modlist.txt");
        var old = File.Exists(listPath) ? File.ReadAllLines(listPath).ToList() : [];
        if (File.Exists(listPath)) File.Copy(listPath, listPath + ".bak_reima", true);

        var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var block = new List<string>();
        foreach (var group in assets.Where(a => a.Kind == AssetKind.Mod).GroupBy(a => a.Separator))
        {
            foreach (var a in group) { block.Add((a.Enabled ? "+" : "-") + a.ModFolder); managed.Add(a.ModFolder); }
            if (group.Key is string sep)
            {
                var sepName = sep + "_separator";
                block.Add("+" + sepName);
                managed.Add(sepName);
                Directory.CreateDirectory(Path.Combine(s.Mo2Root, "mods", sepName));
            }
        }
        var rest = old.Where(l => l.Length < 2 || !managed.Contains(l[1..])).Where(l => !l.StartsWith('#'));
        var header = "# This file was automatically generated by Mod Organizer.";
        File.WriteAllText(listPath, string.Join("\r\n", new[] { header }.Concat(block).Concat(rest)) + "\r\n", new UTF8Encoding(false));
        Log?.Invoke($"Perfil \"{manifest.Profile}\" atualizado ({block.Count} linhas do REIMA).");
    }

    /// <summary>Sets "key value" lines in ltx files (X-Ray console format), appending missing keys.</summary>
    void ApplyConfigPatches()
    {
        foreach (var p in manifest.ConfigPatches.Where(p => p.Requires.All(s.Selected.Contains)))
        {
            var path = Path.Combine(s.AnomalyRoot, p.File);
            if (!File.Exists(path)) { Log?.Invoke($"Config não encontrada (pulei): {p.File}"); continue; }
            File.Copy(path, path + ".bak_reima", true);
            var lines = File.ReadAllLines(path).ToList();
            foreach (var (k, v) in p.Set)
            {
                int idx = lines.FindIndex(l => l.Split(' ', 2)[0].Equals(k, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) lines[idx] = $"{k} {v}"; else lines.Add($"{k} {v}");
            }
            File.WriteAllLines(path, lines);
            Log?.Invoke($"Config aplicada: {p.File}");
        }
    }

    Dictionary<string, string> LoadState() =>
        File.Exists(StatePath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StatePath)) ?? []
            : [];

    void SaveState(Dictionary<string, string> st) =>
        File.WriteAllText(StatePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));

    public static string Fmt(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
