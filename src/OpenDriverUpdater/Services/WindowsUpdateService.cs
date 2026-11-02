using System.Text.RegularExpressions;
using OpenDriverUpdater.Models;

namespace OpenDriverUpdater.Services;

/// <summary>
/// Busca, descarga e instala drivers usando el Agente de Windows Update (WUA).
/// Los drivers vienen del catálogo oficial de Microsoft, donde los fabricantes
/// (Intel, AMD, NVIDIA, Realtek...) publican sus versiones certificadas (WHQL).
/// Se usa COM dinámico para no depender de ensamblados de interoperabilidad.
/// </summary>
public sealed partial class WindowsUpdateService
{
    private const int ServerSelectionWindowsUpdate = 2; // ssWindowsUpdate

    private readonly dynamic _session;

    public WindowsUpdateService()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                   ?? throw new InvalidOperationException("No se encontró el Agente de Windows Update en este sistema.");
        _session = Activator.CreateInstance(type)!;
        _session.ClientApplicationID = "OpenDriverUpdater";
    }

    /// <summary>Consulta Windows Update y devuelve los drivers con versión más nueva disponible.</summary>
    public List<DriverUpdate> SearchDriverUpdates()
    {
        dynamic searcher = _session.CreateUpdateSearcher();
        searcher.ServerSelection = ServerSelectionWindowsUpdate;
        searcher.Online = true;

        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        dynamic updates = result.Updates;
        int count = updates.Count;

        var list = new List<DriverUpdate>(count);
        for (int i = 0; i < count; i++)
        {
            dynamic u = updates[i];
            string title = Safe(() => (string)u.Title) ?? "(sin título)";

            list.Add(new DriverUpdate
            {
                ComUpdate    = u,
                Title        = title,
                DriverClass  = Safe(() => (string)u.DriverClass) ?? "",
                Manufacturer = Safe(() => (string)u.DriverManufacturer) ?? "",
                Model        = Safe(() => (string)u.DriverModel) ?? "",
                Provider     = Safe(() => (string)u.DriverProvider) ?? "",
                HardwareId   = Safe(() => (string)u.DriverHardwareID) ?? "",
                NewDate      = SafeStruct(() => (DateTime)u.DriverVerDate),
                SizeBytes    = SafeStruct(() => Convert.ToDecimal((object)u.MaxDownloadSize)) ?? 0m,
                NewVersion   = ExtractVersion(title),
            });
        }
        return list;
    }

    /// <summary>
    /// Descarga e instala los drivers indicados, uno a uno, informando del progreso.
    /// Devuelve los resultados y si hace falta reiniciar.
    /// </summary>
    public (List<InstallResult> Results, bool RebootRequired) DownloadAndInstall(
        IReadOnlyList<DriverUpdate> updates,
        IProgress<(int Done, int Total, string Message)> progress,
        CancellationToken ct)
    {
        var results = new List<InstallResult>();
        bool reboot = false;
        int total = updates.Count * 2; // descarga + instalación por driver
        int done = 0;

        foreach (var update in updates)
        {
            if (ct.IsCancellationRequested)
            {
                results.Add(new InstallResult(update, InstallStatus.Aborted, "Cancelado por el usuario"));
                continue;
            }

            try
            {
                dynamic u = update.ComUpdate;
                if (!(bool)u.EulaAccepted) u.AcceptEula();

                dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
                coll.Add(u);

                // 1) Descargar
                progress.Report((done, total, $"Descargando: {update.Title}"));
                dynamic downloader = _session.CreateUpdateDownloader();
                downloader.Updates = coll;
                dynamic dl = downloader.Download();
                done++;
                var dlStatus = MapResult((int)dl.ResultCode);
                if (dlStatus is InstallStatus.Failed or InstallStatus.Aborted)
                {
                    done++;
                    results.Add(new InstallResult(update, dlStatus, $"Error en la descarga (HRESULT 0x{(int)dl.HResult:X8})"));
                    continue;
                }

                // 2) Instalar
                progress.Report((done, total, $"Instalando: {update.Title}"));
                dynamic installer = _session.CreateUpdateInstaller();
                installer.Updates = coll;
                dynamic ins = installer.Install();
                done++;

                var status = MapResult((int)ins.ResultCode);
                if ((bool)ins.RebootRequired) reboot = true;

                string msg = status switch
                {
                    InstallStatus.Succeeded           => "Instalado correctamente",
                    InstallStatus.SucceededWithErrors => "Instalado con advertencias",
                    _ => $"Error en la instalación (HRESULT 0x{(int)ins.HResult:X8})",
                };
                results.Add(new InstallResult(update, status, msg));
            }
            catch (Exception ex)
            {
                done = (results.Count + 1) * 2; // saltar al final de este driver
                results.Add(new InstallResult(update, InstallStatus.Failed, ex.Message));
            }

            progress.Report((done, total, ""));
        }

        // Windows también puede tener un reinicio pendiente por otras causas
        try
        {
            dynamic sysInfo = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo")!)!;
            if ((bool)sysInfo.RebootRequired) reboot = true;
        }
        catch { /* no crítico */ }

        return (results, reboot);
    }

    private static InstallStatus MapResult(int code) => code switch
    {
        2 => InstallStatus.Succeeded,           // orcSucceeded
        3 => InstallStatus.SucceededWithErrors, // orcSucceededWithErrors
        4 => InstallStatus.Failed,              // orcFailed
        5 => InstallStatus.Aborted,             // orcAborted
        _ => InstallStatus.Unknown,
    };

    /// <summary>Los títulos suelen acabar en la versión: "Intel - Display - 31.0.101.5590".</summary>
    private static string ExtractVersion(string title)
    {
        var matches = VersionRegex().Matches(title);
        return matches.Count > 0 ? matches[^1].Value : "";
    }

    [GeneratedRegex(@"\d+(\.\d+){1,3}")]
    private static partial Regex VersionRegex();

    private static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); } catch { return null; }
    }

    private static T? SafeStruct<T>(Func<T> f) where T : struct
    {
        try { return f(); } catch { return null; }
    }
}
