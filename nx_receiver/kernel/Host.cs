// nx-ai-bridge kernel - entry point, transports (TCP + folder polling) and run modes.
//
// Run modes (chosen by the loader journal / command line arguments):
//   --live         (default) modeless control window + WinForms timer. Main() returns at once,
//                  NX stays fully interactive, requests are served on NX's own UI thread.
//   --modal        same, but the window is modal (fallback when NX does not pump the timer).
//   --console      blocking loop without any window (CI tests; last-resort fallback in NX).
//   --batch <dir>  run <dir>/requests.json, write <dir>/responses.json, exit (run_journal.exe).
//   --selfcheck    run the self-check and write a report (see SelfCheck.cs).
//   --compile-check  compile nxlib + all handlers and report errors.
//
// SECURITY: the TCP listener binds to 127.0.0.1 only - never to an external interface.
//
// UNVERIFIED (written without NX): that NX keeps dispatching WM_TIMER to a WinForms timer
// created by a journal after Main() returned. The self-check measures exactly this.
// C# 5 only. This file must not reference NXOpen.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NxAiBridge
{
    public static class HostInfo
    {
        public static int Port;
        public static string TcpError;
        public static string UiMode = "none";
        public static DateTime Started = DateTime.Now;

        public static string NxExePath()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; } catch (Exception) { return null; }
        }

        public static string NxFileVersion()
        {
            try
            {
                FileVersionInfo v = Process.GetCurrentProcess().MainModule.FileVersionInfo;
                return (v.ProductName ?? "") + " " + (v.ProductVersion ?? v.FileVersion ?? "");
            }
            catch (Exception) { return null; }
        }

        public static Dictionary<string, object> Describe()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["process"] = NxExePath();
            d["process_version"] = NxFileVersion();
            d["pid"] = Process.GetCurrentProcess().Id;
            d["clr"] = Environment.Version.ToString();
            d["os"] = Environment.OSVersion.ToString();
            d["is_64bit_process"] = IntPtr.Size == 8;
            d["culture"] = CultureInfo.CurrentCulture.Name;
            d["home"] = Paths.Home;
            foreach (string v in new string[] { "UGII_BASE_DIR", "UGII_ROOT_DIR", "UGII_VERSION", "UGII_LANG", "UGII_TMP_DIR" })
                d[v] = Environment.GetEnvironmentVariable(v);
            d["nxopen_assemblies"] = ScriptCompiler.NxReferences();
            return d;
        }
    }

    /// <summary>Serves requests from the TCP socket and from the request folder. Call Pump() regularly.</summary>
    public class Server
    {
        private class Client
        {
            public TcpClient Tcp;
            public MemoryStream Buffer = new MemoryStream();
            public DateTime Since = DateTime.UtcNow;
        }

        private TcpListener listener;
        private readonly List<Client> clients = new List<Client>();
        private DateTime lastBeat = DateTime.MinValue;
        private bool pumping;
        public bool Stopped;

        public void Start(int port, bool tcp)
        {
            HostInfo.Port = 0;
            HostInfo.TcpError = null;
            if (tcp)
            {
                foreach (int candidate in new int[] { port, 0 }) // 0 = any free port
                {
                    try
                    {
                        listener = new TcpListener(IPAddress.Loopback, candidate); // loopback only
                        listener.Start();
                        HostInfo.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                        HostInfo.TcpError = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        listener = null;
                        HostInfo.TcpError = ex.GetType().Name + ": " + ex.Message;
                    }
                }
            }
            else HostInfo.TcpError = "disabled";
            // drop requests left over from an earlier session
            try { foreach (string f in Directory.GetFiles(Paths.Requests)) File.Delete(f); } catch (Exception) { }
            Beat();
            Log.Write("receiver started: mode=" + Dispatcher.Mode + " tcp_port=" + HostInfo.Port + " tcp_error=" + HostInfo.TcpError);
        }

        public void Stop()
        {
            if (Stopped) return;
            Stopped = true;
            try { if (listener != null) listener.Stop(); } catch (Exception) { }
            foreach (Client c in clients) { try { c.Tcp.Close(); } catch (Exception) { } }
            clients.Clear();
            try { File.Delete(Paths.StateFile); } catch (Exception) { }
            Log.Write("receiver stopped");
        }

        /// <summary>Process everything that is pending. Never throws. Returns number of requests handled.</summary>
        public int Pump()
        {
            if (pumping || Stopped) return 0; // NX may pump messages while a tool runs: no re-entrancy
            pumping = true;
            int handled = 0;
            try
            {
                handled += PumpTcp();
                handled += PumpFiles();
                if ((DateTime.UtcNow - lastBeat).TotalSeconds >= 1.0) Beat();
            }
            catch (Exception ex) { Log.Write("pump error: " + ex); }
            finally { pumping = false; }
            return handled;
        }

        private void Beat()
        {
            lastBeat = DateTime.UtcNow;
            try
            {
                Log.WriteFileAtomic(Paths.StateFile, Json.Write(R.Obj(
                    "pid", Process.GetCurrentProcess().Id, "mode", Dispatcher.Mode, "ui_mode", HostInfo.UiMode,
                    "tcp_port", HostInfo.Port, "tcp_error", HostInfo.TcpError, "heartbeat", Log.UnixTime(),
                    "nx_version", HostInfo.NxFileVersion(), "receiver_version", BridgeInfo.Receiver, "protocol", BridgeInfo.Protocol,
                    "ok_count", Dispatcher.OkCount, "error_count", Dispatcher.ErrorCount)));
            }
            catch (Exception) { }
        }

        private int PumpTcp()
        {
            if (listener == null) return 0;
            int handled = 0;
            while (listener.Pending())
            {
                Client c = new Client();
                c.Tcp = listener.AcceptTcpClient();
                clients.Add(c);
            }
            for (int i = clients.Count - 1; i >= 0; i--)
            {
                Client c = clients[i];
                bool close = false;
                try
                {
                    NetworkStream stream = c.Tcp.GetStream();
                    byte[] chunk = new byte[65536];
                    while (c.Tcp.Available > 0)
                    {
                        int n = stream.Read(chunk, 0, chunk.Length);
                        if (n <= 0) break;
                        c.Buffer.Write(chunk, 0, n);
                    }
                    byte[] data = c.Buffer.GetBuffer();
                    int len = (int)c.Buffer.Length;
                    int nl = Array.IndexOf(data, (byte)'\n', 0, len);
                    if (nl >= 0)
                    {
                        string request = Encoding.UTF8.GetString(data, 0, nl);
                        string response = Dispatcher.Handle(request);
                        byte[] outBytes = Log.Utf8.GetBytes(response + "\n");
                        stream.Write(outBytes, 0, outBytes.Length);
                        stream.Flush();
                        handled++;
                        close = true;
                    }
                    else if ((DateTime.UtcNow - c.Since).TotalSeconds > 30) close = true;
                }
                catch (Exception ex)
                {
                    Log.Write("tcp client error: " + ex.Message);
                    close = true;
                }
                if (close)
                {
                    try { c.Tcp.Close(); } catch (Exception) { }
                    clients.RemoveAt(i);
                }
            }
            return handled;
        }

        private int PumpFiles()
        {
            int handled = 0;
            string[] files = Directory.GetFiles(Paths.Requests, "*.json");
            if (files.Length == 0) return 0;
            Array.Sort(files, delegate(string a, string b) { return File.GetCreationTimeUtc(a).CompareTo(File.GetCreationTimeUtc(b)); });
            foreach (string file in files)
            {
                string request;
                try
                {
                    request = File.ReadAllText(file, Encoding.UTF8);
                    File.Delete(file);
                }
                catch (IOException) { continue; } // still being written: next round
                string response = Dispatcher.Handle(request);
                string id = Path.GetFileNameWithoutExtension(file);
                try { Log.WriteFileAtomic(Path.Combine(Paths.Responses, id + ".json"), response); }
                catch (Exception ex) { Log.Write("cannot write response " + id + ": " + ex.Message); }
                handled++;
            }
            return handled;
        }
    }

    public static class Entry
    {
        private static bool resolverInstalled;
        public static Server Current;

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }

        private static bool Flag(string[] args, string name)
        {
            return Array.IndexOf(args, name) >= 0;
        }

        public static Dictionary<string, object> Config()
        {
            try
            {
                if (File.Exists(Paths.ConfigFile)) return Json.ParseObject(File.ReadAllText(Paths.ConfigFile));
            }
            catch (Exception ex) { Log.Write("config.json unreadable: " + ex.Message); }
            return new Dictionary<string, object>();
        }

        /// <summary>Called by the loader journal through reflection.</summary>
        public static void Run(string[] args)
        {
            if (args == null) args = new string[0];
            if (Arg(args, "--home") != null) Paths.Home = Arg(args, "--home");
            if (Arg(args, "--handlers") != null) Paths.HandlersDir = Arg(args, "--handlers");
            if (!resolverInstalled) { ScriptCompiler.InstallResolver(); resolverInstalled = true; }

            Args cfg = new Args(Config());
            int port = cfg.Int("port", 47110);
            if (Arg(args, "--port") != null) port = int.Parse(Arg(args, "--port"), CultureInfo.InvariantCulture);
            string env = Environment.GetEnvironmentVariable("NX_AI_BRIDGE_PORT");
            if (!string.IsNullOrEmpty(env) && Arg(args, "--port") == null) port = int.Parse(env, CultureInfo.InvariantCulture);
            bool tcp = !Flag(args, "--no-tcp") && cfg.Bool("tcp", true);

            if (Flag(args, "--compile-check")) { CompileCheck(); return; }
            if (Arg(args, "--batch") != null) { Batch(Arg(args, "--batch")); return; }
            if (Flag(args, "--selfcheck")) { SelfCheck.Run(Flag(args, "--no-ui"), port); return; }
            if (Flag(args, "--console"))
            {
                double timeout = Arg(args, "--timeout") == null ? 0 : double.Parse(Arg(args, "--timeout"), CultureInfo.InvariantCulture);
                ConsoleLoop(port, tcp, timeout);
                return;
            }
            string ui = cfg.Str("ui_mode", "modeless");
            if (Flag(args, "--modal")) ui = "modal";
            if (ui == "blocking") { ConsoleLoop(port, tcp, 0); return; }
            LiveUi.Start(port, tcp, ui == "modal");
        }

        private static void CompileCheck()
        {
            Dictionary<string, object> report = Dispatcher.CompileAll();
            int failures = 0;
            foreach (KeyValuePair<string, object> kv in report) if (!(kv.Value is string)) failures++;
            string text = Json.Write(R.Obj("failures", failures, "units", report));
            File.WriteAllText(Path.Combine(Paths.Logs, "compile-check.json"), text, Log.Utf8);
            Console.WriteLine(text);
            Environment.ExitCode = failures == 0 ? 0 : 1;
        }

        private static void Batch(string dir)
        {
            Dispatcher.Mode = "batch";
            List<object> responses = new List<object>();
            try
            {
                Dictionary<string, object> job = Json.ParseObject(File.ReadAllText(Path.Combine(dir, "requests.json")));
                Args a = new Args(job);
                bool stopOnError = a.Bool("stop_on_error", true);
                foreach (object item in a.List("requests"))
                {
                    Dictionary<string, object> req = item as Dictionary<string, object>;
                    if (req == null) continue;
                    Dictionary<string, object> resp = Dispatcher.Call(req.ContainsKey("tool") ? req["tool"] as string : null,
                        req.ContainsKey("params") ? req["params"] as Dictionary<string, object> : null);
                    resp["id"] = req.ContainsKey("id") ? req["id"] : null;
                    responses.Add(resp);
                    if (stopOnError && !(bool)resp["ok"]) break;
                }
            }
            catch (Exception ex)
            {
                responses.Add(R.Obj("id", null, "ok", false, "error", Dispatcher.ErrorOf(ex)));
            }
            Log.WriteFileAtomic(Path.Combine(dir, "responses.json"), Json.Write(responses));
        }

        /// <summary>Blocking loop. Ends on a "__shutdown" request, when <home>/stop exists, or after the timeout.</summary>
        public static void ConsoleLoop(int port, bool tcp, double timeoutSeconds)
        {
            Dispatcher.Mode = "console";
            HostInfo.UiMode = "blocking";
            Dispatcher.StopRequested = false;
            Server server = new Server();
            Current = server;
            server.Start(port, tcp);
            string stopFile = Path.Combine(Paths.Home, "stop");
            try { File.Delete(stopFile); } catch (Exception) { }
            Stopwatch sw = Stopwatch.StartNew();
            Console.WriteLine("nx-ai-bridge receiver listening (console mode), tcp_port=" + HostInfo.Port);
            while (!Dispatcher.StopRequested && !File.Exists(stopFile) && (timeoutSeconds <= 0 || sw.Elapsed.TotalSeconds < timeoutSeconds))
            {
                if (server.Pump() == 0) Thread.Sleep(20);
            }
            server.Stop();
        }
    }
}
