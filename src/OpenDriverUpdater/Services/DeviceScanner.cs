using System.Management;
using System.Text.RegularExpressions;
using OpenDriverUpdater.Models;

namespace OpenDriverUpdater.Services;

/// <summary>
/// Detecta todos los dispositivos presentes en el PC (también los que no tienen driver)
/// junto con el driver instalado y la lista completa de Hardware IDs.
/// </summary>
public static partial class DeviceScanner
{
    private const int CmProblemNoDriver = 28; // CM_PROB_FAILED_INSTALL: el dispositivo no tiene driver

    private sealed record DriverData(string DeviceClass, string Manufacturer, string Provider,
                                     string Version, DateTime? Date, string InfName, string DeviceName);

    public static List<DeviceInfo> Scan()
    {
        // 1) Drivers instalados, por DeviceID
        var drivers = new Dictionary<string, DriverData>(StringComparer.OrdinalIgnoreCase);
        using (var s = new ManagementObjectSearcher(
                   "SELECT DeviceID, DeviceName, DeviceClass, Manufacturer, DriverProviderName, " +
                   "DriverVersion, DriverDate, InfName FROM Win32_PnPSignedDriver"))
        {
            foreach (ManagementObject mo in s.Get())
            {
                using (mo)
                {
                    if (Get(mo, "DeviceID") is not string id || id.Length == 0) continue;
                    drivers[id] = new DriverData(
                        Str(mo, "DeviceClass"), Str(mo, "Manufacturer"), Str(mo, "DriverProviderName"),
                        Str(mo, "DriverVersion"), ParseWmiDate(Get(mo, "DriverDate") as string),
                        Str(mo, "InfName"), Str(mo, "DeviceName"));
                }
            }
        }

        // 2) Todos los dispositivos con sus Hardware IDs
        var list = new List<DeviceInfo>();
        using var es = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity");
        foreach (ManagementObject mo in es.Get())
        {
            using (mo)
            {
                if (Get(mo, "DeviceID") is not string id || id.Length == 0) continue;
                if (Get(mo, "Present") is bool present && !present) continue; // desconectado

                int error = Get(mo, "ConfigManagerErrorCode") is { } e ? Convert.ToInt32(e) : 0;
                drivers.TryGetValue(id, out var drv);
                bool missing = error == CmProblemNoDriver;

                if (!missing && (drv is null || string.IsNullOrEmpty(drv.Version)))
                    continue; // dispositivos que no usan driver propio

                var name = Str(mo, "Name");
                if (name.Length == 0) name = drv?.DeviceName ?? "";
                if (name.Length == 0) name = id;

                var hw = Get(mo, "HardwareID") as string[] ?? Array.Empty<string>();
                var compat = Get(mo, "CompatibleID") as string[] ?? Array.Empty<string>();

                list.Add(new DeviceInfo
                {
                    DeviceId = id,
                    Name = name.Trim(),
                    DeviceClass = drv?.DeviceClass is { Length: > 0 } dc ? dc : Str(mo, "PNPClass"),
                    Manufacturer = drv?.Manufacturer is { Length: > 0 } mf ? mf : Str(mo, "Manufacturer"),
                    Provider = drv?.Provider ?? "",
                    Version = missing ? "" : drv?.Version ?? "",
                    Date = missing ? null : drv?.Date,
                    InfName = drv?.InfName ?? "",
                    ErrorCode = error,
                    HardwareIds = hw,
                    CompatibleIds = compat,
                    SearchIds = BuildSearchIds(hw.Concat(compat)),
                });
            }
        }

        return list;
    }

    [GeneratedRegex(@"&REV_[0-9A-F]+", RegexOptions.IgnoreCase)]
    private static partial Regex RevRegex();

    /// <summary>
    /// Elige qué IDs consultar en el catálogo: solo los que identifican fabricante y modelo
    /// (VEN/DEV, VID/PID, IDs ACPI de fabricante), sin la revisión, del más específico al más genérico.
    /// Los IDs genéricos de Windows (clases, ACPI\PNPxxxx...) se ignoran.
    /// </summary>
    private static List<string> BuildSearchIds(IEnumerable<string> ids)
    {
        var result = new List<string>();
        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var id = RevRegex().Replace(raw.Trim().ToUpperInvariant(), "");

            if (id.Contains("&CC_") || id.Contains("\\CC_") || id.Contains("CLASS_") || id.Contains('*'))
                continue;

            bool vendorAndModel =
                (id.Contains("VEN_") && id.Contains("DEV_")) ||
                (id.Contains("VID_") && id.Contains("PID_"));
            bool acpiVendor =
                id.StartsWith("ACPI\\") && !id.StartsWith("ACPI\\PNP") &&
                !id.StartsWith("ACPI\\VEN_PNP") && !id.StartsWith("ACPI\\VEN_ACPI") && !id.StartsWith("ACPI\\ACPI");

            if (!vendorAndModel && !acpiVendor) continue;
            if (!result.Contains(id)) result.Add(id);
            if (result.Count == 3) break;
        }
        return result;
    }

    private static object? Get(ManagementBaseObject mo, string prop)
    {
        try { return mo[prop]; }
        catch (ManagementException) { return null; } // propiedad no disponible en esta versión de Windows
    }

    private static string Str(ManagementBaseObject mo, string prop) => (Get(mo, prop) as string)?.Trim() ?? "";

    private static DateTime? ParseWmiDate(string? cim)
    {
        if (string.IsNullOrWhiteSpace(cim)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(cim); }
        catch { return null; }
    }
}
