// tools: extrude, revolve
// Sweeps all curves of a sketch. Units: mm and degrees.
// Follows the calls of a recorded NX journal (ExtrudeBuilder / RevolveBuilder + Section).
using System;
using System.Collections.Generic;
using NXOpen;
using Feature = NXOpen.Features.Feature;

namespace NxAiBridge.Handlers
{
    public class ExtrudeTool : ITool
    {
        private static object MakeSection(Part part, List<Curve> curves, Frame fr)
        {
            object section = Dyn.Call(part.Sections, "CreateSection", 0.0095, 0.01, 0.5); // chaining, distance, angle tolerance
            Dyn.TryCall(section, "SetAllowedEntityTypes", "OnlyCurves");
            Dyn.TryCall(section, "AllowSelfIntersection", true);
            object rule = Dyn.Call(part.ScRuleFactory, "CreateRuleCurveDumb", new object[] { curves.ToArray() });
            double[] help;
            try { help = Nx.PointOnCurve(curves[0].Tag); } catch (Exception) { help = fr.Origin; }
            // AddToSection(rules, seed, startConnector, endConnector, helpPoint, mode, chainWithinFeature)
            Dyn.Call(section, "AddToSection", Dyn.MakeArray("NXOpen.SelectionIntentRule", rule), curves[0], null, null, Vec.P(help), "Create", false);
            return section;
        }

        public object Run(string tool, Args a)
        {
            Part part = Nx.Work();
            string name = a.Str("sketch").ToUpperInvariant();
            Frame fr = Nx.LoadFrame(part, name);
            List<Curve> curves = Nx.SketchCurves(part, name);
            if (curves.Count == 0) throw new ToolError("INVALID_PARAMS", "Sketch '" + name + "' is empty.", "Add curves with sketch_line / sketch_circle / sketch_rectangle / sketch_arc first.");

            object builder = null;
            try
            {
                if (tool == "extrude")
                {
                    double start = a.Num("start", 0), end = a.Num("distance");
                    if (Math.Abs(end - start) < 1e-9) throw new ToolError("INVALID_PARAMS", "Extrude start and end are equal.");
                    double[] dir = a.Has("direction") ? Vec.Unit(a.Vec("direction", 3)) : fr.Normal;
                    builder = Dyn.Call(part.Features, "CreateExtrudeBuilder", new object[] { null });
                    Dyn.Set(builder, "Section", MakeSection(part, curves, fr));
                    Dyn.TryCall(builder, "AllowSelfIntersectingSection", true);
                    Dyn.TrySet(builder, "DistanceTolerance", 0.01);
                    Dyn.Set(builder, "Direction", Nx.MakeDirection(part, fr.Origin, dir));
                    Dyn.SetPath(builder, "Limits.StartExtend.Value.RightHandSide", Nx.Num(start));
                    Dyn.SetPath(builder, "Limits.EndExtend.Value.RightHandSide", Nx.Num(end));
                }
                else
                {
                    double a0 = a.Num("start_angle", 0), a1 = a.Num("angle", 360);
                    if (Math.Abs(a1 - a0) < 1e-9 || Math.Abs(a1 - a0) > 360 + 1e-9) throw new ToolError("INVALID_PARAMS", "Revolve angle must be between 0 and 360 degrees.");
                    builder = Dyn.Call(part.Features, "CreateRevolveBuilder", new object[] { null });
                    Dyn.Set(builder, "Section", MakeSection(part, curves, fr));
                    Dyn.TrySet(builder, "Tolerance", 0.01);
                    Dyn.Set(builder, "Axis", Nx.MakeAxis(part, a.Vec("axis_point", 3, new double[] { 0, 0, 0 }), a.Vec("axis_direction", 3)));
                    Dyn.SetPath(builder, "Limits.StartExtend.Value.RightHandSide", Nx.Num(a0));
                    Dyn.SetPath(builder, "Limits.EndExtend.Value.RightHandSide", Nx.Num(a1));
                }
                Nx.SetBuilderBoolean(builder, a);
                Feature f = (Feature)Dyn.Call(builder, "CommitFeature");
                Dictionary<string, object> d = Nx.FeatureResult(f);
                d["sketch"] = name;
                return d;
            }
            finally
            {
                if (builder != null) Dyn.TryCall(builder, "Destroy");
            }
        }
    }
}
