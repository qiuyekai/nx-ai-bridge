// tools: screenshot
// variant: 1
// PNG of the NX graphics window via UF_DISP_create_image. Live mode only.
using System;
using System.Collections.Generic;
using System.IO;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class ScreenshotTool : ITool
    {
        public static string Prepare(Args a)
        {
            if (Dispatcher.Mode == "batch")
                throw new ToolError("NOT_AVAILABLE", "screenshot needs the NX window; batch mode has no graphics.", "Use get_model_info to verify the model, or run the receiver inside NX (live mode).");
            Part part = Nx.Work();
            string path = a.Str("path");
            if (!path.ToLowerInvariant().EndsWith(".png")) throw new ToolError("INVALID_PARAMS", "Output file must end with .png");
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new ToolError("INVALID_PARAMS", "Folder does not exist: " + dir);
            if (File.Exists(path)) File.Delete(path);

            string view = a.Str("view", "isometric").ToLowerInvariant();
            try
            {
                object workView = Dyn.GetPath(part, "ModelingViews.WorkView");
                if (view != "current")
                {
                    string canned = char.ToUpperInvariant(view[0]) + view.Substring(1); // Isometric, Trimetric, Top, Front, ...
                    Dyn.Call(workView, "Orient", Dyn.Enum("NXOpen.View+Canned", canned), Dyn.Enum("NXOpen.View+ScaleAdjustment", "Fit"));
                }
                if (a.Bool("fit", true)) Dyn.Call(workView, "Fit");
            }
            catch (ToolError) { throw; }
            catch (Exception) { /* a wrong view is better than no picture */ }
            try { Dyn.Call(Dyn.Get(Nx.UF, "Disp"), "Refresh"); } catch (Exception) { }
            System.Windows.Forms.Application.DoEvents(); // let NX repaint before the capture
            return path;
        }

        public static Dictionary<string, object> Describe(string path, string view, string method)
        {
            if (!File.Exists(path)) throw new ToolError("EXPORT_FAILED", "No image file was written.");
            Dictionary<string, object> d = R.Obj("path", path, "bytes", new FileInfo(path).Length, "view", view, "method", method);
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] head = new byte[24];
                if (fs.Read(head, 0, 24) == 24 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
                {
                    d["width"] = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                    d["height"] = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                }
            }
            return d;
        }

        public object Run(string tool, Args a)
        {
            string path = Prepare(a);
            // UF_DISP_create_image(file, format, background). "Png"/"White" are converted to the UFDisp enums.
            Dyn.Call(Dyn.Get(Nx.UF, "Disp"), "CreateImage", path, "Png", "White");
            if (!File.Exists(path) && File.Exists(path + ".png")) File.Move(path + ".png", path); // in case NX appends the extension
            return Describe(path, a.Str("view", "isometric"), "UF_DISP_create_image");
        }
    }
}
