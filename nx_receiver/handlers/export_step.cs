// tools: export_step
// STEP AP214 export through the STEP translator that ships with NX (StepCreator).
// UNVERIFIED: member names follow recorded NX 8-10 journals; accessed through Dyn.
using System;
using System.Collections.Generic;
using System.IO;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class ExportStepTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Part part = Nx.Work();
            string path = a.Str("path");
            string lower = path.ToLowerInvariant();
            if (!(lower.EndsWith(".stp") || lower.EndsWith(".step"))) throw new ToolError("INVALID_PARAMS", "Output file must end with .stp or .step");
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new ToolError("INVALID_PARAMS", "Folder does not exist: " + dir);
            if (File.Exists(path)) File.Delete(path);

            List<object> notes = new List<object>();
            object creator = Dyn.Call(Dyn.Get(Nx.S, "DexManager"), "CreateStepCreator");
            try
            {
                Dyn.Set(creator, "ExportAs", "Ap214");
                string baseDir = Environment.GetEnvironmentVariable("UGII_BASE_DIR");
                if (!string.IsNullOrEmpty(baseDir))
                {
                    string settings = Path.Combine(Path.Combine(baseDir, "step214ug"), "ugstep214.def");
                    if (File.Exists(settings)) Dyn.TrySet(creator, "SettingsFile", settings);
                    else notes.Add("settings file not found: " + settings);
                }
                Dyn.TrySet(creator, "ObjectTypes.Solids", true);
                Dyn.TrySet(creator, "ObjectTypes.Surfaces", true);
                Dyn.TrySet(creator, "ObjectTypes.Curves", true);
                // export what is in memory (the displayed part) rather than the file on disk
                if (!Dyn.TrySet(creator, "ExportFrom", "DisplayPart"))
                {
                    Dyn.TrySet(creator, "InputFile", part.FullPath);
                    notes.Add("ExportFrom not available: exported the last SAVED state of " + part.FullPath);
                }
                Dyn.Set(creator, "OutputFile", path);
                Dyn.TrySet(creator, "FileSaveFlag", false);
                Dyn.TrySet(creator, "LayerMask", "1-256");
                Dyn.TrySet(creator, "ProcessHoldFlag", true); // wait until the translator has finished
                Dyn.Call(creator, "Commit");
            }
            finally
            {
                Dyn.TryCall(creator, "Destroy");
            }
            // the translator is a separate process: give it a moment in case ProcessHoldFlag is ignored
            for (int i = 0; i < 300 && !File.Exists(path); i++) System.Threading.Thread.Sleep(100);
            if (!File.Exists(path))
                throw new ToolError("EXPORT_FAILED", "The STEP translator produced no file.", "Look for a .log file next to " + path + " for the translator's own error message.", notes);
            Dictionary<string, object> d = R.Obj("path", path, "bytes", new FileInfo(path).Length, "body_count", Nx.Bodies(part).Count);
            if (notes.Count > 0) d["notes"] = notes;
            return d;
        }
    }
}
