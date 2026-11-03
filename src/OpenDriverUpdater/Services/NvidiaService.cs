using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenDriverUpdater.Models;

namespace OpenDriverUpdater.Services;

/// <summary>
/// Consulta la última versión publicada por NVIDIA en su web oficial para tu tarjeta gráfica
/// (el mismo servicio que usa la aplicación de NVIDIA). NVIDIA suele publicar en su web
/// semanas antes que en Windows Update o en el catálogo de Microsoft.
/// </summary>
public static class NvidiaService
{
    private const string LookupUrl =
        "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/" +
        "com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";

    public static bool IsNvidiaGpu(DeviceInfo d) =>
        d.HardwareIds.Any(id => id.StartsWith("PCI\\VEN_10DE&DEV_", StringComparison.OrdinalIgnoreCase)) &&
        (d.DeviceClass.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

    public static async Task<DriverCandidate?> GetLatestAsync(DeviceInfo d, CancellationToken ct)
    {
        var hw = d.HardwareIds.First(id => id.StartsWith("PCI\\VEN_10DE&DEV_", StringComparison.OrdinalIgnoreCase));
        var devId = Regex.Match(hw, @"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();

        var payload = new Dictionary<string, object>
        {
            ["dIDa"] = new[] { $"{devId}_10DE" },
            ["osC"] = "10.0",
            ["osB"] = Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture),
            ["is6"] = "1",
            ["lg"] = "1033",
            ["iLp"] = "1",
            ["prvMd"] = "0",
            ["gcV"] = "3.28.0.417",
            ["gIsB"] = "1",
            ["dch"] = IsDch() ? "1" : "0",
            ["upCRD"] = "0",
            ["isCRD"] = "0",
        };
        var url = LookupUrl + Uri.EscapeDataString(JsonSerializer.Serialize(payload));
        var text = await Http.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(text);
        var info = FindObjectWith(doc.RootElement, "DownloadURL");
        if (info is null) return null;

        var version = GetString(info.Value, "Version");
        var download = GetString(info.Value, "DownloadURL");
        if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(download)) return null;

        var installed = VersionUtil.NvidiaFromWindows(d.Version);
        bool? newer = installed is null ? null : VersionUtil.Compare(version, installed) > 0;
        var name = GetString(info.Value, "Name") ?? "Driver NVIDIA";

        return new DriverCandidate
        {
            Source = DriverSource.Nvidia,
            Title = $"{name} {version}",
            Vendor = "NVIDIA",
            Version = version,
            Date = ParseDate(GetString(info.Value, "ReleaseDateTime")),
            SizeBytes = ParseSize(GetString(info.Value, "DownloadURLFileSize")),
            DownloadUrl = download.Replace("http://", "https://"),
            InfoUrl = GetString(info.Value, "DetailsURL"),
            Match = MatchQuality.Exact,
            MatchedId = hw,
            NewerThanInstalled = newer,
        };
    }

    /// <summary>Descarga el instalador oficial y lo abre (el asistente de NVIDIA guía la instalación).</summary>
    public static async Task<InstallOutcome> InstallAsync(DriverCandidate c, IProgress<string> progress, CancellationToken ct)
    {
        if (c.DownloadUrl is null) return InstallOutcome.Fail("NVIDIA no proporcionó enlace de descarga");

        var work = Path.Combine(Path.GetTempPath(), "OpenDriverUpdater", "nvidia");
        Directory.CreateDirectory(work);
        var file = Path.Combine(work, Path.GetFileName(new Uri(c.DownloadUrl).LocalPath));
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file += ".exe";

        await Http.DownloadFileAsync(c.DownloadUrl, file, progress, $"Descargando: {c.Title}", ct);

        progress.Report("Abriendo el instalador de NVIDIA: sigue sus pasos en la ventana que aparece…");
        using var p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })
                      ?? throw new InvalidOperationException("No se pudo abrir el instalador de NVIDIA");
        await p.WaitForExitAsync(ct);

        return p.ExitCode == 0
            ? new InstallOutcome(InstallStatus.Succeeded, true, "Instalador de NVIDIA completado")
            : InstallOutcome.Fail($"El instalador de NVIDIA terminó con código {p.ExitCode} (¿se canceló?)");
    }

    private static bool IsDch()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\nvlddmkm");
            return key is null || key.GetValue("DCHUVen") is not null;
        }
        catch { return true; }
    }

    private static JsonElement? FindObjectWith(JsonElement e, string property)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    if (p.NameEquals(property)) return e;
                foreach (var p in e.EnumerateObject())
                    if (FindObjectWith(p.Value, property) is { } found) return found;
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (FindObjectWith(item, property) is { } found) return found;
                break;
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // Formato actual de NVIDIA: "2026-10-21T00:00:00+0000"
        var iso = Regex.Match(s, @"^(\d{4}-\d{2}-\d{2})");
        if (iso.Success && DateTime.TryParseExact(iso.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var di))
            return di;
        // Formatos antiguos: "Tue Oct 01, 2024"
        var clean = Regex.Replace(s, @"^[A-Za-z]{3}\s+", "");
        foreach (var fmt in new[] { "MMM dd, yyyy", "MMM d, yyyy", "yyyy-MM-dd", "M/d/yyyy" })
            if (DateTime.TryParseExact(clean, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d)) return d;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2) ? d2 : null;
    }

    private static long ParseSize(string? s)
    {
        var m = Regex.Match(s ?? "", @"([\d.]+)\s*(MB|GB)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        return (long)(n * 1024 * 1024 * (m.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase) ? 1024 : 1));
    }
}
