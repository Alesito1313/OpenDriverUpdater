using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace OpenDriverUpdater.Core
{
    static class DeviceScanner
    {
        const int NoDriverError = 28; // CM_PROB_FAILED_INSTALL

        static readonly Regex Rev = new Regex(@"&REV_[0-9A-F]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<Device> Scan()
        {
            // drivers instalados, por DeviceID
            var drivers = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var fields = new[] { "DeviceName", "DeviceClass", "Manufacturer", "DriverProviderName", "DriverVersion", "DriverDate", "InfName" };
            using (var s = new ManagementObjectSearcher("SELECT DeviceID, " + string.Join(", ", fields) + " FROM Win32_PnPSignedDriver"))
            {
                foreach (ManagementBaseObject mo in s.Get())
                {
                    if (!(Get(mo, "DeviceID") is string id) || id.Length == 0) continue;
                    drivers[id] = fields.ToDictionary(f => f, f => Str(mo, f));
                }
            }

            var list = new List<Device>();
            using (var s = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity"))
            {
                foreach (ManagementBaseObject mo in s.Get())
                {
                    if (!(Get(mo, "DeviceID") is string id) || id.Length == 0) continue;
                    if (Get(mo, "Present") is bool present && !present) continue; // desconectado

                    int error = Get(mo, "ConfigManagerErrorCode") is object e ? Convert.ToInt32(e) : 0;
                    bool missing = error == NoDriverError;
                    drivers.TryGetValue(id, out var drv);

                    var version = drv != null ? drv["DriverVersion"] : "";
                    if (!missing && version.Length == 0) continue; // no usa driver propio

                    var hw = Get(mo, "HardwareID") as string[] ?? new string[0];
                    var compat = Get(mo, "CompatibleID") as string[] ?? new string[0];

                    var name = Str(mo, "Name");
                    if (name.Length == 0 && drv != null) name = drv["DeviceName"];
                    if (name.Length == 0) name = id;

                    var cls = drv != null ? drv["DeviceClass"] : "";
                    var maker = drv != null ? drv["Manufacturer"] : "";

                    list.Add(new Device
                    {
                        Id = id,
                        Name = name,
                        Class = cls.Length > 0 ? cls : Str(mo, "PNPClass"),
                        Manufacturer = maker.Length > 0 ? maker : Str(mo, "Manufacturer"),
                        Provider = drv != null ? drv["DriverProviderName"] : "",
                        Version = missing ? "" : version,
                        Date = missing || drv == null ? null : WmiDate(drv["DriverDate"]),
                        Inf = drv != null ? drv["InfName"] : "",
                        Error = error,
                        HardwareIds = hw,
                        CompatibleIds = compat,
                        SearchIds = PickSearchIds(hw.Concat(compat)),
                    });
                }
            }
            return list;
        }

        // Solo IDs que identifican fabricante + modelo (VEN/DEV, VID/PID, ACPI de fabricante),
        // sin la revision. Los genericos de Windows (clases, ACPI\PNPxxxx) no sirven para buscar.
        static List<string> PickSearchIds(IEnumerable<string> ids)
        {
            var result = new List<string>();
            foreach (var raw in ids)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var id = Rev.Replace(raw.Trim().ToUpperInvariant(), "");
                if (id.Contains("&CC_") || id.Contains("\\CC_") || id.Contains("CLASS_") || id.Contains("*")) continue;

                bool model = (id.Contains("VEN_") && id.Contains("DEV_")) || (id.Contains("VID_") && id.Contains("PID_"));
                bool acpi = id.StartsWith("ACPI\\") && !id.StartsWith("ACPI\\PNP") && !id.StartsWith("ACPI\\VEN_PNP")
                            && !id.StartsWith("ACPI\\VEN_ACPI") && !id.StartsWith("ACPI\\ACPI");
                if (!model && !acpi) continue;

                if (!result.Contains(id)) result.Add(id);
                if (result.Count == 3) break;
            }
            return result;
        }

        static object Get(ManagementBaseObject mo, string prop)
        {
            try { return mo[prop]; }
            catch (ManagementException) { return null; } // propiedad que no existe en esta version de Windows
        }

        static string Str(ManagementBaseObject mo, string prop) => (Get(mo, prop) as string)?.Trim() ?? "";

        static DateTime? WmiDate(string cim)
        {
            if (string.IsNullOrWhiteSpace(cim)) return null;
            try { return ManagementDateTimeConverter.ToDateTime(cim); }
            catch { return null; }
        }
    }
}
