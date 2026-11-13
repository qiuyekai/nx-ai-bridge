// nx-ai-bridge kernel - the small control window shown inside NX while the receiver is live.
// The WinForms timer fires on NX's UI thread, which is the only thread allowed to call NX Open.
// Source files are kept pure ASCII (Chinese UI text uses \u escapes) so that no compiler
// code-page guessing can break them.
// C# 5 only. This file must not reference NXOpen.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace NxAiBridge
{
    public static class LiveUi
    {
        // statics keep the window, timer and server alive after the journal's Main() returned
        private static Form form;
        private static Timer timer;
        private static Server server;
        private static Label statusLabel;
        private static Label countLabel;
        private static int ticks;

        public static bool IsRunning
        {
            get { return form != null && !form.IsDisposed && server != null && !server.Stopped; }
        }

        public static void Start(int port, bool tcp, bool modal)
        {
            if (IsRunning)
            {
                form.Activate();
                MessageBox.Show("NX AI Bridge \u63a5\u6536\u7aef\u5df2\u5728\u8fd0\u884c\u3002\n\nThe receiver is already running.", "NX AI Bridge");
                return;
            }
            if (OtherInstanceActive())
            {
                MessageBox.Show(
                    "\u53e6\u4e00\u4e2a\u63a5\u6536\u7aef\u7a97\u53e3\u4ecd\u5728\u8fd0\u884c\uff0c\u8bf7\u5148\u5173\u95ed\u5b83\u518d\u91cd\u65b0\u8fd0\u884c Journal\u3002\n\n" +
                    "Another receiver window (older version) is still open in this NX session. Close it first, then play the journal again.",
                    "NX AI Bridge");
                return;
            }
            Dispatcher.Mode = "live";
            Dispatcher.StopRequested = false;
            HostInfo.UiMode = modal ? "modal" : "modeless";
            server = new Server();
            Entry.Current = server;
            server.Start(port, tcp);

            form = new Form();
            form.Text = "NX AI Bridge (alpha)";
            form.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            form.StartPosition = FormStartPosition.Manual;
            form.ClientSize = new Size(400, 150);
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            form.Location = new Point(area.Right - 430, area.Bottom - 210);
            form.TopMost = true;
            form.ShowInTaskbar = false;

            Label title = new Label();
            title.Text = "\u63a5\u6536\u7aef\u8fd0\u884c\u4e2d\uff0cAI \u53ef\u4ee5\u63a7\u5236 NX \u4e86  /  Receiver running";
            title.Font = new Font(title.Font, FontStyle.Bold);
            title.SetBounds(12, 10, 380, 20);
            statusLabel = new Label();
            statusLabel.SetBounds(12, 36, 380, 36);
            countLabel = new Label();
            countLabel.SetBounds(12, 76, 380, 20);

            Button stop = new Button();
            stop.Text = "\u505c\u6b62 Stop";
            stop.SetBounds(12, 108, 110, 30);
            stop.Click += delegate { form.Close(); };
            Button log = new Button();
            log.Text = "\u65e5\u5fd7 Log";
            log.SetBounds(132, 108, 110, 30);
            log.Click += delegate
            {
                try { Process.Start(Path.Combine(Paths.Logs, "receiver.log")); } catch (Exception) { }
            };
            form.Controls.AddRange(new Control[] { title, statusLabel, countLabel, stop, log });
            form.FormClosed += delegate { Shutdown(); };

            timer = new Timer();
            timer.Interval = 50;
            timer.Tick += delegate { OnTick(); };
            timer.Start();
            Refresh();
            if (modal) form.ShowDialog(); else form.Show();
        }

        private static bool OtherInstanceActive()
        {
            try
            {
                if (!File.Exists(Paths.StateFile)) return false;
                Args state = new Args(Json.ParseObject(File.ReadAllText(Paths.StateFile)));
                return state.Int("pid", -1) == Process.GetCurrentProcess().Id && Log.UnixTime() - state.Num("heartbeat", 0) < 3.0;
            }
            catch (Exception) { return false; }
        }

        private static void OnTick()
        {
            if (server == null || server.Stopped) return;
            server.Pump();
            ticks++;
            if (ticks % 10 == 0) Refresh();
            if (Dispatcher.StopRequested && form != null && !form.IsDisposed) form.Close();
        }

        private static void Refresh()
        {
            if (statusLabel == null) return;
            statusLabel.Text = (HostInfo.Port > 0 ? "TCP 127.0.0.1:" + HostInfo.Port : "TCP \u4e0d\u53ef\u7528 / unavailable (" + HostInfo.TcpError + ")")
                + "  +  \u6587\u4ef6\u5939 / folder\n" + Paths.Home;
            countLabel.Text = "\u6210\u529f OK: " + Dispatcher.OkCount + "    \u5931\u8d25 failed: " + Dispatcher.ErrorCount + "    " + Dispatcher.LastTool;
        }

        private static void Shutdown()
        {
            try { if (timer != null) { timer.Stop(); timer.Dispose(); } } catch (Exception) { }
            timer = null;
            if (server != null) server.Stop();
        }
    }
}
