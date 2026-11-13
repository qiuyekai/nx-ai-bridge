// nx-ai-bridge kernel - finds the handler for a tool, compiles it on demand, runs it inside an
// undo mark and turns every outcome into the JSON envelope the bridge expects.
//
// Handler files live in <home>/nx_receiver/handlers/*.cs and start with header comments:
//     // tools: create_block, create_cylinder      (required) tools served by this file
//     // variant: 2                                (optional) lower numbers are tried first
// Several files may serve the same tool: when variant 1 fails to compile or throws, the model is
// rolled back and variant 2 is tried. The response says which variant ran ("meta.variant") and
// what went wrong before ("meta.attempts").
//
// Handlers are recompiled automatically when their file changes - no NX restart needed.
// C# 5 only. This file must not reference NXOpen.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace NxAiBridge
{
    public class HandlerFile
    {
        public string Path;
        public string Name;
        public int Variant = 1;
        public List<string> Tools = new List<string>();
    }

    public static class Dispatcher
    {
        public static string Mode = "live";           // live | batch | console | selfcheck
        public static bool StopRequested;
        public static int OkCount;
        public static int ErrorCount;
        public static string LastTool = "";
        private static ISessionHooks hooks;
        private static string hooksKey;
        private static readonly Dictionary<string, ITool> Instances = new Dictionary<string, ITool>();

        // ------------------------------------------------------------ discovery
        public static List<HandlerFile> Scan()
        {
            List<HandlerFile> list = new List<HandlerFile>();
            if (!Directory.Exists(Paths.Handlers)) return list;
            string[] files = Directory.GetFiles(Paths.Handlers, "*.cs");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                HandlerFile h = new HandlerFile();
                h.Path = file;
                h.Name = System.IO.Path.GetFileName(file);
                try
                {
                    using (StreamReader reader = new StreamReader(file))
                    {
                        for (int i = 0; i < 25; i++)
                        {
                            string line = reader.ReadLine();
                            if (line == null) break;
                            Match m = Regex.Match(line, @"^//\s*tools:\s*(.+)$");
                            if (m.Success)
                                foreach (string t in m.Groups[1].Value.Split(','))
                                    if (t.Trim().Length > 0) h.Tools.Add(t.Trim());
                            m = Regex.Match(line, @"^//\s*variant:\s*(\d+)");
                            if (m.Success) h.Variant = int.Parse(m.Groups[1].Value);
                        }
                    }
                }
                catch (Exception) { }
                if (h.Tools.Count > 0) list.Add(h);
            }
            list.Sort(delegate(HandlerFile a, HandlerFile b)
            {
                int c = a.Variant.CompareTo(b.Variant);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        public static List<HandlerFile> HandlersFor(string tool)
        {
            List<HandlerFile> result = new List<HandlerFile>();
            foreach (HandlerFile h in Scan()) if (h.Tools.Contains(tool)) result.Add(h);
            return result;
        }

        // ------------------------------------------------------------ compilation
        /// <summary>Compile nxlib (shared NX helpers). Returns null when there is no nxlib folder.</summary>
        public static Assembly NxLibAssembly()
        {
            if (!Directory.Exists(Paths.NxLib)) return null;
            string[] files = Directory.GetFiles(Paths.NxLib, "*.cs");
            if (files.Length == 0) return null;
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            List<string> refs = new List<string>();
            refs.Add(ScriptCompiler.KernelPath);
            return ScriptCompiler.Compile("nxlib", files, "csharp", refs);
        }

        public static ITool Load(HandlerFile h)
        {
            List<string> refs = new List<string>();
            refs.Add(ScriptCompiler.KernelPath);
            Assembly lib = NxLibAssembly(); // throws CompileFailure("nxlib") when the shared helpers are broken
            if (lib != null) refs.Add(lib.Location);
            Assembly asm = ScriptCompiler.Compile("handler_" + System.IO.Path.GetFileNameWithoutExtension(h.Name), new string[] { h.Path }, "csharp", refs);
            string key = asm.FullName;
            ITool tool;
            if (Instances.TryGetValue(key, out tool)) return tool;
            foreach (Type t in asm.GetTypes())
            {
                if (typeof(ITool).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                {
                    tool = (ITool)Activator.CreateInstance(t);
                    Instances[key] = tool;
                    return tool;
                }
            }
            throw new ToolError("INTERNAL_ERROR", h.Name + " contains no public class implementing NxAiBridge.ITool.");
        }

        private static ISessionHooks Hooks()
        {
            try
            {
                Assembly lib = NxLibAssembly();
                if (lib == null) return null;
                if (hooks != null && hooksKey == lib.FullName) return hooks;
                foreach (Type t in lib.GetTypes())
                {
                    if (typeof(ISessionHooks).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                    {
                        hooks = (ISessionHooks)Activator.CreateInstance(t);
                        hooksKey = lib.FullName;
                        return hooks;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        // ------------------------------------------------------------ errors
        public static Exception Unwrap(Exception ex)
        {
            while ((ex is TargetInvocationException || ex is DynInvocationException) && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        public static Dictionary<string, object> ErrorOf(Exception raw)
        {
            Exception ex = Unwrap(raw);
            Dictionary<string, object> err = new Dictionary<string, object>();
            ToolError te = ex as ToolError;
            CompileFailure cf = ex as CompileFailure;
            ApiMismatch am = ex as ApiMismatch;
            string typeName = ex.GetType().FullName ?? ex.GetType().Name;
            if (te != null)
            {
                err["code"] = te.Code;
                err["message"] = te.Message;
                if (te.Hint != null) err["hint"] = te.Hint;
                if (te.Details != null) err["details"] = te.Details;
                Exception inner = te.InnerException == null ? null : Unwrap(te.InnerException);
                if (inner != null && IsNxException(inner)) err["nx_error"] = NxErrorOf(inner);
            }
            else if (cf != null)
            {
                err["code"] = "COMPILE_ERROR";
                err["message"] = cf.Message;
                err["details"] = R.Obj("unit", cf.Unit, "errors", cf.Errors);
                err["hint"] = "A receiver source file does not compile against this NX version. Fix the listed lines in "
                    + Paths.ReceiverDir + " (files are recompiled automatically) or report the errors.";
            }
            else if (am != null)
            {
                err["code"] = "API_MISMATCH";
                err["message"] = am.Message;
                err["details"] = R.Obj("type", am.TypeName, "member", am.Member, "candidates", am.Candidates);
                err["hint"] = "The handler uses an NX Open member that does not exist in this NX version. Pick the right one from 'candidates' and edit the handler file.";
                err["stack"] = ShortStack(ex);
            }
            else if (IsNxException(ex))
            {
                err["code"] = "NX_ERROR";
                err["message"] = ex.Message;
                err["nx_error"] = NxErrorOf(ex);
                err["stack"] = ShortStack(ex);
            }
            else
            {
                err["code"] = "INTERNAL_ERROR";
                err["message"] = typeName + ": " + ex.Message;
                err["stack"] = ShortStack(ex);
            }
            return err;
        }

        private static bool IsNxException(Exception ex)
        {
            for (Type t = ex.GetType(); t != null; t = t.BaseType) if (t.FullName == "NXOpen.NXException") return true;
            return false;
        }

        private static Dictionary<string, object> NxErrorOf(Exception ex)
        {
            Dictionary<string, object> d = R.Obj("type", ex.GetType().FullName, "message", ex.Message);
            try { d["error_code"] = Dyn.Get(ex, "ErrorCode"); } catch (Exception) { }
            return d;
        }

        private static string ShortStack(Exception ex)
        {
            string s = ex.StackTrace ?? "";
            string[] lines = s.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> keep = new List<string>();
            foreach (string l in lines)
            {
                string t = l.Trim();
                if (t.Contains("System.Reflection") || t.Contains("System.RuntimeMethodHandle")) continue;
                keep.Add(t);
                if (keep.Count >= 8) break;
            }
            return string.Join(" <- ", keep.ToArray());
        }

        // ------------------------------------------------------------ execution
        public static Dictionary<string, object> Call(string tool, Dictionary<string, object> parameters)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Dictionary<string, object> resp = new Dictionary<string, object>();
            Dictionary<string, object> meta = R.Obj("receiver", "nx", "mode", Mode);
            List<object> attempts = new List<object>();
            LastTool = tool ?? "";
            try
            {
                if (tool == "__shutdown") { StopRequested = true; resp["ok"] = true; resp["result"] = R.Obj("stopping", true); }
                else if (tool == "__ping") { resp["ok"] = true; resp["result"] = R.Obj("pong", true); }
                else
                {
                    List<HandlerFile> candidates = HandlersFor(tool);
                    if (candidates.Count == 0)
                        throw new ToolError("UNKNOWN_TOOL", "No handler file in " + Paths.Handlers + " serves the tool '" + tool + "'.");
                    Exception firstError = null;
                    bool done = false;
                    foreach (HandlerFile h in candidates)
                    {
                        object token = null;
                        ISessionHooks hk = null;
                        bool began = false;
                        try
                        {
                            ITool impl = Load(h);
                            hk = Hooks();
                            if (hk != null) { token = hk.Begin(tool); began = true; }
                            object result = impl.Run(tool, new Args(parameters));
                            if (began) hk.Commit(tool, token);
                            resp["ok"] = true;
                            resp["result"] = result ?? new Dictionary<string, object>();
                            meta["variant"] = h.Name;
                            done = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (began) { try { hk.Rollback(tool, token); } catch (Exception) { } }
                            Exception real = Unwrap(ex);
                            Dictionary<string, object> attempt = ErrorOf(real);
                            attempt["variant"] = h.Name;
                            attempts.Add(attempt);
                            if (firstError == null) firstError = real;
                            ToolError te = real as ToolError;
                            if (te != null && te.IsUserError) { firstError = real; break; }
                        }
                    }
                    if (!done)
                    {
                        resp["ok"] = false;
                        resp["error"] = ErrorOf(firstError);
                        if (attempts.Count > 0) meta["variant"] = ((Dictionary<string, object>)attempts[0])["variant"];
                    }
                    if (attempts.Count > 0 && (done || attempts.Count > 1)) meta["attempts"] = attempts;
                }
            }
            catch (Exception ex)
            {
                resp["ok"] = false;
                resp["error"] = ErrorOf(ex);
            }
            meta["elapsed_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
            resp["meta"] = meta;
            bool ok = (bool)resp["ok"];
            if (ok) OkCount++; else ErrorCount++;
            Log.Write((ok ? "ok    " : "ERROR ") + tool + " " + sw.ElapsedMilliseconds + "ms"
                + (ok ? "" : " " + Json.Write(resp["error"])));
            return resp;
        }

        /// <summary>Handle one request given as JSON text; always returns a JSON response.</summary>
        public static string Handle(string requestJson)
        {
            object id = null;
            Dictionary<string, object> resp;
            try
            {
                Dictionary<string, object> req = Json.ParseObject(requestJson);
                req.TryGetValue("id", out id);
                object tool;
                if (!req.TryGetValue("tool", out tool) || !(tool is string))
                    throw new ToolError("BAD_REQUEST", "Request has no 'tool' string.");
                object p;
                req.TryGetValue("params", out p);
                resp = Call((string)tool, p as Dictionary<string, object>);
            }
            catch (Exception ex)
            {
                resp = new Dictionary<string, object>();
                resp["ok"] = false;
                Dictionary<string, object> err = ErrorOf(ex);
                if (ex is FormatException) { err["code"] = "BAD_REQUEST"; err["message"] = "Invalid JSON request: " + ex.Message; }
                resp["error"] = err;
            }
            resp["id"] = id;
            return Json.Write(resp);
        }

        /// <summary>Compile nxlib and every handler; returns file name -> "ok" or list of errors.</summary>
        public static Dictionary<string, object> CompileAll()
        {
            Dictionary<string, object> report = new Dictionary<string, object>();
            try { report["nxlib"] = NxLibAssembly() == null ? (object)"missing" : "ok"; }
            catch (Exception ex) { report["nxlib"] = ErrorOf(ex); }
            foreach (HandlerFile h in Scan())
            {
                try { Load(h); report[h.Name] = "ok"; }
                catch (Exception ex) { report[h.Name] = ErrorOf(ex); }
            }
            return report;
        }
    }
}
