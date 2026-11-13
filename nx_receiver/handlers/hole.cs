// tools: create_hole
// A simple round hole = a cylinder subtracted from the body. This keeps the geometry exactly
// predictable (position / direction / diameter / depth in mm) and avoids the large, version
// dependent HolePackageBuilder API. The feature tree shows CYLINDER + SUBTRACT.
using System;
using System.Collections.Generic;
using NXOpen;
using Feature = NXOpen.Features.Feature;

namespace NxAiBridge.Handlers
{
    public class HoleTool : ITool
    {
        /// <summary>Distance along the ray at which it leaves the box (0 when it never enters).</summary>
        private static double ExitDistance(double[] o, double[] d, double[] box)
        {
            double t0 = 0, t1 = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(d[i]) < 1e-12)
                {
                    if (o[i] < box[i] - 1e-6 || o[i] > box[i + 3] + 1e-6) return 0;
                    continue;
                }
                double ta = (box[i] - o[i]) / d[i], tb = (box[i + 3] - o[i]) / d[i];
                t0 = Math.Max(t0, Math.Min(ta, tb));
                t1 = Math.Min(t1, Math.Max(ta, tb));
            }
            return t1 > t0 ? t1 : 0;
        }

        public object Run(string tool, Args a)
        {
            Nx.Work();
            Body body = Nx.ResolveBody(a.Str("body", null));
            double[] pos = a.Vec("position", 3);
            double[] dir = Vec.Unit(a.Vec("direction", 3, new double[] { 0, 0, -1 }));
            double diameter = a.Num("diameter");
            bool through = a.Bool("through", false) || !a.Has("depth");
            double exit = ExitDistance(pos, dir, Nx.BBox(body));
            if (exit <= 1e-9)
                throw new ToolError("INVALID_PARAMS", "The hole axis does not enter the body.",
                    "`position` must be on the entry face and `direction` must point INTO the material.");
            double depth = through ? exit + 1.0 : a.Num("depth"); // through holes overshoot the far side by 1 mm

            List<object> notes = new List<object>();
            Feature cyl = Nx.CreateCylinder(pos, dir, diameter, depth, notes);
            Body[] tools = Nx.FeatureBodies(cyl);
            if (tools.Length == 0) throw new ToolError("INTERNAL_ERROR", "Could not find the body of the hole cylinder.");
            Feature cut = Nx.Boolean("subtract", body, tools, notes);

            Dictionary<string, object> d = R.Obj("feature", cut != null ? cut.GetFeatureName() : cyl.GetFeatureName(), "type", "SIMPLE HOLE",
                "tool_feature", cyl.GetFeatureName(), "diameter", diameter, "depth", R.Round(through ? exit : depth), "through", through);
            d["bodies"] = R.List(Nx.BodyRef(body));
            Nx.AddBody(d, body);
            if (notes.Count > 0) d["notes"] = notes;
            return d;
        }
    }
}
