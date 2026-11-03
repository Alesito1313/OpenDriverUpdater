using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDriverUpdater.Core
{
    public class CatalogEntry
    {
        public string Id, Title, Products, Classification, Version;
        public DateTime? Date;
        public long Size;
    }

    /// <summary>
    /// Catalogo de Microsoft Update (catalog.update.microsoft.com).
    /// Aqui salen todas las versiones que los fabricantes han subido para un Hardware ID,
    /// no solo la que Windows Update decide ofrecerte.
    /// </summary>
    class Catalog
    {
        const string BaseUrl = "https://www.catalog.update.microsoft.com/";
        const int MaxPages = 3; // 25 por pagina, ya vienen ordenados del mas nuevo al mas viejo

        // si hay 4 puertos USB iguales solo se busca una vez
        readonly ConcurrentDictionary<string, Lazy<Task<List<CatalogEntry>>>> cache =
            new ConcurrentDictionary<string, Lazy<Task<List<CatalogEntry>>>>(StringComparer.OrdinalIgnoreCase);

        public Task<List<CatalogEntry>> Search(string query, CancellationToken ct) =>
            cache.GetOrAdd(query, q => new Lazy<Task<List<CatalogEntry>>>(() => DoSearch(q, ct))).Value;

        static async Task<List<CatalogEntry>> DoSearch(string query, CancellationToken ct)
        {
            var url = BaseUrl + "Search.aspx?q=" + Uri.EscapeDataString(query);
            var results = new List<CatalogEntry>();
            var seen = new HashSet<string>();

            // la paginacion va por "&p=N" (lo comprobe en la web, el postback de asp.net no devuelve nada)
            for (int page = 0; page < MaxPages; page++)
            {
                var html = await Net.GetString(page == 0 ? url : url + "&p=" + page, ct);
                int found = 0, added = 0;
                foreach (var e in Parse(html))
                {
                    found++;
                    if (seen.Add(e.Id)) { results.Add(e); added++; }
                }
                if (added == 0 || found < 25 || !html.Contains("ctl00_catalogBody_nextPageLinkText")) break;
            }
            return results;
        }

        // Cada resultado es un <tr id="{guid}_R0"> y sus celdas <td id="{guid}_C1_R0">...
        // C1 titulo, C2 productos, C3 clasificacion, C4 fecha, C5 version, C6 tamaño
        static readonly Regex RowRx = new Regex(@"<tr[^>]*?\bid=""([0-9a-fA-F-]{36})_R\d+""[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex CellRx = new Regex(@"<td[^>]*?\bid=""[0-9a-fA-F-]{36}_C(\d+)_R\d+""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SizeRx = new Regex(@"_originalSize""[^>]*>\s*(\d+)\s*<", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex TagRx = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        static readonly Regex SpacesRx = new Regex(@"\s+", RegexOptions.Compiled);

        public static IEnumerable<CatalogEntry> Parse(string html)
        {
            foreach (Match row in RowRx.Matches(html))
            {
                var cells = new Dictionary<int, string>();
                foreach (Match c in CellRx.Matches(row.Groups[2].Value))
                    cells[int.Parse(c.Groups[1].Value)] = c.Groups[2].Value;

                string Cell(int i) => cells.TryGetValue(i, out var v) ? v : "";

                var title = Clean(Cell(1));
                if (title.Length == 0) continue;

                var sizeMatch = SizeRx.Match(Cell(6));
                yield return new CatalogEntry
                {
                    Id = row.Groups[1].Value.ToLowerInvariant(),
                    Title = title,
                    Products = Clean(Cell(2)),
                    Classification = Clean(Cell(3)),
                    Date = ParseDate(Clean(Cell(4))),
                    Version = Clean(Cell(5)),
                    Size = sizeMatch.Success ? long.Parse(sizeMatch.Groups[1].Value) : 0,
                };
            }
        }

        static string Clean(string html) =>
            SpacesRx.Replace(WebUtility.HtmlDecode(TagRx.Replace(html ?? "", " ")), " ").Trim();

        static DateTime? ParseDate(string s)
        {
            if (DateTime.TryParseExact(s, new[] { "M/d/yyyy", "MM/dd/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            return null;
        }

        // ¿sirve para este PC?

        static readonly int Build = Environment.OSVersion.Version.Build;
        static readonly Architecture Arch = RuntimeInformation.OSArchitecture;

        static readonly Dictionary<string, int> Win10 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["1507"] = 10240, ["1511"] = 10586, ["1607"] = 14393, ["1703"] = 15063, ["1709"] = 16299,
            ["1803"] = 17134, ["1809"] = 17763, ["1903"] = 18362, ["1909"] = 18363, ["2004"] = 19041,
            ["20H2"] = 19042, ["21H1"] = 19043, ["21H2"] = 19044, ["22H2"] = 19045,
        };
        static readonly Dictionary<string, int> Win11 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["21H2"] = 22000, ["22H2"] = 22621, ["23H2"] = 22631, ["24H2"] = 26100, ["25H2"] = 26200,
        };

        public static bool AppliesHere(CatalogEntry e)
        {
            if (e.Classification.Length > 0 && e.Classification.IndexOf("Driver", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            var title = e.Title.ToLowerInvariant();
            if (Arch == Architecture.X64 && (title.Contains("arm64") || title.Contains(" arm "))) return false;

            if (e.Products.Length == 0) return true;

            // "Windows 11 Client,  version 24H2 and later,  Servicing Drivers, Windows 10 and later drivers"
            // ojo: cada producto lleva comas dentro, asi que se corta antes de cada "Windows"
            foreach (var raw in Regex.Split(e.Products, @",\s*(?=Windows\b)", RegexOptions.IgnoreCase))
            {
                var p = SpacesRx.Replace(raw, " ").Trim().ToLowerInvariant();
                bool is11 = p.Contains("windows 11"), is10 = p.Contains("windows 10");
                if (!is10 && !is11) continue;
                if (is11 && !is10 && Build < 22000) continue;

                var v = Regex.Match(p, @"version\s+(\d{2}h\d|\d{4})");
                if (v.Success && (is11 ? Win11 : Win10).TryGetValue(v.Groups[1].Value, out var minBuild) && Build < minBuild)
                    continue;
                return true;
            }
            return false;
        }

        // "Realtek Driver Update (10.79.50.1003)" o "Realtek Semiconductor Corp. - Net - 10.68.524.2023"
        public static string VendorOf(string title)
        {
            var m = Regex.Match(title, @"^(.+?)\s+(?:\w+\s+)?(?:Driver|Extension|Software Component|Firmware)\b.*Update", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            int i = title.IndexOf(" - ", StringComparison.Ordinal);
            return i > 0 ? title.Substring(0, i).Trim() : "";
        }

        // descarga + instalacion

        static async Task<string> GetDownloadUrl(string id, CancellationToken ct)
        {
            var form = new Dictionary<string, string>
            {
                ["updateIDs"] = "[{\"size\":0,\"languages\":\"\",\"uidInfo\":\"" + id + "\",\"updateID\":\"" + id + "\"}]",
                ["updateIDsBlockedForImport"] = "", ["wsusApiPresent"] = "", ["contentImport"] = "",
                ["sku"] = "", ["serverName"] = "", ["ssl"] = "", ["portNumber"] = "", ["version"] = "",
            };
            var html = await Net.PostForm(BaseUrl + "DownloadDialog.aspx", form, ct);
            // viene como: downloadInformation[0].files[0].url = 'https://catalog.s.download.windowsupdate.com/...cab';
            var m = Regex.Match(html, @"\.url\s*=\s*'([^']+)'");
            return m.Success ? m.Groups[1].Value.Replace("http://", "https://") : null;
        }

        public static async Task<InstallResult> Install(Candidate c, IProgress<string> progress, CancellationToken ct)
        {
            progress.Report(Lang.Get("gettingLink", c.Title));
            var url = await GetDownloadUrl(c.CatalogId, ct);
            if (url == null) return InstallResult.Fail(Lang.Get("noLink"));

            var work = Path.Combine(Path.GetTempPath(), "OpenDriverUpdater", c.CatalogId);
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            Directory.CreateDirectory(work);

            var name = Path.GetFileName(new Uri(url).LocalPath);
            var file = Path.Combine(work, string.IsNullOrEmpty(name) ? "driver.cab" : name);
            await Net.Download(url, file, progress, Lang.Get("downloading", c.Title), ct);

            progress.Report(Lang.Get("extracting", c.Title));
            var dir = Path.Combine(work, "x");
            Directory.CreateDirectory(dir);
            var err = await Extract(file, dir, ct);
            if (err != null) return InstallResult.Fail(err);

            var infs = Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories);
            if (infs.Length == 0)
            {
                // algunos traen un .cab dentro del .cab
                foreach (var inner in Directory.GetFiles(dir, "*.cab", SearchOption.AllDirectories))
                {
                    var sub = inner + "_x";
                    Directory.CreateDirectory(sub);
                    await Extract(inner, sub, ct);
                }
                infs = Directory.GetFiles(dir, "*.inf", SearchOption.AllDirectories);
            }
            if (infs.Length == 0) return InstallResult.Fail(Lang.Get("noInf"));
            if (IsArmOnly(infs)) return InstallResult.Fail(Lang.Get("armOnly"));

            progress.Report(Lang.Get("installing", c.Title));
            var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
            var (code, output) = await Net.Run(pnputil, "/add-driver \"" + Path.Combine(dir, "*.inf") + "\" /subdirs /install", ct);

            switch (code)
            {
                case 0: return new InstallResult(InstallState.Ok, Lang.Get("installed"));
                case 3010: return new InstallResult(InstallState.Ok, Lang.Get("installedReboot"), reboot: true);
                case 259: return new InstallResult(InstallState.NotApplied, Lang.Get("notApplied"));
                default: return InstallResult.Fail(Lang.Get("pnputilError", code, Net.LastLines(output, 3)));
            }
        }

        static async Task<string> Extract(string file, string dest, CancellationToken ct)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".zip")
            {
                ZipFile.ExtractToDirectory(file, dest);
                return null;
            }
            if (ext != ".cab") return Lang.Get("badFormat", ext);

            var expand = Path.Combine(Environment.SystemDirectory, "expand.exe");
            var (code, output) = await Net.Run(expand, "\"" + file + "\" -F:* \"" + dest + "\"", ct);
            return code == 0 ? null : Lang.Get("extractFailed", Net.LastLines(output, 2));
        }

        static bool IsArmOnly(string[] infs)
        {
            bool amd64 = false, arm64 = false;
            foreach (var inf in infs)
            {
                string text;
                try { text = File.ReadAllText(inf).ToLowerInvariant(); } catch { continue; }
                amd64 |= text.Contains("ntamd64");
                arm64 |= text.Contains("ntarm64");
            }
            return arm64 && !amd64;
        }
    }
}
