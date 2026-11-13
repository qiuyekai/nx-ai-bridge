// tools: screenshot
// variant: 2
// Fallback without any NX API: copy the pixels of the NX main window from the screen.
// The NX window must be visible (not minimised, not covered).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class ScreenshotGdiTool : ITool
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        public object Run(string tool, Args a)
        {
            if (Dispatcher.Mode == "batch")
                throw new ToolError("NOT_AVAILABLE", "screenshot needs the NX window; batch mode has no graphics.");
            Part part = Nx.Work();
            string path = a.Str("path");
            if (!path.ToLowerInvariant().EndsWith(".png")) throw new ToolError("INVALID_PARAMS", "Output file must end with .png");
            string view = a.Str("view", "isometric").ToLowerInvariant();
            try
            {
                object workView = Dyn.GetPath(part, "ModelingViews.WorkView");
                if (view != "current")
                    Dyn.Call(workView, "Orient", Dyn.Enum("NXOpen.View+Canned", char.ToUpperInvariant(view[0]) + view.Substring(1)), Dyn.Enum("NXOpen.View+ScaleAdjustment", "Fit"));
                if (a.Bool("fit", true)) Dyn.Call(workView, "Fit");
            }
            catch (Exception) { }
            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            System.Windows.Forms.Application.DoEvents();

            IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
            RECT r;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out r) || r.Right - r.Left < 50 || r.Bottom - r.Top < 50)
                throw new ToolError("EXPORT_FAILED", "Could not find the NX main window on screen.");
            using (Bitmap bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top))
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
                if (File.Exists(path)) File.Delete(path);
                bmp.Save(path, ImageFormat.Png);
            }
            Dictionary<string, object> d = R.Obj("path", path, "bytes", new FileInfo(path).Length, "view", view,
                "width", r.Right - r.Left, "height", r.Bottom - r.Top, "method", "screen copy of the whole NX window");
            return d;
        }
    }
}
