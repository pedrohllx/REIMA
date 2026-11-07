// Drive quota detection: a locked file must raise DriveQuotaException quickly (no 8-minute retry loop).
// usage: DriveCheck <drive url> <scratch file>
using Reima.Services;
using var http = new System.Net.Http.HttpClient();
var d = new Downloader(http);
d.Retrying += m => Console.WriteLine("retry: " + m);
var sw = System.Diagnostics.Stopwatch.StartNew();
try { await d.DownloadAsync(args[0], args[1], null, CancellationToken.None); Console.WriteLine("baixou (sem cota)"); }
catch (DriveQuotaException e) { Console.WriteLine($"cota detectada em {sw.Elapsed.TotalSeconds:0}s: {e.Message}"); }
