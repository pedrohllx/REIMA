using System.IO;
using System.Text;
using Reima.Models;

namespace Reima.Services;

/// <summary>
/// Clean base install, like GAMMA's manual steps but automatic:
/// Anomaly 1.5.3 (official torrent) + portable Mod Organizer 2 pointed at it, with the REIMA profile ready for mods.
/// Each part is skipped when it is already installed.
/// </summary>
public sealed class BaseSetup(BaseGame game, InstallSettings s, Downloader downloader, Extractor extractor, Aria2? aria2, string profile)
{
    public event Action<string>? Log;

    public bool AnomalyInstalled => File.Exists(Path.Combine(s.AnomalyRoot, "AnomalyLauncher.exe"));
    public bool Mo2Installed => File.Exists(Path.Combine(s.Mo2Root, "ModOrganizer.exe"));

    public async Task RunAsync(IProgress<StepProgress> progress, CancellationToken ct)
    {
        if (!AnomalyInstalled)
        {
            var archive = await FetchAsync("Anomaly 1.5.3", game.Anomaly, 0.0, 0.45, progress, ct);
            await ExtractToAsync(archive, s.AnomalyRoot, "AnomalyLauncher.exe", "Anomaly 1.5.3", 0.45, 0.75, progress, ct);
            Log?.Invoke($"Anomaly instalado em {s.AnomalyRoot}");
        }
        else Log?.Invoke("Anomaly já instalado, pulei.");

        if (!Mo2Installed)
        {
            var archive = await FetchAsync("Mod Organizer 2", game.Mo2, 0.75, 0.9, progress, ct);
            await ExtractToAsync(archive, s.Mo2Root, "ModOrganizer.exe", "Mod Organizer 2", 0.9, 0.97, progress, ct);
            Log?.Invoke($"Mod Organizer 2 instalado em {s.Mo2Root}");
        }
        else Log?.Invoke("Mod Organizer 2 já instalado, pulei.");

        progress.Report(new("Configurando o Mod Organizer 2", "modo portátil, caminho do jogo, executáveis, perfil", 0.98, null));
        ConfigureMo2();
        progress.Report(new("Jogo base pronto", "Anomaly + MO2 configurados", 1, 1));
    }

    async Task<string> FetchAsync(string title, BaseFile f, double from, double to, IProgress<StepProgress> progress, CancellationToken ct)
    {
        var file = new[] { s.DownloadsDir }.Concat(s.CacheDirs).Select(d => Path.Combine(d, f.File)).FirstOrDefault(File.Exists)
                   ?? Path.Combine(s.DownloadsDir, f.File);
        var prog = new Progress<TransferProgress>(p =>
        {
            double? cur = p.Total is long t && t > 0 ? (double)p.Done / t : null;
            progress.Report(new($"Baixando {title}",
                $"{Installer.Fmt(p.Done)}{(p.Total is long tt ? " / " + Installer.Fmt(tt) : "")}  {Installer.Fmt((long)p.BytesPerSecond)}/s",
                from + (to - from) * 0.95 * (cur ?? 0), cur));
        });

        if (!File.Exists(file))
        {
            Log?.Invoke($"Baixando {title}: {f.Url[..Math.Min(f.Url.Length, 80)]}...");
            if (f.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                if (aria2 is null) throw new InvalidOperationException("aria2c.exe não encontrado na pasta tools do launcher.");
                await aria2.DownloadAsync(f.Url, s.DownloadsDir, prog, Log, ct);
            }
            else await downloader.DownloadAsync(f.Url, file, prog, ct);
        }
        if (!File.Exists(file))
            throw new FileNotFoundException($"O download de {title} terminou mas {f.File} não apareceu. Baixe manualmente em {f.Page} e coloque na pasta downloads.");

        progress.Report(new($"Verificando {title}", "SHA-256", from + (to - from) * 0.97, null));
        if (!await Downloader.VerifyAsync(file, f.Sha256, null, ct))
            throw new InvalidDataException($"Hash de {f.File} não confere. Apague o arquivo e tente de novo, ou baixe manualmente em {f.Page}.");
        return file;
    }

