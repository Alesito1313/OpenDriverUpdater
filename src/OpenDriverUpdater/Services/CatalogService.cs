using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OpenDriverUpdater.Models;

namespace OpenDriverUpdater.Services;

public sealed record CatalogEntry(
    string UpdateId, string Title, string Products, string Classification,
    DateTime? LastUpdated, string Version, long SizeBytes);

/// <summary>
/// Consulta el Catálogo de Microsoft Update (catalog.update.microsoft.com), donde los fabricantes
/// publican sus drivers certificados. A diferencia de Windows Update, aquí aparecen TODAS las
/// versiones publicadas para un Hardware ID, no solo la que Windows decide ofrecerte.
/// </summary>
public sealed partial class CatalogService
{
    private const string BaseUrl = "https://www.catalog.update.microsoft.com/";
    private const int MaxPages = 3; // 25 resultados por página, ordenados del más nuevo al más viejo

    private readonly ConcurrentDictionary<string, Lazy<Task<List<CatalogEntry>>>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Busca un Hardware ID. Las búsquedas repetidas (dispositivos iguales) se reutilizan.</summary>
    public Task<List<CatalogEntry>> SearchAsync(string query, CancellationToken ct) =>
        _cache.GetOrAdd(query, q => new Lazy<Task<List<CatalogEntry>>>(() => SearchCoreAsync(q, ct))).Value;

    private static async Task<List<CatalogEntry>> SearchCoreAsync(string query, CancellationToken ct)
    {
        var url = BaseUrl + "Search.aspx?q=" + Uri.EscapeDataString(query);
        var results = new List<CatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // El catálogo ordena por fecha (lo más nuevo primero) y pagina con "&p=N" (0, 1, 2...)
        for (int page = 0; page < MaxPages; page++)
        {
            string html = await Http.GetStringAsync(page == 0 ? url : $"{url}&p={page}", ct);
            int found = 0, added = 0;
            foreach (var e in ParseResults(html))
            {
                found++;
                if (seen.Add(e.UpdateId)) { results.Add(e); added++; }
            }

            bool hasNext = html.Contains("ctl00_catalogBody_nextPageLinkText", StringComparison.Ordinal);
            if (added == 0 || found < 25 || !hasNext) break;
        }
        return results;
    }

    // ───────────────────────── Análisis del HTML ─────────────────────────

