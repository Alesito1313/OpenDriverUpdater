using System.Management;
using OpenDriverUpdater.Models;

namespace OpenDriverUpdater.Services;

/// <summary>Detecta los componentes del PC y los drivers que tienen instalados.</summary>
public static class DriverScanner
{
    public static List<InstalledDriver> ScanInstalledDrivers()
    {
        var drivers = new List<InstalledDriver>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceName, DeviceClass, Manufacturer, DriverProviderName, " +
            "DriverVersion, DriverDate, HardWareID, InfName FROM Win32_PnPSignedDriver");

        foreach (ManagementObject mo in searcher.Get())
        {
            using (mo)
            {
                var name = mo["DeviceName"] as string;
                if (string.IsNullOrWhiteSpace(name))
                    continue; // entradas sin dispositivo visible

                drivers.Add(new InstalledDriver
                {
                    DeviceName   = name.Trim(),
                    DeviceClass  = mo["DeviceClass"] as string ?? "",
                    Manufacturer = mo["Manufacturer"] as string ?? "",
                    Provider     = mo["DriverProviderName"] as string ?? "",
                    Version      = mo["DriverVersion"] as string ?? "",
                    Date         = ParseWmiDate(mo["DriverDate"] as string),
                    HardwareId   = mo["HardWareID"] as string ?? "",
                    InfName      = mo["InfName"] as string ?? "",
                });
            }
        }

        return drivers
            .OrderBy(d => d.DeviceClass)
            .ThenBy(d => d.DeviceName)
            .ToList();
    }

    private static DateTime? ParseWmiDate(string? cim)
    {
        if (string.IsNullOrWhiteSpace(cim)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(cim); }
        catch { return null; }
    }

    /// <summary>
    /// Empareja cada actualización con el driver instalado correspondiente
    /// usando el Hardware ID del dispositivo.
    /// </summary>
    public static void MatchUpdatesToInstalled(IEnumerable<DriverUpdate> updates, IReadOnlyList<InstalledDriver> installed)
    {
        foreach (var update in updates)
        {
            if (string.IsNullOrEmpty(update.HardwareId)) continue;

            update.Current =
                installed.FirstOrDefault(d => d.HardwareId.Equals(update.HardwareId, StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(d =>
                       d.HardwareId.Length > 0 &&
                       (d.HardwareId.StartsWith(update.HardwareId, StringComparison.OrdinalIgnoreCase) ||
                        update.HardwareId.StartsWith(d.HardwareId, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
