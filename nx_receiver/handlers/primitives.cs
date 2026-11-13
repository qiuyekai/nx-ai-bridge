// tools: create_block, create_cylinder, create_cone, create_sphere
// variant: 1
// Primitives through the NX feature builders (same calls a recorded journal makes).
// Units: mm. UNVERIFIED member names are accessed through Dyn; on mismatch variant 2 (UF) takes over.
using System;
using System.Collections.Generic;
using NXOpen;
using Feature = NXOpen.Features.Feature;

namespace NxAiBridge.Handlers
{
    public class PrimitivesTool : ITool
    {
        private static readonly double[] Zero = new double[] { 0, 0, 0 };
        private static readonly double[] ZAxis = new double[] { 0, 0, 1 };

        public object Run(string tool, Args a)
        {
            Part part = Nx.Work();
            object builder = null;
            try
            {
                if (tool == "create_block")
                {
                    builder = Dyn.Call(part.Features, "CreateBlockFeatureBuilder", new object[] { null });
                    Nx.SetBuilderBoolean(builder, a);
                    Dyn.Set(builder, "Type", "OriginAndEdgeLengths");
                    Dyn.Call(builder, "SetOriginAndLengths", Vec.P(a.Vec("origin", 3, Zero)),
                        Nx.Num(a.Num("length")), Nx.Num(a.Num("width")), Nx.Num(a.Num("height")));
                }
                else if (tool == "create_cylinder")
                {
                    builder = Dyn.Call(part.Features, "CreateCylinderBuilder", new object[] { null });
                    Nx.SetBuilderBoolean(builder, a);
                    Dyn.Set(builder, "Type", "AxisDiameterAndHeight");
                    Dyn.SetPath(builder, "Diameter.RightHandSide", Nx.Num(a.Num("diameter")));
                    Dyn.SetPath(builder, "Height.RightHandSide", Nx.Num(a.Num("height")));
                    Dyn.Set(builder, "Axis", Nx.MakeAxis(part, a.Vec("origin", 3, Zero), a.Vec("direction", 3, ZAxis)));
                }
                else if (tool == "create_cone")
                {
                    double top = a.Num("top_diameter", 0);
                    if (Math.Abs(top - a.Num("base_diameter")) < 1e-9)
                        throw new ToolError("INVALID_PARAMS", "Base and top diameter of a cone must differ (use create_cylinder).");
                    builder = Dyn.Call(part.Features, "CreateConeBuilder", new object[] { null });
                    Nx.SetBuilderBoolean(builder, a);
                    Dyn.Set(builder, "Type", "DiametersAndHeight");
                    Dyn.SetPath(builder, "BaseDiameter.RightHandSide", Nx.Num(a.Num("base_diameter")));
                    Dyn.SetPath(builder, "TopDiameter.RightHandSide", Nx.Num(top));
                    Dyn.SetPath(builder, "Height.RightHandSide", Nx.Num(a.Num("height")));
                    Dyn.Set(builder, "Axis", Nx.MakeAxis(part, a.Vec("origin", 3, Zero), a.Vec("direction", 3, ZAxis)));
                }
                else
                {
                    builder = Dyn.Call(part.Features, "CreateSphereBuilder", new object[] { null });
                    Nx.SetBuilderBoolean(builder, a);
                    Dyn.Set(builder, "Type", "CenterPointAndDiameter");
                    Dyn.Set(builder, "CenterPoint", part.Points.CreatePoint(Vec.P(a.Vec("center", 3, Zero))));
                    Dyn.SetPath(builder, "Diameter.RightHandSide", Nx.Num(a.Num("diameter")));
                }
                Feature f = (Feature)Dyn.Call(builder, "CommitFeature");
                return Nx.FeatureResult(f);
            }
            finally
            {
                if (builder != null) Dyn.TryCall(builder, "Destroy");
            }
        }
    }
}