    [GeneratedRegex(@"<tr[^>]*?\bid=""([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})_R\d+""[^>]*>(.*?)</tr>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<td[^>]*?\bid=""[0-9a-fA-F-]{36}_C(\d+)_R\d+""[^>]*>(.*?)</td>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CellByIdRegex();

    [GeneratedRegex(@"<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"_originalSize""[^>]*>\s*(\d+)\s*<", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalSizeRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    internal static IEnumerable<CatalogEntry> ParseResults(string html)
    {
        foreach (Match row in RowRegex().Matches(html))
        {
            var id = row.Groups[1].Value.ToLowerInvariant();
            var body = row.Groups[2].Value;

            var cells = new Dictionary<int, string>();
            foreach (Match c in CellByIdRegex().Matches(body))
                cells[int.Parse(c.Groups[1].Value)] = c.Groups[2].Value;

            if (cells.Count == 0)
            {
                // Plan B: por posición (0 = casilla, 1 = título, 2 = productos, ...)
                var all = CellRegex().Matches(body);
                for (int i = 1; i < all.Count; i++) cells[i] = all[i].Groups[1].Value;
            }

            string title = Clean(cells.GetValueOrDefault(1));
            if (title.Length == 0) continue;

            string sizeCell = cells.GetValueOrDefault(6) ?? "";
            long size = OriginalSizeRegex().Match(sizeCell) is { Success: true } sm ? long.Parse(sm.Groups[1].Value) : ParseSizeText(Clean(sizeCell));

            yield return new CatalogEntry(
                id, title,
                Clean(cells.GetValueOrDefault(2)),
                Clean(cells.GetValueOrDefault(3)),
                ParseDate(Clean(cells.GetValueOrDefault(4))),
                Clean(cells.GetValueOrDefault(5)),
                size);
        }
    }

    private static string? HiddenField(string html, string name)
    {
        var m = Regex.Match(html, $@"<input[^>]*name=""{Regex.Escape(name)}""[^>]*value=""([^""]*)""", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static string Clean(string? html) =>
        html is null ? "" : SpaceRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(html, " ")), " ").Trim();

    private static DateTime? ParseDate(string s)
    {
        if (DateTime.TryParseExact(s, new[] { "M/d/yyyy", "MM/dd/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : null;
    }

    private static long ParseSizeText(string s)
    {
        var m = Regex.Match(s, @"([\d.,]+)\s*(KB|MB|GB)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        return m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => (long)(n * 1024),
            "MB" => (long)(n * 1024 * 1024),
            _ => (long)(n * 1024 * 1024 * 1024),
        };
    }

    // ───────────────────────── Filtros: ¿sirve para este PC? ─────────────────────────

    private static readonly int OsBuild = Environment.OSVersion.Version.Build;
    private static readonly Architecture OsArch = RuntimeInformation.OSArchitecture;

    private static readonly Dictionary<string, int> Win10Builds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1507"] = 10240, ["1511"] = 10586, ["1607"] = 14393, ["1703"] = 15063, ["1709"] = 16299,
        ["1803"] = 17134, ["1809"] = 17763, ["1903"] = 18362, ["1909"] = 18363, ["2004"] = 19041,
        ["20H2"] = 19042, ["21H1"] = 19043, ["21H2"] = 19044, ["22H2"] = 19045,
    };

    private static readonly Dictionary<string, int> Win11Builds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["21H2"] = 22000, ["22H2"] = 22621, ["23H2"] = 22631, ["24H2"] = 26100, ["25H2"] = 26200,
    };

    /// <summary>Solo drivers, para Windows 10/11, para tu versión de Windows y tu arquitectura.</summary>
    public static bool IsApplicable(CatalogEntry e)
    {
        if (e.Classification.Length > 0 && !e.Classification.Contains("Driver", StringComparison.OrdinalIgnoreCase))
            return false;

        var title = e.Title.ToLowerInvariant();
        if (OsArch == Architecture.X64 && (title.Contains("arm64") || title.Contains(" arm ")))
            return false;
        if (OsArch == Architecture.Arm64 && (title.Contains("x64") || title.Contains("amd64")))
            return false;

        if (e.Products.Length == 0) return true;
        // Ej.: "Windows 11 Client,  version 24H2 and later,  Servicing Drivers, Windows 10 and later drivers"
        // Cada producto empieza por "Windows" y lleva comas dentro, así que se separa antes de cada "Windows".
        foreach (var raw in Regex.Split(e.Products, @",\s*(?=Windows\b)", RegexOptions.IgnoreCase))
        {
            var p = SpaceRegex().Replace(raw, " ").Trim().ToLowerInvariant();
            bool is11 = p.Contains("windows 11");
            bool is10 = p.Contains("windows 10");
            if (!is10 && !is11) continue;
            if (is11 && !is10 && OsBuild < 22000) continue;

            var vm = Regex.Match(p, @"version\s+(\d{2}h\d|\d{4})");
            if (vm.Success)
            {
                var table = is11 ? Win11Builds : Win10Builds;
                if (table.TryGetValue(vm.Groups[1].Value, out var minBuild) && OsBuild < minBuild) continue;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Fabricante a partir del título. El catálogo usa dos formatos:
    /// "Realtek Driver Update (10.79.50.1003)" y "Realtek Semiconductor Corp. - Net - 10.68.524.2023".
    /// </summary>
    public static string VendorFromTitle(string title)
    {
        var m = Regex.Match(title, @"^(.+?)\s+(?:\w+\s+)?(?:Driver|Extension|Software Component|Firmware)\b.*Update", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim();
        var i = title.IndexOf(" - ", StringComparison.Ordinal);
        return i > 0 ? title[..i].Trim() : "";
    }

    // ───────────────────────── Descarga e instalación ─────────────────────────

    private static async Task<string?> GetDownloadUrlAsync(string updateId, CancellationToken ct)
    {
        var json = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]";
        var form = new Dictionary<string, string>
        {
            ["updateIDs"] = json, ["updateIDsBlockedForImport"] = "", ["wsusApiPresent"] = "",
            ["contentImport"] = "", ["sku"] = "", ["serverName"] = "", ["ssl"] = "",
            ["portNumber"] = "", ["version"] = "",
        };
        var html = await Http.PostFormAsync(BaseUrl + "DownloadDialog.aspx", form, ct);
        var m = Regex.Match(html, @"\.url\s*=\s*'([^']+)'");
        if (!m.Success) m = Regex.Match(html, @"(https?://[^'""\s]+\.(?:cab|zip))", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Replace("http://", "https://") : null;
    }

    public static async Task<InstallOutcome> InstallAsync(DriverCandidate c, IProgress<string> progress, CancellationToken ct)
    {
        if (c.CatalogUpdateId is null) return InstallOutcome.Fail("Falta el identificador del catálogo");

        progress.Report($"Obteniendo enlace de descarga: {c.Title}");
        var url = await GetDownloadUrlAsync(c.CatalogUpdateId, ct);
        if (url is null) return InstallOutcome.Fail("El catálogo no devolvió un enlace de descarga");

        var work = Path.Combine(Path.GetTempPath(), "OpenDriverUpdater", c.CatalogUpdateId);
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* se sobrescribe */ }
        Directory.CreateDirectory(work);

        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrEmpty(fileName)) fileName = "driver.cab";
        var file = Path.Combine(work, fileName);
        await Http.DownloadFileAsync(url, file, progress, $"Descargando: {c.Title}", ct);

        progress.Report($"Extrayendo: {c.Title}");
        var extractDir = Path.Combine(work, "extract");
        Directory.CreateDirectory(extractDir);
        var extracted = await ExtractAsync(file, extractDir, ct);
        if (extracted is not null) return InstallOutcome.Fail(extracted);

        var infs = Directory.GetFiles(extractDir, "*.inf", SearchOption.AllDirectories);
        if (infs.Length == 0)
        {
            // Algunos paquetes traen .cab dentro del .cab
            foreach (var inner in Directory.GetFiles(extractDir, "*.cab", SearchOption.AllDirectories))
            {
                var sub = Path.Combine(Path.GetDirectoryName(inner)!, Path.GetFileNameWithoutExtension(inner) + "_x");
                Directory.CreateDirectory(sub);
                await ExtractAsync(inner, sub, ct);
            }
            infs = Directory.GetFiles(extractDir, "*.inf", SearchOption.AllDirectories);
        }
        if (infs.Length == 0) return InstallOutcome.Fail("El paquete descargado no contiene ningún archivo .inf");

        var archError = CheckArchitecture(infs);
        if (archError is not null) return InstallOutcome.Fail(archError);

        progress.Report($"Instalando: {c.Title}");
        var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        var (code, output) = await ProcessRunner.RunAsync(
            pnputil, $"/add-driver \"{Path.Combine(extractDir, "*.inf")}\" /subdirs /install", ct);

        return code switch
        {
            0 => new InstallOutcome(InstallStatus.Succeeded, false, "Instalado correctamente"),
            3010 => new InstallOutcome(InstallStatus.Succeeded, true, "Instalado (requiere reiniciar)"),
            259 => new InstallOutcome(InstallStatus.NotApplied, false,
                "Windows añadió el driver pero no lo aplicó: no corresponde exactamente a tu dispositivo o considera mejor el actual"),
            _ => InstallOutcome.Fail($"pnputil terminó con código {code}: {LastLines(output, 3)}"),
        };
    }

    private static async Task<string?> ExtractAsync(string file, string dest, CancellationToken ct)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".zip")
        {
            ZipFile.ExtractToDirectory(file, dest, overwriteFiles: true);
            return null;
        }
        if (ext == ".cab")
        {
            var expand = Path.Combine(Environment.SystemDirectory, "expand.exe");
            var (code, output) = await ProcessRunner.RunAsync(expand, $"\"{file}\" -F:* \"{dest}\"", ct);
            return code == 0 ? null : $"No se pudo extraer el paquete (expand {code}): {LastLines(output, 2)}";
        }
        return $"Formato de paquete no soportado: {ext}";
    }

    private static string? CheckArchitecture(string[] infs)
    {
        bool amd64 = false, arm64 = false;
        foreach (var inf in infs)
        {
            string text;
            try { text = File.ReadAllText(inf).ToLowerInvariant(); } catch { continue; }
            amd64 |= text.Contains("ntamd64");
            arm64 |= text.Contains("ntarm64");
        }
        if (OsArch == Architecture.X64 && arm64 && !amd64) return "Este driver es para procesadores ARM64, no para tu PC";
        if (OsArch == Architecture.Arm64 && amd64 && !arm64) return "Este driver es para procesadores x64, no para tu PC";
        return null;
    }

    private static string LastLines(string text, int n) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(n));
}
