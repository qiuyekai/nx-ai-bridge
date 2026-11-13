// tools: open_part, save_part, save_part_as, close_part
using System;
using System.Collections.Generic;
using System.IO;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class PartFilesTool : ITool
    {
        public object Run(string tool, Args a)
        {
            if (tool == "open_part") return Open(a.Str("path"));
            Part part = Nx.Work();
            if (tool == "save_part")
            {
                Save(part);
                return R.Obj("path", part.FullPath, "saved", true);
            }
            if (tool == "save_part_as")
            {
                string path = a.Str("path");
                if (!path.EndsWith(".prt", StringComparison.OrdinalIgnoreCase)) throw new ToolError("INVALID_PARAMS", "Part file name must end with .prt");
                if (File.Exists(path)) throw new ToolError("INVALID_PARAMS", "File already exists: " + path);
                Dispose(Dyn.Call(part, "SaveAs", path));
                return R.Obj("path", part.FullPath, "saved", true);
            }
            // close_part
            string closed = part.FullPath;
            if (a.Bool("save", false)) Save(part);
            // BasePart.Close(CloseWholeTree, CloseModified, PartCloseResponses)
            Dyn.Call(part, "Close", "True", "CloseModified", null);
            return R.Obj("closed", closed);
        }

        private static void Dispose(object status)
        {
            if (status != null) Dyn.TryCall(status, "Dispose");
        }

        private static void Save(Part part)
        {
            // BasePart.Save(SaveComponents.True, CloseAfterSave.False)
            Dispose(Dyn.Call(part, "Save", "True", "False"));
        }

        private static object Open(string path)
        {
            if (!File.Exists(path)) throw new ToolError("NOT_FOUND", "File not found: " + path);
            Session s = Nx.S;
            Part part = null;
            // already loaded in this session? then just display it
            foreach (object o in s.Parts)
            {
                Part p = o as Part;
                if (p != null && string.Equals(p.FullPath, path, StringComparison.OrdinalIgnoreCase)) { part = p; break; }
            }
            if (part != null)
            {
                // UNVERIFIED signature: SetDisplay(part, maintainWorkPart, setEntirePart, out loadStatus)
                object[] args = new object[] { part, false, false, null };
                Dyn.Call(s.Parts, "SetDisplay", args);
                Dispose(args[3]);
            }
            else
            {
                object[] args = new object[] { path, null };
                part = (Part)Dyn.Call(s.Parts, "OpenDisplay", args); // OpenDisplay(path, out PartLoadStatus)
                Dispose(args[1]);
            }
            int features = 0;
            foreach (object f in part.Features) features++;
            return R.Obj("path", part.FullPath, "units", "mm", "feature_count", features);
        }
    }
}
