// tools: get_model_info, list_features, list_expressions
// Read-only summaries of the work part. Lengths in mm, volumes in mm^3.
using System;
using System.Collections.Generic;
using NXOpen;
using Feature = NXOpen.Features.Feature;

namespace NxAiBridge.Handlers
{
    public class ModelInfoTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Part part = Nx.Work();
            if (tool == "list_features") return Features(part);
            if (tool == "list_expressions") return Expressions(part);

            Dictionary<string, object> d = R.Obj("part", part.FullPath, "units", "mm");
            try { d["modified"] = Dyn.Get(part, "IsModified"); } catch (Exception) { }
            List<object> bodies = new List<object>();
            double total = 0;
            bool haveVolume = false;
            double[] box = null;
            foreach (Body b in Nx.Bodies(part))
            {
                Dictionary<string, object> info = Nx.BodyInfo(b);
                bodies.Add(info);
                if (info.ContainsKey("volume")) { total += Convert.ToDouble(info["volume"]); haveVolume = true; }
                try
                {
                    double[] bb = Nx.BBox(b);
                    if (box == null) box = bb;
                    else for (int i = 0; i < 3; i++) { box[i] = Math.Min(box[i], bb[i]); box[i + 3] = Math.Max(box[i + 3], bb[i + 3]); }
                }
                catch (Exception) { }
            }
            d["body_count"] = bodies.Count;
            d["bodies"] = bodies;
            if (box != null) d["bbox"] = R.Nums(box);
            if (haveVolume) d["total_volume"] = R.Round(total);
            if (a.Bool("include_features", true)) d["features"] = Features(part)["features"];
            return d;
        }

        private static Dictionary<string, object> Features(Part part)
        {
            List<object> list = new List<object>();
            foreach (Feature f in part.Features)
            {
                Dictionary<string, object> item = R.Obj("name", f.GetFeatureName(), "type", f.FeatureType);
                try { item["timestamp"] = f.Timestamp; } catch (Exception) { }
                try { item["suppressed"] = f.Suppressed; } catch (Exception) { }
                try
                {
                    Body[] bodies = Nx.FeatureBodies(f);
                    if (bodies.Length > 0) item["body"] = Nx.BodyRef(bodies[0]);
                }
                catch (Exception) { }
                List<object> exprs = Nx.ExpressionNames(f);
                if (exprs.Count > 0) item["expressions"] = exprs;
                list.Add(item);
            }
            return R.Obj("count", list.Count, "features", list);
        }

        private static Dictionary<string, object> Expressions(Part part)
        {
            Dictionary<string, string> owner = new Dictionary<string, string>();
            foreach (Feature f in part.Features)
                foreach (object name in Nx.ExpressionNames(f)) owner[(string)name] = f.GetFeatureName();
            List<object> list = new List<object>();
            foreach (Expression e in part.Expressions)
            {
                Dictionary<string, object> item = R.Obj("name", e.Name);
                try { item["value"] = e.Value; } catch (Exception) { }
                try { item["formula"] = e.RightHandSide; } catch (Exception) { }
                try { object u = Dyn.Get(e, "Units"); item["units"] = u == null ? null : Dyn.Get(u, "Name"); } catch (Exception) { }
                try { item["description"] = Dyn.Get(e, "Description"); } catch (Exception) { }
                if (owner.ContainsKey(e.Name)) item["feature"] = owner[e.Name];
                list.Add(item);
            }
            return R.Obj("count", list.Count, "expressions", list);
        }
    }
}
