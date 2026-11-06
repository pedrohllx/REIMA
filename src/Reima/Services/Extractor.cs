using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Reima.Services;

/// <summary>Extracts archives with 7-Zip (7z/zip/rar, multi-part) and reports percent.</summary>
public sealed partial class Extractor(string sevenZipExe)
{
    public static string? Find(string? gammaRoot)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tools", "7z.exe"),
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            gammaRoot is null ? "" : Path.Combine(gammaRoot, ".Grok's Modpack Installer", "resources", "7zip", "7z.exe"),
        ];
        return candidates.FirstOrDefault(c => c.Length > 0 && File.Exists(c));
    }

    public async Task ExtractAsync(string archive, string outDir, IProgress<int>? percent, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var psi = new ProcessStartInfo(sevenZipExe)
        {
            ArgumentList = { "x", "-y", "-bsp1", "-bso0", $"-o{outDir}", archive },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não consegui iniciar o 7-Zip.");
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });

        var errTask = p.StandardError.ReadToEndAsync(ct);
        var buf = new char[256];
        int n;
        while ((n = await p.StandardOutput.ReadAsync(buf, ct)) > 0)
        {
            var m = Percent().Matches(new string(buf, 0, n));
            if (m.Count > 0) percent?.Report(int.Parse(m[^1].Groups[1].Value));
        }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode > 1) // 1 = warnings
            throw new InvalidOperationException($"7-Zip falhou ({p.ExitCode}) em {Path.GetFileName(archive)}: {(await errTask).Trim()}");
        percent?.Report(100);
    }

    /// <summary>
    /// Extracts only the given paths (folders keep their full path under outDir). One 7-Zip run per archive,
    /// so a big solid pack is decompressed once no matter how many mods come out of it.
    /// </summary>
    public async Task ExtractPathsAsync(string archive, string outDir, IEnumerable<string> paths, IProgress<int>? percent, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var list = Path.Combine(outDir, ".reima_list.txt");
        await File.WriteAllLinesAsync(list, paths, new System.Text.UTF8Encoding(false), ct);
        await RunAsync(["x", "-y", "-bsp1", "-bso0", "-sccUTF-8", "-scsUTF-8", $"-o{outDir}", archive, $"-i@{list}"], archive, percent, ct);
        File.Delete(list);
    }

    /// <summary>Extracts one file (by its path inside the archive) flat into outDir and returns its path.</summary>
    public async Task<string> ExtractFileAsync(string archive, string inner, string outDir, IProgress<int>? percent, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        await RunAsync(["e", "-y", "-bsp1", "-bso0", $"-o{outDir}", archive, inner], archive, percent, ct);
        var file = Path.Combine(outDir, Path.GetFileName(inner.Replace('\\', '/')));
        return File.Exists(file) ? file : throw new FileNotFoundException($"\"{inner}\" não existe em {Path.GetFileName(archive)}.");
    }

    async Task RunAsync(string[] args, string archive, IProgress<int>? percent, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(sevenZipExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não consegui iniciar o 7-Zip.");
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        var errTask = p.StandardError.ReadToEndAsync(ct);
        var buf = new char[256];
        int n;
        while ((n = await p.StandardOutput.ReadAsync(buf, ct)) > 0)
        {
            var m = Percent().Matches(new string(buf, 0, n));
            if (m.Count > 0) percent?.Report(int.Parse(m[^1].Groups[1].Value));
        }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode > 1)
            throw new InvalidOperationException($"7-Zip falhou ({p.ExitCode}) em {Path.GetFileName(archive)}: {(await errTask).Trim()}");
        percent?.Report(100);
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex Percent();
}
