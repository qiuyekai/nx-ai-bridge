// tools: export_stl
// Binary STL through UF_STD_open_binary_stl_file / UF_STD_put_solid_in_stl_file (available in NX 10).
// UNVERIFIED argument lists; accessed through Dyn so a mismatch reports the real signature.
using System;
using System.Collections.Generic;
using System.IO;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class ExportStlTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Part part = Nx.Work();
            string path = a.Str("path");
            if (!path.ToLowerInvariant().EndsWith(".stl")) throw new ToolError("INVALID_PARAMS", "Output file must end with .stl");
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new ToolError("INVALID_PARAMS", "Folder does not exist: " + dir);
            List<Body> bodies = new List<Body>();
            if (a.Has("bodies"))
            {
                foreach (object item in a.List("bodies")) bodies.Add(Nx.ResolveBody(item as string));
            }
            else
            {
                foreach (Body b in Nx.Bodies(part)) { bool solid = true; try { solid = b.IsSolidBody; } catch (Exception) { } if (solid) bodies.Add(b); }
            }
            if (bodies.Count == 0) throw new ToolError("NOT_FOUND", "The work part has no solid body to export.");
            double tol = a.Num("tolerance", 0.05);
            if (File.Exists(path)) File.Delete(path);

            object std = Dyn.Get(Nx.UF, "Std");
            // OpenBinaryStlFile(fileName, append, header, out handle)
            object[] open = new object[] { path, false, "nx-ai-bridge", null };
            Dyn.Call(std, "OpenBinaryStlFile", open);
            object handle = open[3];
            int facetErrors = 0;
            try
            {
                foreach (Body b in bodies)
                {
                    // PutSolidInStlFile(handle, csys, body, minEdgeLen, maxEdgeLen, facetTolerance, out numErrors, out errorInfo)
                    object[] put = new object[] { handle, Tag.Null, b.Tag, 0.0, 1000.0, tol, 0, null };
                    Dyn.Call(std, "PutSolidInStlFile", put);
                    try { facetErrors += Convert.ToInt32(put[6]); } catch (Exception) { }
                }
            }
            finally
            {
                Dyn.Call(std, "CloseStlFile", handle);
            }
            if (!File.Exists(path)) throw new ToolError("EXPORT_FAILED", "No STL file was written.");
            long bytes = new FileInfo(path).Length;
            Dictionary<string, object> d = R.Obj("path", path, "bytes", bytes, "body_count", bodies.Count, "tolerance", tol);
            if (bytes >= 84)
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] head = new byte[84];
                    fs.Read(head, 0, 84);
                    d["triangles"] = BitConverter.ToUInt32(head, 80);
                }
            }
            if (facetErrors > 0) d["facet_errors"] = facetErrors;
            return d;
        }
    }
}
