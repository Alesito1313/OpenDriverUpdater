using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDriverUpdater.Core
{
    static class Net
    {
        public static readonly HttpClient Http = CreateClient();

        static HttpClient CreateClient()
        {
            ServicePointManager.DefaultConnectionLimit = 8;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) OpenDriverUpdater/" + Program.Version);
            // el catalogo lo pedimos en ingles para que las fechas y columnas salgan siempre igual
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en;q=0.8");
            return c;
        }

        public static Task<string> GetString(string url, CancellationToken ct) =>
            Retry(async t =>
            {
                using (var r = await Http.GetAsync(url, t))
                {
                    r.EnsureSuccessStatusCode();
                    return await r.Content.ReadAsStringAsync();
                }
            }, ct);

        public static Task<string> PostForm(string url, Dictionary<string, string> form, CancellationToken ct) =>
            Retry(async t =>
            {
                using (var content = new FormUrlEncodedContent(form))
                using (var r = await Http.PostAsync(url, content, t))
                {
                    r.EnsureSuccessStatusCode();
                    return await r.Content.ReadAsStringAsync();
                }
            }, ct);

        // 3 intentos, 45 s cada uno. El catalogo de Microsoft a veces tarda o da error sin motivo
        static async Task<T> Retry<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(45));
                    try
                    {
                        return await action(cts.Token);
                    }
                    catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested)
                    {
                    }
                }
                await Task.Delay(1500 * attempt, ct);
            }
        }

        public static async Task Download(string url, string path, IProgress<string> progress, string label, CancellationToken ct)
        {
            using (var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                r.EnsureSuccessStatusCode();
                long total = r.Content.Headers.ContentLength ?? 0;
                using (var src = await r.Content.ReadAsStreamAsync())
                using (var dst = File.Create(path))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    int lastPct = -1, n;
                    progress.Report(label);
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n, ct);
                        done += n;
                        if (total <= 0) continue;
                        int pct = (int)(done * 100 / total);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            progress.Report(label + " (" + pct + "%)");
                        }
                    }
                }
            }
        }

        /// <summary>Ejecuta un programa de consola (expand, pnputil...) y devuelve codigo de salida + texto.</summary>
        public static async Task<(int code, string output)> Run(string exe, string args, CancellationToken ct)
        {
            Encoding enc;
            try { enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { enc = Encoding.Default; }

            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc,
            };
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                await WaitForExit(p, ct);
                return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
            }
        }

        public static Task WaitForExit(Process p, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>();
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) => tcs.TrySetResult(true);
            if (p.HasExited) tcs.TrySetResult(true);
            ct.Register(() => tcs.TrySetCanceled());
            return tcs.Task;
        }

        public static string LastLines(string text, int n)
        {
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var start = Math.Max(0, lines.Length - n);
            return string.Join(" ", lines, start, lines.Length - start).Trim();
        }
    }
}
