using System.Diagnostics;
using System.Management;

namespace OpenDriverUpdater.Services;

public static class SystemActions
{
    /// <summary>
    /// Crea un punto de restauración antes de tocar drivers.
    /// Windows solo permite uno cada 24 h por defecto; si falla, se continúa igualmente.
    /// </summary>
    public static bool TryCreateRestorePoint(string description)
    {
        try
        {
            var scope = new ManagementScope(@"\\localhost\root\default");
            using var restore = new ManagementClass(scope, new ManagementPath("SystemRestore"), null);
            using var inParams = restore.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = 10;  // DEVICE_DRIVER_INSTALL
            inParams["EventType"] = 100;        // BEGIN_SYSTEM_CHANGE
            using var outParams = restore.InvokeMethod("CreateRestorePoint", inParams, null);
            return outParams != null && Convert.ToUInt32(outParams["ReturnValue"]) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void RestartNow()
    {
        Process.Start(new ProcessStartInfo("shutdown", "/r /t 5 /c \"OpenDriverUpdater: reiniciando para completar la actualización de drivers\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
    }
}
