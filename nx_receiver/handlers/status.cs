// tools: nx_status
// Connection check: NX version, receiver mode, work part.
using System;
using System.Collections.Generic;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class StatusTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Session s = Nx.S;
            Dictionary<string, object> d = R.Obj(
                "connected", true,
                "mode", Dispatcher.Mode,
                "ui_mode", HostInfo.UiMode,
                "receiver_version", BridgeInfo.Receiver,
                "protocol", BridgeInfo.Protocol,
                "tcp_port", HostInfo.Port,
                "nx_version", NxVersion(s),
                "nx_process", HostInfo.NxFileVersion());
            Part part = s.Parts.Work;
            d["work_part"] = part == null ? null : part.FullPath;
            if (part != null)
            {
                try { d["modified"] = Dyn.Get(part, "IsModified"); } catch (Exception) { }
                try { d["units"] = Convert.ToString(Dyn.Get(part, "PartUnits")) == "Millimeters" ? "mm" : Convert.ToString(Dyn.Get(part, "PartUnits")); } catch (Exception) { }
                try { d["body_count"] = Nx.Bodies(part).Count; } catch (Exception) { }
                if (d.ContainsKey("units") && (string)d["units"] != "mm")
                    d["warning"] = "The work part is not a millimetre part; all tool parameters are still interpreted in part units.";
            }
            else d["modified"] = false;
            return d;
        }

        private static string NxVersion(Session s)
        {
            // UNVERIFIED: several possible sources, first one that works wins
            try
            {
                object[] args = new object[] { null };
                Dyn.Call(Dyn.Get(Nx.UF, "UF"), "GetRelease", args); // UF_get_release -> "NX V10.0.x.y"
                if (args[0] != null) return Convert.ToString(args[0]);
            }
            catch (Exception) { }
            try
            {
                string v = Dyn.Call(s, "GetEnvironmentVariableValue", "UGII_VERSION") as string;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch (Exception) { }
            return Environment.GetEnvironmentVariable("UGII_VERSION") ?? HostInfo.NxFileVersion();
        }
    }
}
