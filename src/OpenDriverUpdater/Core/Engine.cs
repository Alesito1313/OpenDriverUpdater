using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDriverUpdater.Core
{
    public class ScanProgress
    {
        public string Phase;   // devices | sources | compare
        public string Device;  // dispositivo que se esta consultando
        public int Done, Total;
    }

    /// <summary>Lee los dispositivos, pregunta a las 3 fuentes a la vez y decide el estado de cada uno.</summary>
    class Engine
    {
        readonly Catalog catalog = new Catalog();
        WindowsUpdate wu;
        int catalogErrors;
        int catalogQueries;

        public string WuError { get; private set; }
        public string NvidiaError { get; private set; }
        public int CatalogErrors => catalogErrors;

        public async Task<List<Row>> Scan(IProgress<ScanProgress> progress, CancellationToken ct)
        {
            WuError = null;
            NvidiaError = null;
            catalogErrors = 0;

            progress.Report(new ScanProgress { Phase = "devices" });
            var devices = await Task.Run(() => DeviceScanner.Scan(), ct);
            var rows = devices.Select(d => new Row { Device = d }).ToList();

            var wuTask = Task.Run(() =>
            {
                try
                {
                    if (wu == null) wu = new WindowsUpdate();
                    return wu.FindDrivers();
                }
                catch (Exception ex)
                {
                    WuError = ex.Message;
                    return new List<Candidate>();
                }
            }, ct);

            await Task.WhenAll(wuTask, SearchCatalog(rows, progress, ct), SearchNvidia(rows, ct));
            progress.Report(new ScanProgress { Phase = "compare" });

            AddWindowsUpdate(rows, wuTask.Result);
            foreach (var r in rows) Decide(r);

            return rows.OrderBy(r => (int)r.Status).ThenBy(r => r.Device.Class).ThenBy(r => r.Device.Name).ToList();
        }

        async Task SearchCatalog(List<Row> rows, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            var targets = rows.Where(r => r.Device.SearchIds.Count > 0).ToList();
            catalogQueries = targets.Sum(r => r.Device.SearchIds.Count);
            int done = 0;
            var gate = new SemaphoreSlim(6); // mas de 6 a la vez y el catalogo empieza a fallar

            progress.Report(new ScanProgress { Phase = "sources", Total = targets.Count });

            await Task.WhenAll(targets.Select(async row =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var ids = row.Device.SearchIds;
                    for (int i = 0; i < ids.Count; i++)
                    {
                        List<CatalogEntry> entries;
                        try { entries = await catalog.Search(ids[i], ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { Interlocked.Increment(ref catalogErrors); continue; }

                        lock (row)
                        {
                            foreach (var e in entries.Where(Catalog.AppliesHere))
                            {
                                if (row.Candidates.Any(c => c.CatalogId == e.Id)) continue;
                                row.Candidates.Add(new Candidate
                                {
                                    Source = Source.Catalog,
                                    Title = e.Title,
                                    Vendor = Catalog.VendorOf(e.Title),
                                    Version = Versions.Extract(e.Version.Length > 0 ? e.Version : e.Title),
                                    Date = e.Date,
                                    Size = e.Size,
                                    CatalogId = e.Id,
                                    InfoUrl = "https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=" + e.Id,
                                    Match = i == 0 ? MatchKind.Exact : MatchKind.Generic,
                                    MatchedId = ids[i],
                                });
                            }
                        }
                    }
                }
                finally
                {
                    gate.Release();
                    int n = Interlocked.Increment(ref done);
                    progress.Report(new ScanProgress { Phase = "sources", Device = row.Device.Name, Done = n, Total = targets.Count });
                }
            }));
        }

        async Task SearchNvidia(List<Row> rows, CancellationToken ct)
        {
            foreach (var row in rows.Where(r => Nvidia.IsNvidiaGpu(r.Device)))
            {
                try
                {
                    var c = await Nvidia.Latest(row.Device, ct);
                    if (c == null) NvidiaError = Lang.Get("nvidiaNothing");
                    else lock (row) row.Candidates.Add(c);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { NvidiaError = ex.Message; }
            }
        }

        static void AddWindowsUpdate(List<Row> rows, List<Candidate> updates)
        {
            foreach (var u in updates)
            {
                var row = rows.FirstOrDefault(r => u.MatchedId.Length > 0 && r.Device.HasId(u.MatchedId));
                if (row == null)
                {
                    // WU ofrece algo para un dispositivo que no hemos emparejado; lo enseño igual
                    row = new Row
                    {
                        Device = new Device
                        {
                            Name = u.Title,
                            Version = "?",
                            HardwareIds = u.MatchedId.Length > 0 ? new[] { u.MatchedId } : new string[0],
                        },
                    };
                    rows.Add(row);
                }
                row.Candidates.Add(u);
            }
        }

        static void Decide(Row row)
        {
            var d = row.Device;
            if (row.Candidates.Count == 0)
            {
                row.Latest = null;
                row.Status = !d.HasDriver ? Status.Missing : d.SearchIds.Count == 0 ? Status.Builtin : Status.NotFound;
                return;
            }

            var newer = row.Candidates.Where(c => IsNewer(d, c)).ToList();
            if (newer.Count == 0)
            {
                row.Latest = Best(row.Candidates);
                row.Status = Status.UpToDate;
                return;
            }

            var best = Best(newer);
            row.Latest = best;
            if (!d.HasDriver) row.Status = Status.Missing;
            else if (IsVendorSwitch(d, best) && best.Source != Source.WindowsUpdate) row.Status = Status.Vendor;
            else row.Status = Status.Update;
        }

        static bool IsNewer(Device d, Candidate c)
        {
            if (!d.HasDriver) return true;
            if (c.Newer.HasValue) return c.Newer.Value;
            if (IsVendorSwitch(d, c)) return true;

            var cmp = Versions.Compare(c.Version, d.Version);
            if (cmp.HasValue) return cmp.Value > 0;

            // sin version comparable miro la fecha
            return c.Date.HasValue && d.Date.HasValue && c.Date.Value.Date > d.Date.Value.Date;
        }

        // tiene el generico de Microsoft y esto es del fabricante
        static bool IsVendorSwitch(Device d, Candidate c) =>
            d.IsMicrosoftDriver && c.Vendor.Length > 0 && c.Vendor.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0;

        // NVIDIA primero (su web siempre va por delante), luego coincidencias exactas,
        // luego version mas alta y a igualdad WU, que instala mas limpio
        static Candidate Best(IEnumerable<Candidate> candidates)
        {
            var list = candidates.ToList();
            return list.FirstOrDefault(c => c.Source == Source.Nvidia)
                ?? list.OrderBy(c => c.Match)
                       .ThenByDescending(c => Versions.Parse(c.Version) ?? new Version(0, 0))
                       .ThenByDescending(c => c.Date ?? DateTime.MinValue)
                       .ThenBy(c => c.Source == Source.WindowsUpdate ? 0 : 1)
                       .First();
        }

        public async Task<InstallResult> Install(Row row, IProgress<string> progress, CancellationToken ct)
        {
            var c = row.Latest;
            try
            {
                switch (c.Source)
                {
                    case Source.WindowsUpdate: return await Task.Run(() => wu.Install(c, progress), ct);
                    case Source.Catalog: return await Catalog.Install(c, progress, ct);
                    default: return await Nvidia.Install(c, progress, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new InstallResult(InstallState.Cancelled, Lang.Get("cancelled"));
            }
            catch (Exception ex)
            {
                return InstallResult.Fail(ex.Message);
            }
        }

        public List<string> Warnings()
        {
            var w = new List<string>();
            if (WuError != null) w.Add(Lang.Get("warnWu", WuError));
            if (catalogErrors > 0) w.Add(Lang.Get("warnCatalog", catalogErrors));
            if (NvidiaError != null) w.Add(Lang.Get("warnNvidia", NvidiaError));
            return w;
        }

        public string Report(IEnumerable<Row> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Lang.Get("rep.title") + " — " + DateTime.Now.ToString("g"));
            sb.AppendLine("OpenDriverUpdater " + Program.Version + " · Windows " + Environment.OSVersion.Version + " (" + RuntimeInformation.OSArchitecture + ")");
            foreach (var w in Warnings()) sb.AppendLine("! " + w);
            sb.AppendLine("Windows Update: " + (WuError ?? "OK") + " · Catalog: " + catalogQueries + " / " + catalogErrors + " · NVIDIA: " + (NvidiaError ?? "OK"));
            sb.AppendLine(new string('=', 80));

            foreach (var r in rows)
            {
                var d = r.Device;
                sb.AppendLine(d.Name + "  [" + d.Class + "]");
                sb.AppendLine("  " + Lang.Get("rep.status") + ": " + r.StatusKey + (r.Latest != null ? " (" + r.Latest.Match + ")" : ""));
                sb.AppendLine("  " + Lang.Get("rep.installed") + ": " + (d.HasDriver ? d.Version : Lang.Get("rep.none")) + "  " + d.Date?.ToString("d") + "  " + d.Provider + "  " + d.Inf);
                if (r.Latest != null)
                    sb.AppendLine("  " + Lang.Get("rep.latest") + ": " + r.Latest.Version + "  " + r.Latest.Date?.ToString("d") + "  " + Lang.SourceName(r.Latest.Source) + "  " + r.Latest.Title);
                sb.AppendLine("  " + Lang.Get("rep.candidates") + ": " + r.Candidates.Count);
                sb.AppendLine("  Hardware IDs: " + string.Join(" | ", d.HardwareIds));
                sb.AppendLine("  " + Lang.Get("rep.searched") + ": " + string.Join(" | ", d.SearchIds));
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
