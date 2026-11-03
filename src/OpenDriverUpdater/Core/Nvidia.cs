using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace OpenDriverUpdater.Core
{
    /// <summary>
    /// Ultima version en la web de NVIDIA para tu grafica (el mismo servicio que usa la app de NVIDIA).
    /// NVIDIA publica aqui semanas antes que en Windows Update.
    /// </summary>
    static class Nvidia
    {
        const string LookupUrl = "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";

        public static bool IsNvidiaGpu(Device d) =>
            d.HardwareIds.Any(id => id.StartsWith("PCI\\VEN_10DE&DEV_", StringComparison.OrdinalIgnoreCase)) &&
            (d.Class.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) || d.Name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0);

        public static async Task<Candidate> Latest(Device d, CancellationToken ct)
        {
            var hw = d.HardwareIds.First(id => id.StartsWith("PCI\\VEN_10DE&DEV_", StringComparison.OrdinalIgnoreCase));
            var devId = Regex.Match(hw, @"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase).Groups[1].Value.ToUpperInvariant();

            var query = new Dictionary<string, object>
            {
                ["dIDa"] = new[] { devId + "_10DE" },
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
            var json = new JavaScriptSerializer();
            var text = await Net.GetString(LookupUrl + Uri.EscapeDataString(json.Serialize(query)), ct);

            // la respuesta trae DriverAttributes { Version, DownloadURL, ReleaseDateTime, ... }
            var info = FindWith(json.DeserializeObject(text), "DownloadURL");
            if (info == null) return null;

            var version = Str(info, "Version");
            var url = Str(info, "DownloadURL");
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(url)) return null;

            var installed = Versions.NvidiaFromWindows(d.Version);
            return new Candidate
            {
                Source = Source.Nvidia,
                Title = (Str(info, "Name") ?? "NVIDIA Driver") + " " + version,
                Vendor = "NVIDIA",
                Version = version,
                Date = ParseDate(Str(info, "ReleaseDateTime")),
                Size = ParseSize(Str(info, "DownloadURLFileSize")),
                DownloadUrl = url.Replace("http://", "https://"),
                InfoUrl = Str(info, "DetailsURL"),
                Match = MatchKind.Exact,
                MatchedId = hw,
                Newer = installed == null ? (bool?)null : Versions.Compare(version, installed) > 0,
            };
        }

        // se descarga el instalador oficial y se abre su asistente
        public static async Task<InstallResult> Install(Candidate c, IProgress<string> progress, CancellationToken ct)
        {
            if (c.DownloadUrl == null) return InstallResult.Fail(Lang.Get("nvidiaNoLink"));

            var dir = Path.Combine(Path.GetTempPath(), "OpenDriverUpdater", "nvidia");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Path.GetFileName(new Uri(c.DownloadUrl).LocalPath));
            if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file += ".exe";

            await Net.Download(c.DownloadUrl, file, progress, Lang.Get("downloading", c.Title), ct);

            progress.Report(Lang.Get("nvidiaWizard"));
            using (var p = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }))
            {
                await Net.WaitForExit(p, ct);
                return p.ExitCode == 0
                    ? new InstallResult(InstallState.Ok, Lang.Get("nvidiaDone"), reboot: true)
                    : InstallResult.Fail(Lang.Get("nvidiaExit", p.ExitCode));
            }
        }

        static bool IsDch()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\nvlddmkm"))
                    return key == null || key.GetValue("DCHUVen") != null;
            }
            catch { return true; }
        }

        static IDictionary<string, object> FindWith(object node, string key)
        {
            if (node is IDictionary<string, object> obj)
            {
                if (obj.ContainsKey(key)) return obj;
                foreach (var v in obj.Values)
                {
                    var found = FindWith(v, key);
                    if (found != null) return found;
                }
            }
            else if (node is IEnumerable list && !(node is string))
            {
                foreach (var item in list)
                {
                    var found = FindWith(item, key);
                    if (found != null) return found;
                }
            }
            return null;
        }

        static string Str(IDictionary<string, object> o, string key) =>
            o.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        static DateTime? ParseDate(string s)
        {
            // formato actual: "2026-10-21T00:00:00+0000"
            if (s != null && s.Length >= 10 &&
                DateTime.TryParseExact(s.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
            return null;
        }

        static long ParseSize(string s)
        {
            var m = Regex.Match(s ?? "", @"([\d.]+)\s*(MB|GB)", RegexOptions.IgnoreCase);
            if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
            return (long)(n * 1024 * 1024 * (m.Groups[2].Value.ToUpperInvariant() == "GB" ? 1024 : 1));
        }
    }
}
