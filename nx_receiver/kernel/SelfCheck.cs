// nx-ai-bridge kernel - self-check. Runs inside NX (journal NxAiBridgeSelfCheck.cs) and writes one
// JSON report that tells the developers everything they need to fix the blind-written receiver:
// environment, which transports work, whether NX stays responsive, the outcome of every tool
// (with the raw NX error), analytic volume checks, and a dump of the real NX Open API.
// The report is rewritten after every step, so even a crash leaves useful data behind.
// C# 5 only. This file must not reference NXOpen.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;

namespace NxAiBridge
{
    public static class SelfCheck
    {
        private static Dictionary<string, object> report;
        private static string reportPath;
        private static string workDir;
        private static List<object> steps;
        private static readonly Dictionary<string, object> Named = new Dictionary<string, object>();
        private static int failed;
        private static int passed;
        private static int volumeMismatches;
        // UI probe (statics keep it alive after Main returned)
        private static Form probeForm;
        private static Timer probeTimer;
        private static int probeTicks;
        private static Stopwatch probeClock;

        private static void Save()
        {
            try
            {
                report["summary"] = R.Obj("passed", passed, "failed", failed, "volume_mismatches", volumeMismatches);
                Log.WriteFileAtomic(reportPath, Json.Write(report));
            }
            catch (Exception ex) { Log.Write("selfcheck: cannot write report: " + ex.Message); }
        }

        private static Dictionary<string, object> P(params object[] pairs) { return R.Obj(pairs); }
        private static List<object> V(params double[] v) { List<object> l = new List<object>(); foreach (double d in v) l.Add(d); return l; }
        private static List<object> L(params object[] items) { return new List<object>(items); }

