// tools: run_journal
// Escape hatch: compile and run arbitrary NX Open code (C# or VB) inside NX.
// SECURITY: this executes whatever code it is given with the user's privileges.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace NxAiBridge.Handlers
{
    public class RunJournalTool : ITool
    {
        public object Run(string tool, Args a)
        {
            string code = a.Str("code");
            string language = a.Str("language", "vb").ToLowerInvariant();
            if (language != "vb" && language != "csharp") throw new ToolError("INVALID_PARAMS", "language must be 'vb' or 'csharp'.");
            List<string> argList = new List<string>();
            foreach (object item in a.List("args")) argList.Add(Convert.ToString(item));

            string file = Path.Combine(Paths.Cache, "journal-" + ScriptCompiler.Hash(language + code) + (language == "vb" ? ".vb" : ".cs"));
            File.WriteAllText(file, code, Log.Utf8);
            List<string> refs = new List<string>();
            refs.Add(ScriptCompiler.KernelPath);
            try
            {
                Assembly lib = Dispatcher.NxLibAssembly();
                if (lib != null) refs.Add(lib.Location);
            }
            catch (Exception) { }

            Assembly asm;
            try { asm = ScriptCompiler.Compile("journal", new string[] { file }, language, refs); }
            catch (CompileFailure cf)
            {
                throw new ToolError("COMPILE_ERROR", "The journal does not compile: " + (cf.Errors.Count > 0 ? cf.Errors[0] : cf.Message),
                    "Fix the code and call run_journal again. Use nx_api_lookup to check NX Open member names. Line numbers refer to your code.",
                    R.Obj("errors", cf.Errors));
            }

            MethodInfo main = null;
            foreach (Type t in asm.GetTypes())
            {
                MethodInfo m = t.GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null) { main = m; break; }
            }
            if (main == null)
                throw new ToolError("COMPILE_ERROR", "No Main method found in the journal.",
                    "The code must contain 'Sub Main' / 'Function Main' in a Module (VB) or 'public static ... Main' in a class (C#).");

            TextWriter oldOut = Console.Out;
            StringWriter captured = new StringWriter();
            object ret;
            Console.SetOut(captured);
            try
            {
                ret = main.Invoke(null, main.GetParameters().Length == 0 ? null : new object[] { argList.ToArray() });
            }
            finally
            {
                Console.SetOut(oldOut);
            }
            return R.Obj("executed", true, "return", ret == null ? null : Convert.ToString(ret), "stdout", captured.ToString());
        }
    }
}
