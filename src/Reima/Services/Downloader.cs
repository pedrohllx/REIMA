using System.Net.Http;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Reima.Services;

public readonly record struct TransferProgress(long Done, long? Total, double BytesPerSecond);

/// <summary>Resumable HTTP downloads with Google Drive link handling and SHA-256 verification.</summary>
public sealed partial class Downloader(HttpClient http)
{
    /// <summary>Turns share links into direct links. Google Drive large files need confirm=t to skip the virus-scan page.</summary>
    public static Uri Normalize(string url)
    {
        var m = DriveFileId().Match(url);
        if (m.Success)
            return new Uri($"https://drive.usercontent.google.com/download?id={m.Groups[1].Value}&export=download&confirm=t");
        return new Uri(url);
    }

    /// <summary>
    /// ModDB "addons/start/ID" pages are HTML; the real file is behind a "/downloads/mirror/..." link
    /// that redirects to a CDN. Same approach as GAMMA's launcher.
    /// </summary>
    async Task<Uri> ResolveAsync(string url, CancellationToken ct)
    {
        if (!url.Contains("moddb.com/addons/start/", StringComparison.OrdinalIgnoreCase)) return Normalize(url);
        var html = await http.GetStringAsync(url, ct);
        var m = ModDbMirror().Match(html);
        if (!m.Success) throw new InvalidOperationException($"Não achei o link de download na página do ModDB: {url}");
        return new Uri(new Uri("https://www.moddb.com"), WebUtility.HtmlDecode(m.Groups[1].Value));
    }

    public async Task DownloadAsync(string url, string dest, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, await ResolveAsync(url, ct));
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Move(part, dest, true); // already complete
            return;
        }
        resp.EnsureSuccessStatusCode();

        if (resp.Content.Headers.ContentType?.MediaType == "text/html")
            throw new InvalidOperationException(
                "O servidor devolveu uma página em vez do arquivo. Se for Google Drive, confira se o link está como " +
                "\"qualquer pessoa com o link\" e se a cota de download não estourou.");

        bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        long? total = resp.Content.Headers.ContentLength is long len ? len + existing : null;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            var buf = new byte[1 << 20];
            long done = existing, windowStart = done;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (sw.ElapsedMilliseconds >= 250)
                {
                    progress?.Report(new(done, total, (done - windowStart) / sw.Elapsed.TotalSeconds));
                    windowStart = done;
                    sw.Restart();
                }
            }
            progress?.Report(new(done, total, 0));
        }
        File.Move(part, dest, true);
    }

    public static async Task<string> Sha256Async(string file, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long done = 0;
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            done += n;
            progress?.Report(new(done, fs.Length, 0));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static async Task<bool> VerifyAsync(string file, string? expected, IProgress<TransferProgress>? progress, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(expected) ||
        string.Equals(await Sha256Async(file, progress, ct), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    public static async Task<string> Md5Async(string file, CancellationToken ct)
    {
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        return Convert.ToHexString(await MD5.HashDataAsync(fs, ct));
    }

    [GeneratedRegex(@"href=""(/downloads/mirror/[^""]+)""")]
    private static partial Regex ModDbMirror();

    [GeneratedRegex(@"drive\.google\.com/(?:file/d/|open\?id=|uc\?(?:export=download&)?id=)([\w-]{10,})")]
    private static partial Regex DriveFileId();
}
