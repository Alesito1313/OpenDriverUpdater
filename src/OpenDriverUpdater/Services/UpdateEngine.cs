using System.Text;
using OpenDriverUpdater.Models;

namespace OpenDriverUpdater.Services;

public sealed record ScanProgress(string Message, int Done, int Total);

/// <summary>
/// Orquesta el análisis: lee los dispositivos, consulta las tres fuentes oficiales en paralelo,
/// compara la versión instalada con la más reciente y decide el estado de cada dispositivo.
/// </summary>
public sealed class UpdateEngine
{
    private const int CatalogConcurrency = 6;

    private readonly CatalogService _catalog = new();
    private WindowsUpdateService? _wu;
    private int _catalogErrors;

    public string? WindowsUpdateError { get; private set; }
    public string? NvidiaError { get; private set; }
    public int CatalogErrors => _catalogErrors;
    public int CatalogQueries { get; private set; }

    public async Task<List<DeviceRow>> ScanAsync(IProgress<ScanProgress> progress, CancellationToken ct)
    {
        WindowsUpdateError = null;
        NvidiaError = null;
        _catalogErrors = 0;

        progress.Report(new("Analizando los componentes del equipo…", 0, 0));
        var devices = await Task.Run(DeviceScanner.Scan, ct);
        var rows = devices.Select(d => new DeviceRow { Device = d }).ToList();

        var wuTask = Task.Run(() =>
        {
            try
            {
                _wu ??= new WindowsUpdateService();
                return _wu.SearchDriverUpdates();
            }
            catch (Exception ex)
            {
                WindowsUpdateError = ex.Message;
                return new List<DriverCandidate>();
            }
        }, ct);
        var catalogTask = SearchCatalogAsync(rows, progress, ct);
        var nvidiaTask = SearchNvidiaAsync(rows, ct);

        await Task.WhenAll(wuTask, catalogTask, nvidiaTask);
        progress.Report(new("Comparando versiones…", 1, 1));

        AttachWindowsUpdate(rows, wuTask.Result);
        foreach (var r in rows) Evaluate(r);

        return rows
            .OrderBy(r => (int)r.Status)
            .ThenBy(r => r.Device.DeviceClass)
            .ThenBy(r => r.Device.Name)
            .ToList();
    }

    // ───────────────────────── Fuentes ─────────────────────────

