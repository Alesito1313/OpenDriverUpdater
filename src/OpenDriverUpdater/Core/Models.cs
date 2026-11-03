using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenDriverUpdater.Core
{
    public class Device
    {
        public string Id = "";
        public string Name = "";
        public string Class = "";
        public string Manufacturer = "";
        public string Provider = "";
        public string Version = "";
        public DateTime? Date;
        public string Inf = "";
        public int Error;
        public string[] HardwareIds = new string[0];
        public string[] CompatibleIds = new string[0];
        public List<string> SearchIds = new List<string>(); // lo que se busca en el catalogo, de mas concreto a mas generico

        public bool HasDriver => Version.Length > 0;
        public bool IsMicrosoftDriver => Provider.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;

        public bool HasId(string id) =>
            HardwareIds.Any(h => h.Equals(id, StringComparison.OrdinalIgnoreCase)) ||
            CompatibleIds.Any(h => h.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public enum Source { WindowsUpdate, Catalog, Nvidia }

    // Exact = salio buscando el ID mas especifico del dispositivo.
    // Generic = solo salio con un ID mas generico, puede no ser para ese modelo exacto
    public enum MatchKind { Exact, Generic }

    public class Candidate
    {
        public Source Source;
        public string Title = "";
        public string Vendor = "";
        public string Version = "";
        public DateTime? Date;
        public long Size;
        public MatchKind Match;
        public string MatchedId = "";
        public string CatalogId;
        public string DownloadUrl;
        public string InfoUrl;
        public bool? Newer;          // si la fuente ya sabe si es mas nueva (WU, NVIDIA)
        public object WuUpdate;      // IUpdate de Windows Update

        public string SourceKey => Source == Source.Nvidia ? "nvidia" : Source == Source.WindowsUpdate ? "wu" : "catalog";
    }

    // el orden se usa para ordenar la lista
    public enum Status { Update, Missing, Vendor, UpToDate, NotFound, Builtin }

    public class Row
    {
        public Device Device;
        public List<Candidate> Candidates = new List<Candidate>();
        public Candidate Latest;
        public Status Status = Status.Builtin;

        public bool CanUpdate => Latest != null && (Status == Status.Update || Status == Status.Missing || Status == Status.Vendor);

        // lo que se marca solo: actualizacion clara y para el modelo exacto
        public bool Recommended => CanUpdate && Status != Status.Vendor && Latest.Match == MatchKind.Exact;

        public string StatusKey
        {
            get
            {
                switch (Status)
                {
                    case Status.Update: return "update";
                    case Status.Missing: return "missing";
                    case Status.Vendor: return "vendor";
                    case Status.UpToDate: return "ok";
                    case Status.NotFound: return "none";
                    default: return "builtin";
                }
            }
        }
    }

    public enum InstallState { Ok, NotApplied, Failed, Cancelled }

    public class InstallResult
    {
        public InstallState State;
        public bool Reboot;
        public string Message;

        public bool Ok => State == InstallState.Ok;

        public InstallResult(InstallState state, string message, bool reboot = false)
        {
            State = state;
            Message = message;
            Reboot = reboot;
        }

        public static InstallResult Fail(string message) => new InstallResult(InstallState.Failed, message);
    }
}
