using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace OpenDriverUpdater.Services;

/// <summary>Cliente HTTP compartido con reintentos y descargas con progreso.</summary>
internal static class Http
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) OpenDriverUpdater/0.2");
        // El catálogo se pide en inglés para que columnas y fechas tengan un formato fijo
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
        c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en;q=0.8");
        return c;
    }

    public static Task<string> GetStringAsync(string url, CancellationToken ct) =>
        WithRetry(async t =>
        {
            using var r = await Client.GetAsync(url, t);
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadAsStringAsync(t);
        }, ct);

    public static Task<string> PostFormAsync(string url, IDictionary<string, string> form, CancellationToken ct) =>
        WithRetry(async t =>
        {
            using var content = new FormUrlEncodedContent(form);
            using var r = await Client.PostAsync(url, content, t);
            r.EnsureSuccessStatusCode();
            return await r.Content.ReadAsStringAsync(t);
        }, ct);

    private static async Task<T> WithRetry<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                return await action(cts.Token);
            }
            catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                await Task.Delay(1500 * attempt, ct);
            }
        }
    }

    public static async Task DownloadFileAsync(string url, string path, IProgress<string> progress, string label, CancellationToken ct)
    {
        using var r = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        long? total = r.Content.Headers.ContentLength;

        await using var src = await r.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int lastPct = -1;
        int n;
        progress.Report(label);
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (total > 0)
            {
                int pct = (int)(done * 100 / total.Value);
                if (pct != lastPct)
                {
                    lastPct = pct;
                    progress.Report($"{label} ({pct}%)");
                }
            }
        }
    }
}

internal static class ProcessRunner
{
    static ProcessRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Ejecuta un programa de consola y devuelve código de salida y salida de texto.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, CancellationToken ct)
    {
        Encoding enc;
        try { enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { enc = Encoding.Default; }

        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo ejecutar {file}");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, ((await outTask) + (await errTask)).Trim());
    }
}
