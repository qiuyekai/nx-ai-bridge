// tools: boolean_unite, boolean_subtract, boolean_intersect
// Booleans between existing bodies (BooleanBuilder, with a UF fallback inside Nx.Boolean).
using System;
using System.Collections.Generic;
using NXOpen;
using Feature = NXOpen.Features.Feature;

namespace NxAiBridge.Handlers
{
    public class BooleanTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Nx.Work();
            Body target = Nx.ResolveBody(a.Str("target"));
            List<Body> tools = new List<Body>();
            List<object> consumed = new List<object>();
            foreach (object item in a.List("tools"))
            {
                if (!(item is string)) throw new ToolError("INVALID_PARAMS", "`tools` must be a list of body names.");
                Body b = Nx.ResolveBody((string)item);
                if (b == target) throw new ToolError("INVALID_PARAMS", "The target body cannot also be a tool body.");
                if (!tools.Contains(b)) { tools.Add(b); consumed.Add(Nx.BodyRef(b)); }
            }
            if (tools.Count == 0) throw new ToolError("INVALID_PARAMS", "`tools` must contain at least one body.");

            List<object> notes = new List<object>();
            Feature f = Nx.Boolean(tool.Substring("boolean_".Length), target, tools.ToArray(), notes);
            Dictionary<string, object> d = f != null ? Nx.FeatureResult(f) : R.Obj("feature", null, "type", tool);
            d["bodies"] = R.List(Nx.BodyRef(target));
            Nx.AddBody(d, target);
            d["consumed"] = consumed;
            if (notes.Count > 0) d["notes"] = notes;
            return d;
        }
    }
}
