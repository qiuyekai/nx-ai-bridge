// tools: edge_blend, chamfer
// Constant-radius edge blend and symmetric chamfer. Units: mm.
using System;
using System.Collections.Generic;
using NXOpen;
using Feature = NXOpen.Features.Feature;

namespace NxAiBridge.Handlers
{
    public class EdgeOpsTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Part part = Nx.Work();
            Edge[] edges = Nx.ResolveEdges(a);
            object collector = Dyn.Call(part.ScCollectors, "CreateCollector");
            object rule = Dyn.Call(part.ScRuleFactory, "CreateRuleEdgeDumb", new object[] { edges });
            Dyn.Call(collector, "ReplaceRules", Dyn.MakeArray("NXOpen.SelectionIntentRule", rule), false);

            object builder = null;
            try
            {
                if (tool == "edge_blend")
                {
                    builder = Dyn.Call(part.Features, "CreateEdgeBlendBuilder", new object[] { null });
                    Dyn.TrySet(builder, "Tolerance", 0.01);
                    Dyn.Call(builder, "AddChainset", collector, Nx.Num(a.Num("radius")));
                }
                else
                {
                    builder = Dyn.Call(part.Features, "CreateChamferBuilder", new object[] { null });
                    Dyn.Set(builder, "Option", "SymmetricOffsets");
                    string offset = Nx.Num(a.Num("distance"));
                    // UNVERIFIED: the offset is either an Expression property or a string property
                    if (!Dyn.TrySet(builder, "FirstOffsetExp.RightHandSide", offset)) Dyn.Set(builder, "FirstOffset", offset);
                    Dyn.TrySet(builder, "Tolerance", 0.01);
                    Dyn.Set(builder, "SmartCollector", collector);
                }
                Feature f = (Feature)Dyn.Call(builder, "CommitFeature");
                Dictionary<string, object> d = Nx.FeatureResult(f);
                d["edge_count"] = edges.Length;
                return d;
            }
            finally
            {
                if (builder != null) Dyn.TryCall(builder, "Destroy");
            }
        }
    }
}
