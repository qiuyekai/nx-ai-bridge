// tools: new_part
// variant: 1
// Creates an empty millimetre part with PartCollection.NewDisplay.
using System;
using System.Collections.Generic;
using System.IO;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class NewPartTool : ITool
    {
        public static void CheckNewPath(string path)
        {
            if (!path.EndsWith(".prt", StringComparison.OrdinalIgnoreCase))
                throw new ToolError("INVALID_PARAMS", "Part file name must end with .prt");
            if (File.Exists(path))
                throw new ToolError("INVALID_PARAMS", "File already exists: " + path, "Use open_part, or choose another file name.");
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                throw new ToolError("INVALID_PARAMS", "Folder does not exist: " + dir, "Give an absolute path in an existing folder.");
        }

        public object Run(string tool, Args a)
        {
            string path = a.Str("path");
            CheckNewPath(path);
            // "Millimeters" is converted to the enum the method expects (Part.Units / BasePart.Units)
            Part part = (Part)Dyn.Call(Nx.S.Parts, "NewDisplay", path, "Millimeters");
            return R.Obj("path", part.FullPath, "units", "mm");
        }
    }
}
