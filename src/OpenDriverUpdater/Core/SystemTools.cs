using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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

        public static bool InstallsPending()
        {
            // con 0 ms solo pregunta; WAIT_OBJECT_0 (0) = no hay nada pendiente
            try { return CMP_WaitNoPendingInstallEvents(0) != 0; }
            catch { return false; }
        }

        // Espera a que Windows no tenga instalaciones de drivers en marcha (de nosotros o de otros instaladores)
        public static async Task WaitForPendingInstalls(TimeSpan max, CancellationToken ct)
        {
            var until = DateTime.UtcNow + max;
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (!InstallsPending()) return;
                await Task.Delay(1000, ct);
            }
        }

        // Lanza un instalador y espera a que acaben el y todos los procesos que abra.
        // El de NVIDIA, por ejemplo, deja a rundll32 instalando el driver aunque su ventana ya se haya cerrado.
        public static async Task<int> RunInstaller(string file, string args, CancellationToken ct)
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }))
                {
                    if (job != IntPtr.Zero && !AssignProcessToJobObject(job, p.Handle))
                    {
                        CloseHandle(job);
                        job = IntPtr.Zero;
                    }

                    await Net.WaitForExit(p, ct);
                    int code = p.ExitCode;

                    while (job != IntPtr.Zero && ActiveProcesses(job) > 0)
                        await Task.Delay(1000, ct);
                    return code;
                }
            }
            finally
            {
                // sin KILL_ON_JOB_CLOSE: si cerramos la app el instalador sigue a lo suyo
                if (job != IntPtr.Zero) CloseHandle(job);
            }
        }

        static int ActiveProcesses(IntPtr job)
        {
            var info = new JOBOBJECT_BASIC_ACCOUNTING_INFORMATION();
            return QueryInformationJobObject(job, 1, ref info, Marshal.SizeOf(info), IntPtr.Zero) ? (int)info.ActiveProcesses : 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int length, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("cfgmgr32.dll")]
        static extern uint CMP_WaitNoPendingInstallEvents(uint timeoutMs);
    }
}
