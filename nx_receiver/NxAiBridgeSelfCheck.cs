// NX AI Bridge - SELF-CHECK journal (generated from NxAiBridgeReceiver.cs by scripts/sync_loaders.py - do not edit).
//
// Play this file once inside NX: Alt+F8 (Tools > Journal > Play...), select it, click Run.
// It tests every modelling tool in two temporary parts, takes about a minute, and writes
//   %USERPROFILE%\.nx-ai-bridge\reports\nx-ai-bridge-selfcheck-<time>.json   (also copied to the Desktop).
// Close your own work first: the journal creates and closes parts.
//
// NX AI Bridge - receiver journal (ALPHA, not yet verified on a real NX installation).
//
// HOW TO START (the only manual step):
//   In NX press Alt+F8 (menu: Tools > Journal > Play...), select this file, click Run.
//   A small "NX AI Bridge" window appears; while it is open, AI tools can drive NX.
//
// This file is only a loader. It compiles the real receiver ("kernel") from
//   %USERPROFILE%\.nx-ai-bridge\nx_receiver\kernel\*.cs     (or %NX_AI_BRIDGE_HOME%\nx_receiver\...)
// with the .NET compiler that is part of Windows - the same mechanism NX uses for journals.
// Nothing is installed, no administrator rights and no NX Open author licence are needed.
//
// Written for NX 10 (.NET Framework 4.x, C# 5): do not use newer C# syntax in this file.
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

public class NxAiBridgeSelfCheck
{
    private const string DefaultMode = "--selfcheck";

    public static void Main(string[] args)
    {
        string home = Environment.GetEnvironmentVariable("NX_AI_BRIDGE_HOME");
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nx-ai-bridge");
        try
        {
            List<string> all = new List<string>();
            bool hasMode = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    all.Add(args[i]);
                    if (args[i] == "--home" && i + 1 < args.Length) home = args[i + 1];
                    if (args[i] == "--live" || args[i] == "--modal" || args[i] == "--console" || args[i] == "--batch"
                        || args[i] == "--selfcheck" || args[i] == "--compile-check") hasMode = true;
                }
            }
            if (!hasMode) all.Add(DefaultMode);
            all.Add("--home");
            all.Add(home);
            Assembly kernel = LoadKernel(home);
            Type entry = kernel.GetType("NxAiBridge.Entry", true);
            entry.GetMethod("Run").Invoke(null, new object[] { all.ToArray() });
        }
        catch (Exception ex)
        {
            Report(home, ex);
        }
    }

    private static Assembly LoadKernel(string home)
    {
        string dir = Path.Combine(Path.Combine(home, "nx_receiver"), "kernel");
        if (!Directory.Exists(dir))
            throw new Exception("Receiver files not found in " + dir + ". Run install.ps1 (or: nx-ai-bridge install-receiver) first.");
        string[] files = Directory.GetFiles(dir, "*.cs");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        if (files.Length == 0) throw new Exception("No kernel source files in " + dir);

        StringBuilder text = new StringBuilder();
        foreach (string f in files) text.Append(Path.GetFileName(f)).Append('\n').Append(File.ReadAllText(f)).Append('\n');
        string hash;
        using (MD5 md5 = MD5.Create())
        {
            byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 6; i++) sb.Append(h[i].ToString("x2"));
            hash = sb.ToString();
        }
        string name = "NxAiBridgeKernel-" + hash;

        foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { if (loaded.GetName().Name == name) return loaded; } catch (Exception) { }
        }
        string cache = Path.Combine(home, "cache");
        if (!Directory.Exists(cache)) Directory.CreateDirectory(cache);
        string dll = Path.Combine(cache, name + ".dll");
        if (File.Exists(dll))
        {
            try { return Assembly.LoadFrom(dll); }
            catch (Exception) { try { File.Delete(dll); } catch (Exception) { } }
        }

        CompilerParameters cp = new CompilerParameters();
        cp.GenerateExecutable = false;
        cp.GenerateInMemory = false;
        cp.OutputAssembly = dll;
        cp.TempFiles = new TempFileCollection(cache, false);
        cp.ReferencedAssemblies.Add("System.dll");
        cp.ReferencedAssemblies.Add("System.Core.dll");
        cp.ReferencedAssemblies.Add("System.Windows.Forms.dll");
        cp.ReferencedAssemblies.Add("System.Drawing.dll");
        CompilerResults results = new Microsoft.CSharp.CSharpCodeProvider().CompileAssemblyFromFile(cp, files);
        StringBuilder errors = new StringBuilder();
        foreach (CompilerError e in results.Errors)
        {
            if (e.IsWarning) continue;
            errors.Append(Path.GetFileName(e.FileName ?? "")).Append('(').Append(e.Line).Append(',').Append(e.Column).Append("): ")
                  .Append(e.ErrorNumber).Append(": ").Append(e.ErrorText).Append(Environment.NewLine);
        }
        if (errors.Length > 0) throw new Exception("The receiver kernel does not compile:" + Environment.NewLine + errors);
        return results.CompiledAssembly;
    }

    private static void Report(string home, Exception ex)
    {
        while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
        string text = "NX AI Bridge could not start." + Environment.NewLine + ex.GetType().FullName + ": " + ex.Message
            + Environment.NewLine + ex.StackTrace + Environment.NewLine;
        try
        {
            string logs = Path.Combine(home, "logs");
            if (!Directory.Exists(logs)) Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "loader-error.txt"), text);
            text += Environment.NewLine + "(saved to " + Path.Combine(logs, "loader-error.txt") + ")";
        }
        catch (Exception) { }
        try { Console.Error.WriteLine(text); } catch (Exception) { }
        try
        {
            NXOpen.Session session = NXOpen.Session.GetSession();
            session.ListingWindow.Open();
            session.ListingWindow.WriteLine(text);
        }
        catch (Exception) { }
    }

    // Keep the compiled journal loaded after Main() returns: the control window lives on.
    public static int GetUnloadOption(string dummy)
    {
        return System.Convert.ToInt32(NXOpen.Session.LibraryUnloadOption.AtTermination);
    }
}