    /// <summary>Extracts and moves the folder that contains <paramref name="marker"/> into dest (handles a wrapper folder).</summary>
    async Task ExtractToAsync(string archive, string dest, string marker, string title, double from, double to,
        IProgress<StepProgress> progress, CancellationToken ct)
    {
        var tmp = Path.Combine(s.DownloadsDir, ".extract", Path.GetFileNameWithoutExtension(archive));
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        await extractor.ExtractAsync(archive, tmp,
            new Progress<int>(p => progress.Report(new($"Extraindo {title}", $"{p}%", from + (to - from) * p / 100.0, p / 100.0))), ct);

        var root = Directory.EnumerateFiles(tmp, marker, SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                   ?? throw new InvalidDataException($"{marker} não encontrado dentro de {Path.GetFileName(archive)}.");
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(root!, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(root!, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(f, target, true);
        }
        Directory.Delete(tmp, true);
    }

    /// <summary>Portable instance, game path, executables, REIMA profile. Existing ini is left untouched.</summary>
    void ConfigureMo2()
    {
        File.WriteAllText(Path.Combine(s.Mo2Root, "portable.txt"), "");
        foreach (var d in new[] { "mods", "downloads", "overwrite", Path.Combine("profiles", profile) })
            Directory.CreateDirectory(Path.Combine(s.Mo2Root, d));

        var modlist = Path.Combine(s.Mo2Root, "profiles", profile, "modlist.txt");
        if (!File.Exists(modlist)) File.WriteAllText(modlist, "# This file was automatically generated by Mod Organizer.\r\n");

        var ini = Path.Combine(s.Mo2Root, "ModOrganizer.ini");
        if (File.Exists(ini)) { Log?.Invoke("ModOrganizer.ini já existe, mantive o seu."); return; }

        string Win(string p) => p.Replace("\\", "\\\\");
        string Fwd(string p) => p.Replace('\\', '/');
        var a = s.AnomalyRoot.TrimEnd('\\', '/');
        (string title, string bin, string wd)[] exes =
        [
            ("Anomaly (DX11-AVX)", $"{a}\\bin\\AnomalyDX11AVX.exe", $"{a}\\bin"),
            ("Anomaly (DX11)", $"{a}\\bin\\AnomalyDX11.exe", $"{a}\\bin"),
            ("Anomaly Launcher", $"{a}\\AnomalyLauncher.exe", a),
        ];
        var sb = new StringBuilder();
        sb.AppendLine("[General]");
        sb.AppendLine("gameName=STALKER Anomaly");
        sb.AppendLine($"gamePath=@ByteArray({Win(a)})");
        sb.AppendLine($"selected_profile=@ByteArray({profile})");
        sb.AppendLine("first_start=false");
        sb.AppendLine();
        sb.AppendLine("[customExecutables]");
        sb.AppendLine($"size={exes.Length}");
        for (int i = 0; i < exes.Length; i++)
        {
            var n = i + 1;
            sb.AppendLine($"{n}\\arguments=");
            sb.AppendLine($"{n}\\binary={Fwd(exes[i].bin)}");
            sb.AppendLine($"{n}\\hide=false");
            sb.AppendLine($"{n}\\ownicon=true");
            sb.AppendLine($"{n}\\steamAppID=");
            sb.AppendLine($"{n}\\title={exes[i].title}");
            sb.AppendLine($"{n}\\toolbar=false");
            sb.AppendLine($"{n}\\workingDirectory={Fwd(exes[i].wd)}");
        }
        File.WriteAllText(ini, sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
        Log?.Invoke("ModOrganizer.ini criado (modo portátil, Anomaly configurado).");
    }
}