        // "$label.key.0" -> value from an earlier step's result
        private static object Resolve(object value)
        {
            string s = value as string;
            if (s != null && s.StartsWith("$"))
            {
                string[] parts = s.Substring(1).Split('.');
                object cur;
                if (!Named.TryGetValue(parts[0], out cur)) return null;
                for (int i = 1; i < parts.Length && cur != null; i++)
                {
                    Dictionary<string, object> d = cur as Dictionary<string, object>;
                    List<object> l = cur as List<object>;
                    int index;
                    if (d != null) { object next; cur = d.TryGetValue(parts[i], out next) ? next : null; }
                    else if (l != null && int.TryParse(parts[i], out index) && index < l.Count) cur = l[index];
                    else cur = null;
                }
                return cur;
            }
            Dictionary<string, object> map = value as Dictionary<string, object>;
            if (map != null)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in map) copy[kv.Key] = Resolve(kv.Value);
                return copy;
            }
            List<object> list = value as List<object>;
            if (list != null)
            {
                List<object> copy = new List<object>();
                foreach (object item in list) copy.Add(Resolve(item));
                return copy;
            }
            return value;
        }

        private static double NumberOf(object v)
        {
            if (v is double) return (double)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            return double.NaN;
        }

        /// <summary>Run one tool, record everything. expectedVolume = NaN when unknown.</summary>
        private static Dictionary<string, object> Step(string label, string tool, Dictionary<string, object> parameters, double expectedVolume)
        {
            Dictionary<string, object> p = (Dictionary<string, object>)Resolve(parameters ?? new Dictionary<string, object>());
            Dictionary<string, object> entry = R.Obj("label", label, "tool", tool, "params", p);
            Dictionary<string, object> resp;
            try { resp = Dispatcher.Call(tool, p); }
            catch (Exception ex) { resp = R.Obj("ok", false, "error", Dispatcher.ErrorOf(ex)); }
            // round-trip through JSON so the stored result has plain types only
            try { resp = Json.ParseObject(Json.Write(resp)); } catch (Exception) { }
            bool ok = resp.ContainsKey("ok") && resp["ok"] is bool && (bool)resp["ok"];
            entry["ok"] = ok;
            if (resp.ContainsKey("meta")) entry["meta"] = resp["meta"];
            Dictionary<string, object> result = null;
            if (ok)
            {
                passed++;
                result = resp["result"] as Dictionary<string, object>;
                entry["result"] = resp["result"];
                if (label != null && result != null) Named[label] = result;
                if (!double.IsNaN(expectedVolume))
                {
                    double actual = double.NaN;
                    if (result != null && result.ContainsKey("volume")) actual = NumberOf(result["volume"]);
                    else if (result != null && result.ContainsKey("total_volume")) actual = NumberOf(result["total_volume"]);
                    bool vok = !double.IsNaN(actual) && Math.Abs(actual - expectedVolume) <= Math.Max(0.01, Math.Abs(expectedVolume) * 0.001);
                    entry["expected_volume"] = Math.Round(expectedVolume, 4);
                    entry["volume"] = double.IsNaN(actual) ? null : (object)actual;
                    entry["volume_ok"] = vok;
                    if (!vok) volumeMismatches++;
                }
            }
            else
            {
                failed++;
                entry["error"] = resp.ContainsKey("error") ? resp["error"] : null;
            }
            steps.Add(entry);
            Save();
            return ok ? (result ?? new Dictionary<string, object>()) : null;
        }

        private static Dictionary<string, object> Step(string label, string tool, Dictionary<string, object> parameters)
        {
            return Step(label, tool, parameters, double.NaN);
        }

        private static void Note(string key, object value)
        {
            report[key] = value;
            Save();
        }

        // ------------------------------------------------------------------ main
        public static void Run(bool noUi, int port)
        {
            Dispatcher.Mode = "selfcheck";
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            reportPath = Path.Combine(Paths.Reports, "nx-ai-bridge-selfcheck-" + stamp + (noUi ? "-batch" : "") + ".json");
            workDir = Paths.Ensure(Path.Combine(Path.Combine(Paths.Home, "selfcheck"), stamp + (noUi ? "-batch" : "")));
            report = new Dictionary<string, object>();
            steps = new List<object>();
            Named.Clear();
            failed = passed = volumeMismatches = 0;
            report["report_version"] = 1;
            report["receiver_version"] = BridgeInfo.Receiver;
            report["generated"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
            report["ui"] = noUi ? "none (batch)" : "interactive";
            report["environment"] = HostInfo.Describe();
            try
            {
                string envReport = Path.Combine(Paths.Reports, "env-report.json");
                if (File.Exists(envReport)) report["env_report"] = Json.Parse(File.ReadAllText(envReport));
            }
            catch (Exception ex) { report["env_report"] = "unreadable: " + ex.Message; }
            if (!noUi)
            {
                // embed the newest batch-mode report (written by selfcheck.ps1 through run_journal.exe),
                // so that the user only has to bring back ONE file
                try
                {
                    string[] batch = Directory.GetFiles(Paths.Reports, "nx-ai-bridge-selfcheck-*-batch.json");
                    if (batch.Length > 0)
                    {
                        Array.Sort(batch, StringComparer.OrdinalIgnoreCase);
                        Dictionary<string, object> b = Json.ParseObject(File.ReadAllText(batch[batch.Length - 1]));
                        b.Remove("api_dump"); // identical to ours
                        report["batch_report"] = b;
                    }
                }
                catch (Exception ex) { report["batch_report"] = "unreadable: " + ex.Message; }
            }
            Save();

            Note("compile", Dispatcher.CompileAll());
            Note("transport", TransportChecks(port));
            report["steps"] = steps;
            try { ToolChecks(noUi); }
            catch (Exception ex) { Note("tool_checks_crashed", Dispatcher.ErrorOf(ex)); }
            try { Bracket(); }
            catch (Exception ex) { Note("bracket_crashed", Dispatcher.ErrorOf(ex)); }
            Note("api_dump", ApiDump());
            report["finished"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
            Save();
            CopyToDesktop();
            if (noUi) { Console.WriteLine("selfcheck report: " + reportPath); return; }
            StartUiProbe();
        }

        // ------------------------------------------------------------------ transports
        private static Dictionary<string, object> TransportChecks(int port)
        {
            Dictionary<string, object> t = new Dictionary<string, object>();
            // 1. raw sockets inside the journal
            try
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int p = ((IPEndPoint)l.LocalEndpoint).Port;
                TcpClient c = new TcpClient();
                c.Connect(IPAddress.Loopback, p);
                TcpClient s = l.AcceptTcpClient();
                byte[] msg = Encoding.ASCII.GetBytes("ping\n");
                c.GetStream().Write(msg, 0, msg.Length);
                byte[] buf = new byte[16];
                int n = s.GetStream().Read(buf, 0, buf.Length);
                c.Close(); s.Close(); l.Stop();
                t["sockets"] = R.Obj("ok", n == msg.Length, "port_used", p);
            }
            catch (Exception ex) { t["sockets"] = R.Obj("ok", false, "error", ex.GetType().FullName + ": " + ex.Message); }
            // 2. can the configured port be bound?
            try
            {
                TcpListener l = new TcpListener(IPAddress.Loopback, port);
                l.Start(); l.Stop();
                t["configured_port"] = R.Obj("port", port, "free", true);
            }
            catch (Exception ex) { t["configured_port"] = R.Obj("port", port, "free", false, "error", ex.Message); }
            // 3. full request through the real server code, TCP and file
            Server server = new Server();
            try
            {
                server.Start(0, true);
                if (HostInfo.Port > 0)
                {
                    TcpClient c = new TcpClient();
                    c.Connect(IPAddress.Loopback, HostInfo.Port);
                    byte[] req = Encoding.UTF8.GetBytes("{\"id\":\"sc-tcp\",\"tool\":\"__ping\",\"params\":{}}\n");
                    c.GetStream().Write(req, 0, req.Length);
                    Stopwatch sw = Stopwatch.StartNew();
                    while (c.Available == 0 && sw.ElapsedMilliseconds < 5000) { server.Pump(); System.Threading.Thread.Sleep(10); }
                    byte[] buf = new byte[4096];
                    int n = c.Available > 0 ? c.GetStream().Read(buf, 0, buf.Length) : 0;
                    string text = Encoding.UTF8.GetString(buf, 0, n);
                    c.Close();
                    t["tcp_roundtrip"] = R.Obj("ok", text.Contains("\"pong\""), "ms", sw.ElapsedMilliseconds);
                }
                else t["tcp_roundtrip"] = R.Obj("ok", false, "error", HostInfo.TcpError);
                string reqFile = Path.Combine(Paths.Requests, "sc-file.json");
                string respFile = Path.Combine(Paths.Responses, "sc-file.json");
                if (File.Exists(respFile)) File.Delete(respFile);
                Log.WriteFileAtomic(reqFile, "{\"id\":\"sc-file\",\"tool\":\"__ping\",\"params\":{}}");
                server.Pump();
                bool fileOk = File.Exists(respFile) && File.ReadAllText(respFile).Contains("\"pong\"");
                try { File.Delete(respFile); } catch (Exception) { }
                t["file_roundtrip"] = R.Obj("ok", fileOk);
            }
            catch (Exception ex) { t["server_error"] = ex.GetType().FullName + ": " + ex.Message; }
            finally { server.Stop(); }
            return t;
        }

        // ------------------------------------------------------------------ every tool once
        private static string F(string name) { return Path.Combine(workDir, name); }

        private static void ToolChecks(bool noUi)
        {
            double pi = Math.PI;
            Step("status", "nx_status", null);
            if (Step("new", "new_part", P("path", F("sc_tools.prt"))) == null) return; // nothing else can work

            // primitives
            Step("block", "create_block", P("length", 100.0, "width", 60.0, "height", 20.0), 120000);
            Step("cyl", "create_cylinder", P("diameter", 20.0, "height", 30.0, "origin", V(200, 0, 0)), pi * 100 * 30);
            Step("cone", "create_cone", P("base_diameter", 30.0, "top_diameter", 10.0, "height", 25.0, "origin", V(300, 0, 0)), pi * 25 / 12 * (900 + 300 + 100));
            Step("sphere", "create_sphere", P("diameter", 40.0, "center", V(400, 0, 0)), pi * 64000 / 6);
            Step("info1", "get_model_info", null, 120000 + pi * 3000 + pi * 25 / 12 * 1300 + pi * 64000 / 6);
            Step("features1", "list_features", null);
            Step("edges", "list_edges", P("body", "$block.body"));
            Step("faces", "list_faces", P("body", "$block.body"));

            // detail features on the block
            double v = 120000 - (1 - pi / 4) * 9 * 20;
            Step("blend", "edge_blend", P("radius", 3.0, "edges", L(P("near", V(0, 0, 10), "body", "$block.body"))), v);
            v -= 0.5 * 4 * 60;
            Step("chamfer", "chamfer", P("distance", 2.0, "edges", L(P("near", V(100, 30, 20), "body", "$block.body"))), v);
            v -= pi * 25 * 20;
            Step("hole", "create_hole", P("position", V(50, 30, 20), "direction", V(0, 0, -1), "diameter", 10.0, "through", true, "body", "$block.body"), v);
            Step("blind_hole", "create_hole", P("position", V(20, 30, 20), "direction", V(0, 0, -1), "diameter", 6.0, "depth", 5.0, "body", "$block.body"), v - pi * 9 * 5);

            // shell
            Step("shellblock", "create_block", P("length", 40.0, "width", 40.0, "height", 40.0, "origin", V(0, 200, 0)), 64000);
            Step("shell", "shell", P("thickness", 2.0, "body", "$shellblock.body", "remove_faces", L(P("normal", V(0, 0, 1), "body", "$shellblock.body"))), 64000 - 36.0 * 36 * 38);

            // booleans
            Step("boxA", "create_block", P("length", 20.0, "width", 20.0, "height", 20.0, "origin", V(0, 300, 0)), 8000);
            Step("boxB", "create_block", P("length", 10.0, "width", 10.0, "height", 10.0, "origin", V(15, 315, 15)), 1000);
            Step("subtract", "boolean_subtract", P("target", "$boxA.body", "tools", L("$boxB.body")), 7875);
            Step("boxC", "create_block", P("length", 10.0, "width", 10.0, "height", 10.0, "origin", V(20, 300, 0)), 1000);
            Step("unite", "boolean_unite", P("target", "$boxA.body", "tools", L("$boxC.body")), 8875);
            Step("boxD", "create_block", P("length", 20.0, "width", 20.0, "height", 20.0, "origin", V(0, 400, 0)), 8000);
            Step("boxE", "create_block", P("length", 20.0, "width", 20.0, "height", 20.0, "origin", V(10, 410, 10)), 8000);
            Step("intersect", "boolean_intersect", P("target", "$boxD.body", "tools", L("$boxE.body")), 1000);
            Step("cyl_bool", "create_cylinder", P("diameter", 8.0, "height", 30.0, "origin", V(5, 405, -5), "boolean", "none"), pi * 16 * 30);

            // sketches
            Step("sk1", "create_sketch", P("plane", "XY", "origin", V(0, 500, 0)));
            Step("sk1_rect", "sketch_rectangle", P("sketch", "$sk1.sketch", "corner1", V(0, 0), "corner2", V(30, 20)));
            Step("sk1_circle", "sketch_circle", P("sketch", "$sk1.sketch", "center", V(15, 10), "radius", 5.0));
            Step("extrude", "extrude", P("sketch", "$sk1.sketch", "distance", 10.0), (600 - 25 * pi) * 10);
            Step("sk2", "create_sketch", P("plane", "XZ", "origin", V(0, 600, 0)));
            Step("sk2_circle", "sketch_circle", P("sketch", "$sk2.sketch", "center", V(20, 0), "radius", 3.0));
            Step("revolve", "revolve", P("sketch", "$sk2.sketch", "axis_point", V(0, 600, 0), "axis_direction", V(0, 0, 1)), 2 * pi * pi * 20 * 9);
            Step("sk3", "create_sketch", P("plane", "YZ", "origin", V(0, 700, 0)));
            Step("sk3_arc", "sketch_arc", P("sketch", "$sk3.sketch", "center", V(0, 0), "radius", 10.0, "start_angle", 0.0, "end_angle", 180.0));
            Step("sk3_line", "sketch_line", P("sketch", "$sk3.sketch", "start", V(-10, 0), "end", V(10, 0)));
            Step("extrude_arc", "extrude", P("sketch", "$sk3.sketch", "distance", 5.0), pi * 100 / 2 * 5);
            Step("sk4", "create_sketch", P("face", P("normal", V(0, 0, 1), "body", "$boxD.body")));

            // expressions, undo, delete
            Dictionary<string, object> exprs = Step("exprs", "list_expressions", null);
            string heightExpr = null;
            object cylExprs = Resolve("$cyl.expressions");
            if (exprs != null && cylExprs is List<object> && exprs.ContainsKey("expressions"))
            {
                foreach (object item in (List<object>)exprs["expressions"])
                {
                    Dictionary<string, object> e = item as Dictionary<string, object>;
                    if (e == null || !e.ContainsKey("name") || !e.ContainsKey("value")) continue;
                    if (((List<object>)cylExprs).Contains(e["name"]) && Math.Abs(NumberOf(e["value"]) - 30.0) < 1e-9) heightExpr = (string)e["name"];
                }
            }
            if (heightExpr != null)
            {
                Step("set_expr", "set_expression", P("name", heightExpr, "value", 40.0));
                Step("cyl_after_set", "list_edges", P("body", "$cyl.body"));
                Step("info_after_set", "get_model_info", P("include_features", false));
                Step("undo", "undo", P("steps", 1));
                Step("info_after_undo", "get_model_info", P("include_features", false));
            }
            else Note("set_expression_skipped", "could not identify the height expression of the cylinder from $cyl.expressions");
            Step("delete", "delete_feature", P("feature", "$sphere.feature"));
            Step("info2", "get_model_info", null);

            // files
            Step("save", "save_part", null);
            Step("step", "export_step", P("path", F("sc_tools.stp")));
            Step("stl", "export_stl", P("path", F("sc_tools.stl")));
            if (!noUi)
            {
                Step("shot_iso", "screenshot", P("path", F("sc_iso.png"), "view", "isometric"));
                Step("shot_top", "screenshot", P("path", F("sc_top.png"), "view", "top"));
            }
            Step("save_as", "save_part_as", P("path", F("sc_tools_copy.prt")));

            // escape hatch
            Step("journal_vb", "run_journal", P("language", "vb", "code",
                "Imports System\nImports NXOpen\nModule SelfCheckJournal\n  Function Main(ByVal args() As String) As String\n" +
                "    Dim s As Session = Session.GetSession()\n    Console.WriteLine(\"hello from VB\")\n    Return \"work part: \" & s.Parts.Work.FullPath\n  End Function\nEnd Module\n"));
            Step("journal_cs", "run_journal", P("language", "csharp", "code",
                "using System;\nusing NXOpen;\npublic class SelfCheckJournal {\n  public static string Main(string[] args) {\n" +
                "    return \"work part: \" + Session.GetSession().Parts.Work.FullPath;\n  }\n}\n"));
            Step("journal_bad", "run_journal", P("language", "csharp", "code", "public class Broken { public static void Main() { int x = ; } }"));
            Step("api", "nx_api_lookup", P("type", "NXOpen.Features.ExtrudeBuilder", "limit", 20));

            Step("close", "close_part", P("save", false));
            Step("reopen", "open_part", P("path", F("sc_tools.prt")));
            Step("info3", "get_model_info", P("include_features", false));
            Step("close2", "close_part", P("save", false));
        }

        // ------------------------------------------------------------------ the reference bracket (same as demo.py)
        private static void Bracket()
        {
            double pi = Math.PI;
            double solid = 80.0 * 40 * 10 + 10.0 * 40 * 50;
            double holes = solid - pi * 25 * 10 - pi * 16 * 10;
            double chamfer = holes - 0.5 * 4 * 40;
            double final = chamfer - (1 - pi / 4) * 9 * 40;
            if (Step("br_new", "new_part", P("path", F("sc_bracket.prt"))) == null) return;
            Step("br_base", "create_block", P("length", 80.0, "width", 40.0, "height", 10.0), 32000);
            Step("br_upright", "create_block", P("length", 10.0, "width", 40.0, "height", 50.0, "origin", V(0, 0, 10), "boolean", "unite"), solid);
            Step("br_hole1", "create_hole", P("position", V(55, 20, 10), "direction", V(0, 0, -1), "diameter", 10.0, "through", true));
            Step("br_hole2", "create_hole", P("position", V(10, 20, 40), "direction", V(-1, 0, 0), "diameter", 8.0, "through", true), holes);
            Step("br_chamfer", "chamfer", P("distance", 2.0, "edges", L(P("near", V(80, 20, 10)))), chamfer);
            Step("br_blend", "edge_blend", P("radius", 3.0, "edges", L(P("near", V(0, 20, 60)))), final);
            Dictionary<string, object> info = Step("br_info", "get_model_info", null, final);
            Dictionary<string, object> summary = R.Obj("expected_volume", Math.Round(final, 4), "expected_bbox", R.Nums(0, 0, 0, 80, 40, 60));
            if (info != null)
            {
                summary["volume"] = info.ContainsKey("total_volume") ? info["total_volume"] : null;
                summary["bbox"] = info.ContainsKey("bbox") ? info["bbox"] : null;
            }
            Step("br_save", "save_part", null);
            Step("br_close", "close_part", P("save", false));
            Note("bracket", summary);
        }

        // ------------------------------------------------------------------ API dump
        private static Dictionary<string, object> ApiDump()
        {
            Dictionary<string, object> dump = new Dictionary<string, object>();
            string[] types = new string[] {
                "NXOpen.Session", "NXOpen.PartCollection", "NXOpen.BasePart", "NXOpen.Part", "NXOpen.Body", "NXOpen.Edge", "NXOpen.Face",
                "NXOpen.NXObject", "NXOpen.Update", "NXOpen.Features.FeatureCollection", "NXOpen.Features.Feature",
                "NXOpen.Features.BlockFeatureBuilder", "NXOpen.Features.CylinderBuilder", "NXOpen.Features.ConeBuilder",
                "NXOpen.Features.SphereBuilder", "NXOpen.Features.ExtrudeBuilder", "NXOpen.Features.RevolveBuilder",
                "NXOpen.Features.EdgeBlendBuilder", "NXOpen.Features.ChamferBuilder", "NXOpen.Features.ShellBuilder",
                "NXOpen.Features.BooleanBuilder", "NXOpen.Features.HolePackageBuilder", "NXOpen.Features.DeleteBodyBuilder",
                "NXOpen.GeometricUtilities.BooleanOperation", "NXOpen.GeometricUtilities.Limits", "NXOpen.GeometricUtilities.Extend",
                "NXOpen.Section", "NXOpen.SectionCollection", "NXOpen.ScRuleFactory", "NXOpen.ScCollector", "NXOpen.ScCollectorCollection",
                "NXOpen.Sketch", "NXOpen.SketchCollection", "NXOpen.SketchInPlaceBuilder", "NXOpen.CurveCollection",
                "NXOpen.AxisCollection", "NXOpen.DirectionCollection", "NXOpen.PointCollection", "NXOpen.NXMatrixCollection",
                "NXOpen.ExpressionCollection", "NXOpen.Expression", "NXOpen.MeasureManager", "NXOpen.MeasureBodies",
                "NXOpen.View", "NXOpen.ModelingView", "NXOpen.ModelingViewCollection", "NXOpen.ViewCollection",
                "NXOpen.DexManager", "NXOpen.StepCreator", "NXOpen.ObjectTypes", "NXOpen.ListingWindow", "NXOpen.UI",
                "NXOpen.UF.UFSession", "NXOpen.UF.UFSket", "NXOpen.UF.UFDisp", "NXOpen.UF.UFStd", "NXOpen.UF.UFPart", "NXOpen.UF.UFUf",
                "NXOpen.UF.UFCurve", "NXOpen.UF.UFObj", "NXOpen.UF.UFModl"
            };
            foreach (string name in types)
            {
                try { dump[name] = Dyn.Members(Dyn.FindType(name), null, 3000); }
                catch (Exception ex) { dump[name] = "MISSING: " + ex.Message; }
            }
            return dump;
        }

        private static void CopyToDesktop()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
                {
                    string dest = Path.Combine(desktop, Path.GetFileName(reportPath));
                    File.Copy(reportPath, dest, true);
                    report["desktop_copy"] = dest;
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ does NX stay responsive?
        private static void UiNote(string answer)
        {
            report["ui_probe"] = R.Obj(
                "modeless_timer_ticks", probeTicks,
                "seconds", probeClock == null ? 0 : Math.Round(probeClock.Elapsed.TotalSeconds, 1),
                "modeless_ok", probeTicks > 5,
                "user_could_rotate_model", answer);
            Save();
            CopyToDesktop();
        }

        private static void StartUiProbe()
        {
            probeTicks = 0;
            probeClock = Stopwatch.StartNew();
            UiNote("no answer (window never became clickable?)");
            probeForm = new Form();
            probeForm.Text = "NX AI Bridge - \u81ea\u68c0 Self-check";
            probeForm.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            probeForm.StartPosition = FormStartPosition.CenterScreen;
            probeForm.ClientSize = new Size(520, 230);
            probeForm.TopMost = true;
            Label text = new Label();
            text.SetBounds(14, 12, 492, 130);
            text.Text =
                "\u81ea\u68c0\u5df2\u5b8c\u6210\uff1a\u901a\u8fc7 " + passed + " \u9879\uff0c\u5931\u8d25 " + failed + " \u9879\u3002\n\n" +
                "\u6700\u540e\u4e00\u6b65\uff1a\u8bf7\u5148\u4e0d\u8981\u5173\u95ed\u672c\u7a97\u53e3\uff0c\u7528\u9f20\u6807\u5728 NX \u91cc\u65cb\u8f6c\u4e00\u4e0b\u6a21\u578b\uff08\u6216\u70b9\u51fb\u4efb\u610f\u83dc\u5355\uff09\uff0c\u7136\u540e\u56de\u7b54\uff1a\n" +
                "NX \u8fd8\u80fd\u6b63\u5e38\u64cd\u4f5c\u5417\uff1f\n\n" +
                "Self-check finished (" + passed + " passed, " + failed + " failed). Last step: WITHOUT closing this window, " +
                "try to rotate the model in NX, then answer: does NX still respond?";
            Button yes = new Button();
            yes.Text = "\u80fd\u64cd\u4f5c Yes";
            yes.SetBounds(14, 180, 150, 34);
            Button no = new Button();
            no.Text = "\u5361\u4f4f\u4e86 No, frozen";
            no.SetBounds(180, 180, 150, 34);
            yes.Click += delegate { FinishProbe("yes"); };
            no.Click += delegate { FinishProbe("no"); };
            probeForm.Controls.AddRange(new Control[] { text, yes, no });
            probeTimer = new Timer();
            probeTimer.Interval = 100;
            probeTimer.Tick += delegate
            {
                probeTicks++;
                if (probeTicks == 6 || probeTicks % 50 == 0) UiNote("no answer yet");
            };
            probeTimer.Start();
            probeForm.Show(); // modeless on purpose: Main() returns now
        }

        private static void FinishProbe(string answer)
        {
            UiNote(answer);
            try { probeTimer.Stop(); probeTimer.Dispose(); } catch (Exception) { }
            probeForm.Close();
            MessageBox.Show(
                "\u81ea\u68c0\u62a5\u544a\u5df2\u751f\u6210\uff0c\u8bf7\u628a\u8fd9\u4e2a\u6587\u4ef6\u5e26\u56de\u6216\u4e0a\u4f20\uff1a\n\nReport written - please bring back / upload this file:\n\n" +
                reportPath + (report.ContainsKey("desktop_copy") ? "\n\n(\u684c\u9762\u4e5f\u6709\u4e00\u4efd / copy on Desktop)" : ""),
                "NX AI Bridge");
        }
    }
}
