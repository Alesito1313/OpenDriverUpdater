using OpenDriverUpdater.Models;

namespace OpenDriverUpdater.Services;

/// <summary>
/// Busca, descarga e instala drivers usando el Agente de Windows Update (WUA).
/// Se usa COM dinámico para no depender de ensamblados de interoperabilidad.
/// </summary>
public sealed class WindowsUpdateService
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

    /// <summary>Drivers que Windows Update ofrece para este PC (incluidos los opcionales).</summary>
    public List<DriverCandidate> SearchDriverUpdates()
    {
        dynamic searcher = _session.CreateUpdateSearcher();
        searcher.ServerSelection = ServerSelectionWindowsUpdate;
        searcher.Online = true;

        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        dynamic updates = result.Updates;
        int count = updates.Count;

        var list = new List<DriverCandidate>(count);
        for (int i = 0; i < count; i++)
        {
            dynamic u = updates[i];
            string title = Safe(() => (string)u.Title) ?? "(sin título)";
            string provider = Safe(() => (string)u.DriverProvider) ?? "";
            string manufacturer = Safe(() => (string)u.DriverManufacturer) ?? "";

            list.Add(new DriverCandidate
            {
                Source = DriverSource.WindowsUpdate,
                ComUpdate = u,
                Title = title,
                Vendor = provider.Length > 0 ? provider : manufacturer,
                Version = VersionUtil.ExtractText(title),
                Date = SafeStruct(() => (DateTime)u.DriverVerDate),
                SizeBytes = SafeStruct(() => Convert.ToInt64((object)u.MaxDownloadSize)) ?? 0,
                MatchedId = Safe(() => (string)u.DriverHardwareID) ?? "",
                Match = MatchQuality.Exact,
                // Windows Update solo ofrece drivers que considera mejores que el instalado
                NewerThanInstalled = true,
            });
        }
        return list;
    }

    public InstallOutcome Install(DriverCandidate c, IProgress<string> progress)
    {
        dynamic u = c.ComUpdate ?? throw new InvalidOperationException("Actualización de Windows Update no válida");
        if (!(bool)u.EulaAccepted) u.AcceptEula();

        dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        coll.Add(u);

        progress.Report($"Descargando: {c.Title}");
        dynamic downloader = _session.CreateUpdateDownloader();
        downloader.Updates = coll;
        dynamic dl = downloader.Download();
        var dlStatus = MapResult((int)dl.ResultCode);
        if (dlStatus is InstallStatus.Failed or InstallStatus.Aborted)
            return InstallOutcome.Fail($"Error en la descarga (HRESULT 0x{(int)dl.HResult:X8})");

        progress.Report($"Instalando: {c.Title}");
        dynamic installer = _session.CreateUpdateInstaller();
        installer.Updates = coll;
        dynamic ins = installer.Install();

        var status = MapResult((int)ins.ResultCode);
        bool reboot = (bool)ins.RebootRequired;
        return status switch
        {
            InstallStatus.Succeeded => new InstallOutcome(status, reboot, "Instalado correctamente"),
            InstallStatus.SucceededWithErrors => new InstallOutcome(status, reboot, "Instalado con advertencias"),
            _ => InstallOutcome.Fail($"Error en la instalación (HRESULT 0x{(int)ins.HResult:X8})"),
        };
    }

    public static bool IsRebootPending()
    {
        try
        {
            dynamic sysInfo = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo")!)!;
            return (bool)sysInfo.RebootRequired;
        }
        catch { return false; }
    }

    private static InstallStatus MapResult(int code) => code switch
    {
        2 => InstallStatus.Succeeded,
        3 => InstallStatus.SucceededWithErrors,
        4 => InstallStatus.Failed,
        5 => InstallStatus.Aborted,
        _ => InstallStatus.Unknown,
    };

    private static T? Safe<T>(Func<T> f) where T : class
    {
        try { return f(); } catch { return null; }
    }

    private static T? SafeStruct<T>(Func<T> f) where T : struct
    {
        try { return f(); } catch { return null; }
    }
}
