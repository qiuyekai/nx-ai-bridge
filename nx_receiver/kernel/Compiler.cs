// nx-ai-bridge kernel - compiles handler files (and run_journal code) at run time with CodeDom,
// exactly like NX compiles journals. Each unit is compiled separately so that one broken file
// never takes the others down, and every compiler message ends up in the tool response.
// C# 5 only. This file must not reference NXOpen.
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace NxAiBridge
{
    public class CompileFailure : Exception
    {
        public string Unit;
        public List<string> Errors;

        public CompileFailure(string unit, List<string> errors)
            : base("Compilation of " + unit + " failed: " + (errors.Count > 0 ? errors[0] : "unknown error") + (errors.Count > 1 ? " (+" + (errors.Count - 1) + " more)" : ""))
        {
            Unit = unit;
            Errors = errors;
        }
    }

    public static class ScriptCompiler
    {
        private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>();
        private static readonly Dictionary<string, CompileFailure> Failed = new Dictionary<string, CompileFailure>();
        private static List<string> nxRefs;

        /// <summary>Paths of the NX Open assemblies loaded in this process (empty outside NX).</summary>
        public static List<string> NxReferences()
        {
            if (nxRefs != null) return nxRefs;
            foreach (string name in new string[] { "NXOpen", "NXOpen.UF", "NXOpen.Utilities", "NXOpenUI" })
            {
                try { Assembly.Load(name); } catch (Exception) { }
            }
            List<string> refs = new List<string>();
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string n = asm.GetName().Name;
                    if (n.StartsWith("NXOpen", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(asm.Location) && !refs.Contains(asm.Location))
                        refs.Add(asm.Location);
                }
                catch (Exception) { }
            }
            nxRefs = refs;
            return refs;
        }

        public static string KernelPath
        {
            get { return typeof(ScriptCompiler).Assembly.Location; }
        }

        public static string Hash(string text)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Compile source files into one assembly (cached on disk by content hash).
        /// language: "csharp" or "vb". extraRefs: additional assembly paths.
        /// </summary>
        public static Assembly Compile(string unit, string[] files, string language, IList<string> extraRefs)
        {
            StringBuilder all = new StringBuilder(language);
            foreach (string f in files) all.Append('\n').Append(Path.GetFileName(f)).Append('\n').Append(File.ReadAllText(f));
            List<string> refs = new List<string>();
            refs.AddRange(new string[] { "System.dll", "System.Core.dll", "System.Windows.Forms.dll", "System.Drawing.dll", "System.Xml.dll", "System.Data.dll" });
            refs.AddRange(NxReferences());
            if (extraRefs != null) foreach (string r in extraRefs) if (!string.IsNullOrEmpty(r) && !refs.Contains(r)) refs.Add(r);
            foreach (string r in refs) all.Append('\n').Append(r);
            string safeUnit = unit.Replace('.', '_').Replace(' ', '_');
            string key = safeUnit + "-" + Hash(all.ToString());

            Assembly asm;
            if (Loaded.TryGetValue(key, out asm)) return asm;
            CompileFailure failure;
            if (Failed.TryGetValue(key, out failure)) throw failure;

            string dll = Path.Combine(Paths.Cache, key + ".dll");
            if (File.Exists(dll))
            {
                try
                {
                    asm = Assembly.LoadFrom(dll);
                    Loaded[key] = asm;
                    return asm;
                }
                catch (Exception) { try { File.Delete(dll); } catch (Exception) { } }
            }
            CleanOld(safeUnit, key);

            CodeDomProvider provider = language == "vb"
                ? (CodeDomProvider)new Microsoft.VisualBasic.VBCodeProvider()
                : (CodeDomProvider)new Microsoft.CSharp.CSharpCodeProvider();
            CompilerParameters cp = new CompilerParameters();
            cp.GenerateExecutable = false;
            cp.GenerateInMemory = false;
            cp.IncludeDebugInformation = false;
            cp.TreatWarningsAsErrors = false;
            cp.OutputAssembly = dll;
            cp.TempFiles = new TempFileCollection(Paths.Cache, false);
            if (language != "vb") cp.CompilerOptions = "/nowarn:1591,0618,0612";
            foreach (string r in refs) cp.ReferencedAssemblies.Add(r);

            CompilerResults results;
            try { results = provider.CompileAssemblyFromFile(cp, files); }
            catch (Exception ex)
            {
                List<string> one = new List<string>();
                one.Add("The compiler could not be started: " + ex.GetType().Name + ": " + ex.Message);
                throw new CompileFailure(unit, one); // not cached: may be transient
            }
            List<string> errors = new List<string>();
            foreach (CompilerError e in results.Errors)
            {
                if (e.IsWarning) continue;
                errors.Add(Path.GetFileName(e.FileName ?? "") + "(" + e.Line + "," + e.Column + "): error " + e.ErrorNumber + ": " + e.ErrorText);
            }
            if (errors.Count > 0)
            {
                failure = new CompileFailure(unit, errors);
                Failed[key] = failure;
                Log.Write("COMPILE FAILED " + unit + ": " + string.Join(" || ", errors.ToArray()));
                throw failure;
            }
            asm = results.CompiledAssembly;
            Loaded[key] = asm;
            Log.Write("compiled " + unit + " -> " + Path.GetFileName(dll));
            return asm;
        }

        private static void CleanOld(string safeUnit, string keepKey)
        {
            try
            {
                foreach (string old in Directory.GetFiles(Paths.Cache, safeUnit + "-*.dll"))
                {
                    if (Path.GetFileNameWithoutExtension(old) == keepKey) continue;
                    try { File.Delete(old); } catch (Exception) { } // still loaded: leave it
                }
            }
            catch (Exception) { }
        }

        /// <summary>Resolve references between our own dynamically compiled assemblies and to NXOpen.</summary>
        public static void InstallResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                string wanted = new AssemblyName(e.Name).Name;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { if (a.GetName().Name == wanted) return a; } catch (Exception) { }
                }
                return null;
            };
        }
    }
}
