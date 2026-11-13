// tools: shell
// Hollow a solid, optionally opening faces. Units: mm.
// UNVERIFIED: ShellBuilder member names (accessed through Dyn so mismatches are reported precisely).
using System;
using System.Collections.Generic;
using NXOpen;
using Feature = NXOpen.Features.Feature;

namespace NxAiBridge.Handlers
{
    public class ShellTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Part part = Nx.Work();
            Body body = Nx.ResolveBody(a.Str("body", null));
            string bodyRef = Nx.BodyRef(body);
            List<Face> faces = new List<Face>();
            List<object> removed = new List<object>();
            foreach (object sel in a.List("remove_faces"))
            {
                Face f = Nx.ResolveFace(sel, bodyRef);
                if (faces.Contains(f)) continue;
                faces.Add(f);
                removed.Add("F:" + bodyRef + ":" + Array.IndexOf(body.GetFaces(), f));
            }

            object builder = Dyn.Call(part.Features, "CreateShellBuilder", new object[] { null });
            try
            {
                Dyn.TrySet(builder, "Tolerance", 0.01);
                Dyn.SetPath(builder, "DefaultThickness.RightHandSide", Nx.Num(a.Num("thickness")));
                object collector = Dyn.Call(part.ScCollectors, "CreateCollector");
                if (faces.Count > 0)
                {
                    object rule = Dyn.Call(part.ScRuleFactory, "CreateRuleFaceDumb", new object[] { faces.ToArray() });
                    Dyn.Call(collector, "ReplaceRules", Dyn.MakeArray("NXOpen.SelectionIntentRule", rule), false);
                    Dyn.SetAny(builder, new string[] { "RemovedFacesCollector", "FacesToPierce", "FaceCollector" }, collector);
                }
                else
                {
                    // closed shell of the whole body
                    object rule;
                    try { rule = Dyn.Call(part.ScRuleFactory, "CreateRuleBodyDumb", new Body[] { body }, true); }
                    catch (ApiMismatch) { rule = Dyn.Call(part.ScRuleFactory, "CreateRuleBodyDumb", new object[] { new Body[] { body } }); }
                    Dyn.Call(collector, "ReplaceRules", Dyn.MakeArray("NXOpen.SelectionIntentRule", rule), false);
                    Dyn.TrySet(builder, "ShellType", "ShellAllFaces");
                    if (!Dyn.TrySet(builder, "Body", body)) Dyn.SetAny(builder, new string[] { "BodyCollector", "TargetBodyCollector" }, collector);
                }
                Feature feature = (Feature)Dyn.Call(builder, "CommitFeature");
                Dictionary<string, object> d = Nx.FeatureResult(feature);
                d["removed_faces"] = removed;
                if (!d.ContainsKey("body")) Nx.AddBody(d, body);
                return d;
            }
            finally
            {
                Dyn.TryCall(builder, "Destroy");
            }
        }
    }
}
