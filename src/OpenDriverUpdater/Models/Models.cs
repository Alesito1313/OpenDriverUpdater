namespace OpenDriverUpdater.Models;

/// <summary>Un driver instalado actualmente en el equipo (leído de WMI).</summary>
public sealed class InstalledDriver
{
    public string DeviceName { get; init; } = "";
    public string DeviceClass { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public string HardwareId { get; init; } = "";
    public string InfName { get; init; } = "";
}

/// <summary>Una actualización de driver disponible en Windows Update.</summary>
public sealed class DriverUpdate
{
    public string Title { get; init; } = "";
    public string DriverClass { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Provider { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string NewVersion { get; init; } = "";
    public DateTime? NewDate { get; init; }
    public decimal SizeBytes { get; init; }

    /// <summary>Driver instalado al que reemplaza (si se ha podido emparejar).</summary>
    public InstalledDriver? Current { get; set; }

    /// <summary>Objeto COM IUpdate original, necesario para descargar e instalar.</summary>
    internal object ComUpdate { get; init; } = null!;
}

public enum InstallStatus { Succeeded, SucceededWithErrors, Failed, Aborted, Unknown }

public sealed record InstallResult(DriverUpdate Update, InstallStatus Status, string Message);