    private async Task SearchCatalogAsync(List<DeviceRow> rows, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        var targets = rows.Where(r => r.Device.SearchIds.Count > 0).ToList();
        CatalogQueries = targets.Sum(r => r.Device.SearchIds.Count);
        int done = 0;
        using var gate = new SemaphoreSlim(CatalogConcurrency);

        progress.Report(new($"Consultando el catálogo oficial de drivers… (0/{targets.Count})", 0, targets.Count));

        await Task.WhenAll(targets.Select(async row =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var ids = row.Device.SearchIds;
                for (int i = 0; i < ids.Count; i++)
                {
                    List<CatalogEntry> entries;
                    try
                    {
                        entries = await _catalog.SearchAsync(ids[i], ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch
                    {
                        Interlocked.Increment(ref _catalogErrors);
                        continue;
                    }

                    var match = i == 0 ? MatchQuality.Exact : MatchQuality.Generic;
                    lock (row)
                    {
                        foreach (var e in entries.Where(CatalogService.IsApplicable))
                        {
                            if (row.AllCandidates.Any(c => c.CatalogUpdateId == e.UpdateId)) continue;
                            row.AllCandidates.Add(new DriverCandidate
                            {
                                Source = DriverSource.MicrosoftCatalog,
                                Title = e.Title,
                                Vendor = CatalogService.VendorFromTitle(e.Title),
                                Version = VersionUtil.ExtractText(e.Version.Length > 0 ? e.Version : e.Title),
                                Date = e.LastUpdated,
                                SizeBytes = e.SizeBytes,
                                CatalogUpdateId = e.UpdateId,
                                InfoUrl = "https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid=" + e.UpdateId,
                                Match = match,
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
                progress.Report(new($"Consultando el catálogo oficial de drivers… ({n}/{targets.Count})", n, targets.Count));
            }
        }));
    }

    private async Task SearchNvidiaAsync(List<DeviceRow> rows, CancellationToken ct)
    {
        foreach (var row in rows.Where(r => NvidiaService.IsNvidiaGpu(r.Device)))
        {
            try
            {
                var c = await NvidiaService.GetLatestAsync(row.Device, ct);
                if (c is not null) lock (row) row.AllCandidates.Add(c);
                else NvidiaError = "NVIDIA no devolvió ningún driver para esta tarjeta";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                NvidiaError = ex.Message;
            }
        }
    }

    private static void AttachWindowsUpdate(List<DeviceRow> rows, List<DriverCandidate> updates)
    {
        foreach (var u in updates)
        {
            var row = rows.FirstOrDefault(r => u.MatchedId.Length > 0 && r.Device.MatchesId(u.MatchedId));
            if (row is null)
            {
                // Windows Update ofrece un driver para un dispositivo que no hemos emparejado: se muestra igual
                row = new DeviceRow
                {
                    Device = new DeviceInfo
                    {
                        Name = u.Title,
                        DeviceClass = "",
                        Version = "(desconocida)",
                        HardwareIds = u.MatchedId.Length > 0 ? new[] { u.MatchedId } : Array.Empty<string>(),
                    },
                };
                rows.Add(row);
            }
            row.AllCandidates.Add(u);
        }
    }

    // ───────────────────────── Comparación ─────────────────────────

    private static void Evaluate(DeviceRow row)
    {
        var d = row.Device;
        if (row.AllCandidates.Count == 0)
        {
            row.Latest = null;
            row.Status = !d.HasDriver ? DeviceStatus.MissingDriver
                       : d.SearchIds.Count == 0 ? DeviceStatus.NotChecked
                       : DeviceStatus.NotFound;
            return;
        }

        var newer = row.AllCandidates.Where(c => IsNewer(d, c)).ToList();
        if (newer.Count > 0)
        {
            var best = PickBest(newer);
            row.Latest = best;
            row.Status = !d.HasDriver ? DeviceStatus.MissingDriver
                       : IsVendorSwitch(d, best) && best.Source != DriverSource.WindowsUpdate ? DeviceStatus.VendorDriverAvailable
                       : DeviceStatus.UpdateAvailable;
        }
        else
        {
            row.Latest = PickBest(row.AllCandidates);
            row.Status = DeviceStatus.UpToDate;
        }
    }

    /// <summary>¿Es la versión de esta fuente más reciente que la instalada?</summary>
    private static bool IsNewer(DeviceInfo d, DriverCandidate c)
    {
        if (!d.HasDriver) return true;
        if (c.NewerThanInstalled is bool known) return known;
        if (IsVendorSwitch(d, c)) return true;

        var cmp = VersionUtil.Compare(c.Version, d.Version);
        if (cmp is int x) return x > 0;

        // Sin versión comparable: por fecha
        return c.Date is DateTime cd && d.Date is DateTime dd && cd.Date > dd.Date;
    }

    /// <summary>El instalado es el genérico de Microsoft y el candidato es del fabricante.</summary>
    private static bool IsVendorSwitch(DeviceInfo d, DriverCandidate c) =>
        d.IsMicrosoftDriver && c.Vendor.Length > 0 && !c.Vendor.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Elige el mejor candidato: primero la web del fabricante (NVIDIA), después coincidencias exactas,
    /// después la versión más alta; a igualdad, Windows Update (instalación más limpia).
    /// </summary>
    private static DriverCandidate PickBest(IEnumerable<DriverCandidate> candidates)
    {
        var list = candidates.ToList();
        var vendor = list.FirstOrDefault(c => c.Source == DriverSource.Nvidia);
        if (vendor is not null) return vendor;

        return list
            .OrderBy(c => c.Match)
            .ThenByDescending(c => VersionUtil.Parse(c.Version) ?? new Version(0, 0))
            .ThenByDescending(c => c.Date ?? DateTime.MinValue)
            .ThenBy(c => c.Source == DriverSource.WindowsUpdate ? 0 : 1)
            .First();
    }

    // ───────────────────────── Instalación ─────────────────────────

    public async Task<InstallOutcome> InstallAsync(DeviceRow row, IProgress<string> progress, CancellationToken ct)
    {
        var c = row.Latest ?? throw new InvalidOperationException("No hay versión que instalar");
        try
        {
            return c.Source switch
            {
                DriverSource.WindowsUpdate => await Task.Run(() => _wu!.Install(c, progress), ct),
                DriverSource.MicrosoftCatalog => await CatalogService.InstallAsync(c, progress, ct),
                DriverSource.Nvidia => await NvidiaService.InstallAsync(c, progress, ct),
                _ => InstallOutcome.Fail("Fuente desconocida"),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new InstallOutcome(InstallStatus.Aborted, false, "Cancelado");
        }
        catch (Exception ex)
        {
            return InstallOutcome.Fail(ex.Message);
        }
    }

    // ───────────────────────── Informe ─────────────────────────

    public string BuildReport(IEnumerable<DeviceRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OpenDriverUpdater — informe {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Windows Update: {WindowsUpdateError ?? "OK"}");
        sb.AppendLine($"Catálogo Microsoft: {CatalogQueries} búsquedas, {CatalogErrors} con error");
        sb.AppendLine($"NVIDIA: {NvidiaError ?? "OK / no aplica"}");
        sb.AppendLine(new string('=', 80));

        foreach (var r in rows)
        {
            var d = r.Device;
            sb.AppendLine($"{d.Name}  [{d.DeviceClass}]");
            sb.AppendLine($"  Estado:            {r.StatusText}");
            sb.AppendLine($"  Instalado:         {(d.HasDriver ? d.Version : "(ninguno)")}  {d.Date:dd/MM/yyyy}  {d.Provider}  {d.InfName}");
            if (r.Latest is { } l)
                sb.AppendLine($"  Última oficial:    {l.Version}  {l.Date:dd/MM/yyyy}  {l.SourceName}  ({l.Match})  {l.Title}");
            sb.AppendLine($"  Candidatos:        {r.AllCandidates.Count}");
            sb.AppendLine($"  Hardware IDs:      {string.Join(" | ", d.HardwareIds)}");
            sb.AppendLine($"  IDs consultados:   {string.Join(" | ", d.SearchIds)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
