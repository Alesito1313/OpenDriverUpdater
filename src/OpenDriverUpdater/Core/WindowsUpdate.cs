using System;
using System.Collections.Generic;

namespace OpenDriverUpdater.Core
{
    /// <summary>
    /// Agente de Windows Update por COM. Uso dynamic para no tener que generar
    /// los ensamblados de interop de WUApiLib.
    /// </summary>
    class WindowsUpdate
    {
        readonly dynamic session;

        public WindowsUpdate()
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type == null) throw new InvalidOperationException(Lang.Get("wuMissing"));
            session = Activator.CreateInstance(type);
            session.ClientApplicationID = "OpenDriverUpdater";
        }

        public List<Candidate> FindDrivers()
        {
            var searcher = session.CreateUpdateSearcher();
            searcher.ServerSelection = 2; // ssWindowsUpdate
            searcher.Online = true;

            var updates = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0").Updates;
            var list = new List<Candidate>();
            int count = updates.Count;
            for (int i = 0; i < count; i++)
            {
                var u = updates[i];
                string title = Try(() => (string)u.Title) ?? "?";
                string provider = Try(() => (string)u.DriverProvider) ?? "";
                list.Add(new Candidate
                {
                    Source = Source.WindowsUpdate,
                    WuUpdate = u,
                    Title = title,
                    Vendor = provider.Length > 0 ? provider : Try(() => (string)u.DriverManufacturer) ?? "",
                    Version = Versions.Extract(title),
                    Date = TryDate(() => (DateTime)u.DriverVerDate),
                    Size = TryLong(() => Convert.ToInt64((object)u.MaxDownloadSize)),
                    MatchedId = Try(() => (string)u.DriverHardwareID) ?? "",
                    Match = MatchKind.Exact,
                    Newer = true, // WU solo ofrece lo que considera mejor que lo instalado
                });
            }
            return list;
        }

        public InstallResult Install(Candidate c, IProgress<string> progress)
        {
            dynamic u = c.WuUpdate;
            if (!(bool)u.EulaAccepted) u.AcceptEula();

            dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl"));
            coll.Add(u);

            progress.Report(Lang.Get("downloading", c.Title));
            var downloader = session.CreateUpdateDownloader();
            downloader.Updates = coll;
            var dl = downloader.Download();
            int dlCode = (int)dl.ResultCode;
            if (dlCode == 4 || dlCode == 5) // failed / aborted
                return InstallResult.Fail(Lang.Get("wuDownloadError", (int)dl.HResult));

            progress.Report(Lang.Get("installing", c.Title));
            var installer = session.CreateUpdateInstaller();
            installer.Updates = coll;
            var res = installer.Install();
            bool reboot = (bool)res.RebootRequired;

            switch ((int)res.ResultCode)
            {
                case 2: return new InstallResult(InstallState.Ok, Lang.Get("installed"), reboot);
                case 3: return new InstallResult(InstallState.Ok, Lang.Get("installedWarnings"), reboot);
                default: return InstallResult.Fail(Lang.Get("wuInstallError", (int)res.HResult));
            }
        }

        public static bool RebootPending()
        {
            try
            {
                dynamic info = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo"));
                return (bool)info.RebootRequired;
            }
            catch { return false; }
        }

        static string Try(Func<string> f) { try { return f(); } catch { return null; } }
        static DateTime? TryDate(Func<DateTime> f) { try { return f(); } catch { return null; } }
        static long TryLong(Func<long> f) { try { return f(); } catch { return 0; } }
    }
}
