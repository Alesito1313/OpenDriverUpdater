namespace OpenDriverUpdater.Models;

/// <summary>Un dispositivo del PC con el driver que tiene instalado (o sin driver).</summary>
public sealed class DeviceInfo
{
    public string DeviceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public string InfName { get; init; } = "";
    public int ErrorCode { get; init; }

    /// <summary>Hardware IDs del dispositivo, del más específico al más genérico.</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CompatibleIds { get; init; } = Array.Empty<string>();

    /// <summary>IDs que se consultan en el catálogo oficial (el primero es el más específico).</summary>
    public IReadOnlyList<string> SearchIds { get; init; } = Array.Empty<string>();

    public bool HasDriver => !string.IsNullOrEmpty(Version);
    public bool IsMicrosoftDriver => Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    public bool MatchesId(string id) =>
        HardwareIds.Any(h => h.Equals(id, StringComparison.OrdinalIgnoreCase)) ||
        CompatibleIds.Any(h => h.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public enum DriverSource { WindowsUpdate, MicrosoftCatalog, Nvidia }

/// <summary>
/// Exact: el driver se encontró buscando el ID más específico del dispositivo.
/// Generic: solo se encontró buscando un ID más genérico (puede no aplicar a tu modelo exacto).
/// </summary>
public enum MatchQuality { Exact, Generic }

/// <summary>Una versión de driver encontrada en una fuente oficial.</summary>
public sealed class DriverCandidate
{
    public DriverSource Source { get; init; }
    public string Title { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public long SizeBytes { get; init; }
    public MatchQuality Match { get; init; }
    public string MatchedId { get; init; } = "";
    public string? CatalogUpdateId { get; init; }
    public string? DownloadUrl { get; init; }
    public string? InfoUrl { get; init; }

    /// <summary>Si la propia fuente ya sabe si es más nueva que la instalada (Windows Update, NVIDIA).</summary>
    public bool? NewerThanInstalled { get; init; }

    internal object? ComUpdate { get; init; }

    public string SourceName => Source switch
    {
        DriverSource.WindowsUpdate => "Windows Update",
        DriverSource.MicrosoftCatalog => "Catálogo Microsoft",
        DriverSource.Nvidia => "NVIDIA (web oficial)",
        _ => Source.ToString(),
    };
}

/// <summary>El orden importa: se usa para ordenar la lista (lo más urgente arriba).</summary>
public enum DeviceStatus { UpdateAvailable, MissingDriver, VendorDriverAvailable, UpToDate, NotFound, NotChecked }

/// <summary>Una fila de la lista: dispositivo + la mejor versión oficial encontrada.</summary>
public sealed class DeviceRow
{
    public required DeviceInfo Device { get; init; }
    public List<DriverCandidate> AllCandidates { get; } = new();
    public DriverCandidate? Latest { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.NotChecked;

    public bool CanUpdate =>
        Latest is not null &&
        Status is DeviceStatus.UpdateAvailable or DeviceStatus.MissingDriver or DeviceStatus.VendorDriverAvailable;

    /// <summary>Se marca por defecto: actualización clara del mismo fabricante y para tu modelo exacto.</summary>
    public bool Recommended =>
        CanUpdate && Status != DeviceStatus.VendorDriverAvailable && Latest!.Match == MatchQuality.Exact;

    public string StatusText
    {
        get
        {
            var text = Status switch
            {
                DeviceStatus.UpdateAvailable => "Actualización disponible",
                DeviceStatus.MissingDriver => Latest is null ? "Sin driver (no encontrado)" : "Sin driver (disponible)",
                DeviceStatus.VendorDriverAvailable => "Driver del fabricante disponible",
                DeviceStatus.UpToDate => "Al día",
                DeviceStatus.NotFound => "Sin datos en fuentes oficiales",
                DeviceStatus.NotChecked => "Driver integrado de Windows",
                _ => Status.ToString(),
            };
            if (CanUpdate && Latest!.Match == MatchQuality.Generic) text += " · coincidencia genérica";
            return text;
        }
    }
}

public enum InstallStatus { Succeeded, SucceededWithErrors, NotApplied, Failed, Aborted, Unknown }

public sealed record InstallOutcome(InstallStatus Status, bool RebootRequired, string Message)
{
    public bool Ok => Status is InstallStatus.Succeeded or InstallStatus.SucceededWithErrors;
    public static InstallOutcome Fail(string msg) => new(InstallStatus.Failed, false, msg);
}

public sealed record InstallResult(DeviceRow Row, InstallOutcome Outcome);
