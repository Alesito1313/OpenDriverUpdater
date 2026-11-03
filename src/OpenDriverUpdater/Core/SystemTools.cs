using System;
using System.Diagnostics;
using System.Management;

namespace OpenDriverUpdater.Core
{
    static class SystemTools
    {
        // Windows solo deja crear uno cada 24 h por defecto; si falla se sigue igual
        public static bool CreateRestorePoint(string description)
        {
            try
            {
                using (var restore = new ManagementClass(new ManagementScope(@"\\localhost\root\default"), new ManagementPath("SystemRestore"), null))
                using (var args = restore.GetMethodParameters("CreateRestorePoint"))
                {
                    args["Description"] = description;
                    args["RestorePointType"] = 10; // DEVICE_DRIVER_INSTALL
                    args["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
                    using (var result = restore.InvokeMethod("CreateRestorePoint", args, null))
                        return result != null && Convert.ToUInt32(result["ReturnValue"]) == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Restart()
        {
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false });
        }
    }
}
