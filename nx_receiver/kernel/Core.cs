// nx-ai-bridge kernel - contracts shared by the kernel, nxlib and the tool handlers.
// C# 5 only. This file must not reference NXOpen.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NxAiBridge
{
    public static class BridgeInfo
    {
        public const string Receiver = "0.1.0a1";
        public const int Protocol = 1;
    }

    /// <summary>A tool handler. One handler file may serve several tools (see the "// tools:" header).</summary>
    public interface ITool
    {
        object Run(string tool, Args a);
    }

    /// <summary>Implemented by nxlib: wraps every tool call in an NX undo mark.</summary>
    public interface ISessionHooks
    {
        object Begin(string tool);           // returns a token (undo mark) or null
        void Commit(string tool, object token);
        void Rollback(string tool, object token);
    }

    /// <summary>An error the AI can act on. Codes: INVALID_PARAMS, NOT_FOUND, NO_WORK_PART, NX_ERROR, ...</summary>
    public class ToolError : Exception
    {
        public string Code;
        public string Hint;
        public object Details;

        public ToolError(string code, string message) : base(message) { Code = code; }
        public ToolError(string code, string message, string hint) : base(message) { Code = code; Hint = hint; }
        public ToolError(string code, string message, string hint, object details) : base(message) { Code = code; Hint = hint; Details = details; }

        /// <summary>Errors caused by the request itself: trying another implementation variant is pointless.</summary>
        public bool IsUserError
        {
            get { return Code == "INVALID_PARAMS" || Code == "NOT_FOUND" || Code == "NO_WORK_PART" || Code == "NOTHING_TO_UNDO"; }
        }
    }

    /// <summary>Typed access to the "params" object of a request.</summary>
    public class Args
    {
        public readonly Dictionary<string, object> Map;

        public Args(Dictionary<string, object> map)
        {
            Map = map ?? new Dictionary<string, object>();
        }

        public bool Has(string key)
        {
            object v;
            return Map.TryGetValue(key, out v) && v != null;
        }

        public object Raw(string key)
        {
            object v;
            return Map.TryGetValue(key, out v) ? v : null;
        }

        private object Required(string key)
        {
            if (!Has(key)) throw new ToolError("INVALID_PARAMS", "Missing required parameter '" + key + "'.");
            return Map[key];
        }

        public static double ToDouble(object v, string what)
        {
            if (v is double) return (double)v;
            if (v is int || v is long || v is float) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            throw new ToolError("INVALID_PARAMS", what + " must be a number.");
        }

        public double Num(string key) { return ToDouble(Required(key), "Parameter '" + key + "'"); }
        public double Num(string key, double def) { return Has(key) ? Num(key) : def; }
        public int Int(string key, int def) { return Has(key) ? (int)Math.Round(Num(key)) : def; }

        public string Str(string key)
        {
            object v = Required(key);
            if (!(v is string)) throw new ToolError("INVALID_PARAMS", "Parameter '" + key + "' must be a string.");
            return (string)v;
        }

        public string Str(string key, string def) { return Has(key) ? Str(key) : def; }

        public bool Bool(string key, bool def)
        {
            if (!Has(key)) return def;
            object v = Map[key];
            if (!(v is bool)) throw new ToolError("INVALID_PARAMS", "Parameter '" + key + "' must be true or false.");
            return (bool)v;
        }

        public List<object> List(string key)
        {
            if (!Has(key)) return new List<object>();
            List<object> l = Map[key] as List<object>;
            if (l == null) throw new ToolError("INVALID_PARAMS", "Parameter '" + key + "' must be an array.");
            return l;
        }

        public static double[] ToVec(object v, int n, string what)
        {
            List<object> l = v as List<object>;
            if (l == null || l.Count != n) throw new ToolError("INVALID_PARAMS", what + " must be an array of " + n + " numbers.");
            double[] r = new double[n];
            for (int i = 0; i < n; i++) r[i] = ToDouble(l[i], what);
            return r;
        }

        public double[] Vec(string key, int n) { return ToVec(Required(key), n, "Parameter '" + key + "'"); }
        public double[] Vec(string key, int n, double[] def) { return Has(key) ? Vec(key, n) : def; }
    }

    /// <summary>Folder layout below the home directory (same rules as the Python bridge).</summary>
    public static class Paths
    {
        public static string Home = DefaultHome();

        public static string DefaultHome()
        {
            string env = Environment.GetEnvironmentVariable("NX_AI_BRIDGE_HOME");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nx-ai-bridge");
        }

        public static string ReceiverDir { get { return Path.Combine(Home, "nx_receiver"); } }
        public static string HandlersDir = null; // override for tests
        public static string Handlers { get { return HandlersDir ?? Path.Combine(ReceiverDir, "handlers"); } }
        public static string NxLib { get { return Path.Combine(ReceiverDir, "nxlib"); } }
        public static string Cache { get { return Ensure(Path.Combine(Home, "cache")); } }
        public static string Logs { get { return Ensure(Path.Combine(Home, "logs")); } }
        public static string Reports { get { return Ensure(Path.Combine(Home, "reports")); } }
        public static string Requests { get { return Ensure(Path.Combine(Path.Combine(Home, "ipc"), "requests")); } }
        public static string Responses { get { return Ensure(Path.Combine(Path.Combine(Home, "ipc"), "responses")); } }
        public static string StateFile { get { return Path.Combine(Home, "receiver.json"); } }
        public static string ConfigFile { get { return Path.Combine(Home, "config.json"); } }

        public static string Ensure(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static class Log
    {
        private static readonly object Gate = new object();
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(Path.Combine(Paths.Logs, "receiver.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine, Utf8);
                }
            }
            catch (Exception) { }
        }

        /// <summary>Atomic write: temp file then rename, so readers never see half a file.</summary>
        public static void WriteFileAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Utf8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static double UnixTime()
        {
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}
