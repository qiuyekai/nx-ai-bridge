// tools: dyn_demo
// Test handler: exercises the late-binding helper (Dyn) the NX handlers rely on.
using System;
using System.Collections.Generic;

namespace NxAiBridge.Handlers
{
    public class Target
    {
        public enum Kind { Plain, Fancy }
        public class Inner { public string Text = "x"; public Kind Mode { get; set; } }
        private readonly Inner inner = new Inner();
        public Inner Child { get { return inner; } }
        public double Size { get; set; }

        public void Outs(string a, out int n, double[] fill, ref string name) { n = a.Length; fill[0] = 7.5; name = name + "!"; }
        public void OutArray(int count, out double[] values) { values = new double[count]; values[count - 1] = 9; }
        public string Pick(Kind k) { return "kind:" + k; }
        public int Sum(int[] xs) { int t = 0; foreach (int x in xs) t += x; return t; }
        public string Over(string s) { return "string"; }
        public string Over(Kind k) { return "enum"; }
        public string Over(double d) { return "double"; }
        public string Nullable(object o, string s) { return o == null && s == null ? "nulls" : "values"; }
        public void Fail() { throw new InvalidOperationException("inner failure"); }
    }

    public class DynDemo : ITool
    {
        public object Run(string tool, Args a)
        {
            Target t = new Target();
            object[] outs = new object[] { "abcd", null, new double[1], "n" };
            Dyn.Call(t, "Outs", outs);
            object[] arr = new object[] { 3, null };
            Dyn.Call(t, "OutArray", arr);
            Dyn.Set(t, "Size", 4);                         // int -> double
            Dyn.SetPath(t, "Child.Mode", "fancy");         // string -> enum, case-insensitive
            Dyn.SetPath(t, "Child.Text", "hello");         // public field
            string failure = "none";
            try { Dyn.Call(t, "Fail"); }
            catch (DynInvocationException ex) { failure = ex.InnerException.GetType().Name + ":" + ex.Message; }
            string missing = "none";
            try { Dyn.Set(t, "Sise", 1); }
            catch (ApiMismatch ex) { missing = string.Join("|", ex.Candidates.ToArray()); }
            return R.Obj(
                "out_int", outs[1], "filled", ((double[])outs[2])[0], "ref_string", outs[3],
                "out_array_last", ((double[])arr[1])[2],
                "size", Dyn.Get(t, "Size"), "mode", Convert.ToString(Dyn.GetPath(t, "Child.Mode")), "text", Dyn.GetPath(t, "Child.Text"),
                "pick", Dyn.Call(t, "Pick", "Fancy"),
                "sum_list", Dyn.Call(t, "Sum", new object[] { new List<object>(new object[] { 1.0, 2.0, 3.0 }) }),
                "over_string", Dyn.Call(t, "Over", "Fancy"), "over_enum", Dyn.Call(t, "Over", Target.Kind.Fancy), "over_int", Dyn.Call(t, "Over", 3),
                "nulls", Dyn.Call(t, "Nullable", null, null),
                "enum_lookup", Convert.ToString(Dyn.Enum("NxAiBridge.Handlers.Target.Kind", "plain")),
                "try_set_missing", Dyn.TrySet(t, "Nope.Deeper", 1), "set_any", Dyn.SetAny(t, new string[] { "Nope", "Size" }, 2.5),
                "failure", failure, "missing_candidates", missing);
        }
    }
}
