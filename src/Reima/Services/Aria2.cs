using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Reima.Services;

/// <summary>Runs the bundled aria2c for torrent/magnet downloads (official Anomaly torrent).</summary>
public sealed partial class Aria2(string exe)
{
    public static string? Find()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "tools", "aria2c.exe");
        return File.Exists(p) ? p : null;
    }

    public async Task DownloadAsync(string uri, string dir, IProgress<TransferProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(exe)
        {
            ArgumentList =
            {
                $"--dir={dir}",
                "--seed-time=0",              // stop seeding as soon as the file is complete
                "--follow-torrent=mem",
                "--continue=true",
                "--check-integrity=true",
                "--file-allocation=none",
                "--summary-interval=1",
                "--console-log-level=warn",
                "--bt-tracker-connect-timeout=10",
                "--max-connection-per-server=8",
                uri,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não consegui iniciar o aria2c.");
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        _ = Task.Run(async () =>
        {
            string? l;
            while ((l = await p.StandardError.ReadLineAsync()) is not null) if (l.Trim().Length > 0) log?.Invoke("aria2: " + l.Trim());
        });

        string? line;
        while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            var m = Summary().Match(line);
            if (m.Success)
                progress?.Report(new(ToBytes(m.Groups["done"].Value), ToBytes(m.Groups["total"].Value),
                    m.Groups["dl"].Success ? ToBytes(m.Groups["dl"].Value) : 0));
            else if (line.Contains("ERR", StringComparison.Ordinal)) log?.Invoke("aria2: " + line.Trim());
        }
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"aria2c terminou com código {p.ExitCode}. Veja o log.");
    }

    static long ToBytes(string s)
    {
        var m = Size().Match(s);
        if (!m.Success) return 0;
        double v = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (long)(v * m.Groups[2].Value switch { "KiB" => 1024, "MiB" => 1 << 20, "GiB" => 1 << 30, _ => 1 });
    }

    // e.g. [#2089b0 1.2GiB/7.6GiB(16%) CN:5 SD:3 DL:12MiB ETA:9m]
    [GeneratedRegex(@"\[#\w+\s+(?<done>[\d.]+\w*)/(?<total>[\d.]+\w*)\(\d+%\)(?:.*?DL:(?<dl>[\d.]+\w*))?")]
    private static partial Regex Summary();

    [GeneratedRegex(@"([\d.]+)(B|KiB|MiB|GiB)?")]
    private static partial Regex Size();
}
