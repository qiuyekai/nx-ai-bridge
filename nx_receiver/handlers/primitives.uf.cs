// tools: create_block, create_cylinder, create_cone, create_sphere
// variant: 2
// Fallback: classic UF_MODL_create_*1 functions (stable since Unigraphics days), then an
// explicit boolean so the target body can be chosen. Units: mm.
using System;
using System.Collections.Generic;
using NXOpen;
using Feature = NXOpen.Features.Feature;

namespace NxAiBridge.Handlers
{
    public class PrimitivesUfTool : ITool
    {
        private static readonly double[] Zero = new double[] { 0, 0, 0 };
        private static readonly double[] ZAxis = new double[] { 0, 0, 1 };

        public object Run(string tool, Args a)
        {
            Nx.Work();
            string op = a.Str("boolean", "none");
            Body target = op == "none" ? null : Nx.ResolveBody(a.Str("target", null)); // resolve before the new body exists
            object modl = Dyn.Get(Nx.UF, "Modl");
            object sign = Dyn.Enum("NXOpen.UF.FeatureSigns", "Nullsign");
            object[] args;
            if (tool == "create_block")
            {
                string[] edges = new string[] { Nx.Num(a.Num("length")), Nx.Num(a.Num("width")), Nx.Num(a.Num("height")) };
                args = new object[] { sign, a.Vec("origin", 3, Zero), edges, Tag.Null };
                Dyn.Call(modl, "CreateBlock1", args);
            }
            else if (tool == "create_cylinder")
            {
                args = new object[] { sign, a.Vec("origin", 3, Zero), Nx.Num(a.Num("height")), Nx.Num(a.Num("diameter")), Vec.Unit(a.Vec("direction", 3, ZAxis)), Tag.Null };
                Dyn.Call(modl, "CreateCyl1", args);
            }
            else if (tool == "create_cone")
            {
                string[] diams = new string[] { Nx.Num(a.Num("base_diameter")), Nx.Num(a.Num("top_diameter", 0)) };
                args = new object[] { sign, a.Vec("origin", 3, Zero), Nx.Num(a.Num("height")), diams, Vec.Unit(a.Vec("direction", 3, ZAxis)), Tag.Null };
                Dyn.Call(modl, "CreateCone1", args);
            }
            else
            {
                args = new object[] { sign, a.Vec("center", 3, Zero), Nx.Num(a.Num("diameter")), Tag.Null };
                Dyn.Call(modl, "CreateSphere1", args);
            }
            Feature f = (Feature)NXOpen.Utilities.NXObjectManager.Get((Tag)args[args.Length - 1]);
            if (target == null) return Nx.FeatureResult(f);

            Body[] made = Nx.FeatureBodies(f);
            if (made.Length == 0) throw new ToolError("INTERNAL_ERROR", "Could not find the body of the new primitive.");
            List<object> notes = new List<object>();
            Feature bf = Nx.Boolean(op, target, made, notes);
            Dictionary<string, object> d = Nx.FeatureResult(f);
            if (bf != null) d["boolean_feature"] = bf.GetFeatureName();
            Nx.AddBody(d, target);
            d["bodies"] = R.List(Nx.BodyRef(target));
            return d;
        }
    }
}
