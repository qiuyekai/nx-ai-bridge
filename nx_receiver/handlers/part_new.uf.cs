// tools: new_part
// variant: 2
// Fallback: UF_PART_new (units 1 = metric).
using System;
using System.Collections.Generic;
using System.IO;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class NewPartUfTool : ITool
    {
        public object Run(string tool, Args a)
        {
            string path = a.Str("path");
            if (!path.EndsWith(".prt", StringComparison.OrdinalIgnoreCase)) throw new ToolError("INVALID_PARAMS", "Part file name must end with .prt");
            if (File.Exists(path)) throw new ToolError("INVALID_PARAMS", "File already exists: " + path, "Use open_part, or choose another file name.");
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new ToolError("INVALID_PARAMS", "Folder does not exist: " + dir);
            object[] args = new object[] { path, 1, Tag.Null };
            Dyn.Call(Dyn.Get(Nx.UF, "Part"), "New", args);
            Part part = Nx.Work();
            return R.Obj("path", part.FullPath, "units", "mm");
        }
    }
}
