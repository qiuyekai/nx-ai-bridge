// tools: delete_feature, set_expression, undo
using System;
using System.Collections.Generic;
using NXOpen;
using Feature = NXOpen.Features.Feature;

namespace NxAiBridge.Handlers
{
    public class ManageTool : ITool
    {
        public object Run(string tool, Args a)
        {
            if (tool == "undo") return R.Obj("undone", NxHooks.Undo(a.Int("steps", 1)));
            Part part = Nx.Work();
            Session s = Nx.S;
            if (tool == "delete_feature")
            {
                string name = a.Str("feature");
                Feature f = Nx.FindFeature(part, name);
                if (f == null) throw new ToolError("NOT_FOUND", "Feature '" + name + "' not found. Features: " + Json.Write(Names(part)), "Use list_features.");
                List<object> before = Names(part);
                Session.UndoMarkId mark = s.SetUndoMark(Session.MarkVisibility.Invisible, "AI delete feature");
                try { Dyn.Call(s.UpdateManager, "AddToDeleteList", f); }
                catch (ApiMismatch) { Dyn.Call(s.UpdateManager, "AddToDeleteList", new object[] { new NXObject[] { f } }); }
                Update(s, mark);
                List<object> after = Names(part);
                List<object> deleted = new List<object>();
                foreach (object n in before) if (!after.Contains(n)) deleted.Add(n);
                return R.Obj("deleted", deleted);
            }

            // set_expression
            string exprName = a.Str("name");
            Expression expr = null;
            List<object> all = new List<object>();
            foreach (Expression e in part.Expressions)
            {
                all.Add(e.Name);
                if (string.Equals(e.Name, exprName, StringComparison.OrdinalIgnoreCase)) expr = e;
            }
            if (expr == null) throw new ToolError("NOT_FOUND", "Expression '" + exprName + "' not found. Expressions: " + Json.Write(all), "Use list_expressions.");
            if (!a.Has("value") && !a.Has("formula")) throw new ToolError("INVALID_PARAMS", "Give `value` or `formula`.");
            string rhs = a.Has("value") ? Nx.Num(a.Num("value")) : a.Str("formula");
            double old = double.NaN;
            try { old = expr.Value; } catch (Exception) { }
            Session.UndoMarkId m = s.SetUndoMark(Session.MarkVisibility.Invisible, "AI edit expression");
            try { Dyn.Call(part.Expressions, "Edit", expr, rhs); }
            catch (ApiMismatch) { expr.RightHandSide = rhs; }
            Update(s, m);
            Dictionary<string, object> d = R.Obj("name", expr.Name, "old_value", double.IsNaN(old) ? null : (object)old, "formula", rhs, "model_updated", true);
            try { d["value"] = expr.Value; } catch (Exception) { }
            return d;
        }

        private static List<object> Names(Part part)
        {
            List<object> names = new List<object>();
            foreach (Feature f in part.Features) names.Add(f.GetFeatureName());
            return names;
        }

        /// <summary>Run the model update; a failing update becomes an error (and the dispatcher rolls back).</summary>
        private static void Update(Session s, Session.UndoMarkId mark)
        {
            int errors = s.UpdateManager.DoUpdate(mark);
            if (errors <= 0) return;
            List<object> details = new List<object>();
            try
            {
                object list = Dyn.Get(s.UpdateManager, "ErrorList"); // UNVERIFIED member names
                int n = Convert.ToInt32(Dyn.Get(list, "Length"));
                for (int i = 0; i < n && i < 10; i++)
                {
                    object info = Dyn.Call(list, "GetErrorInfo", i);
                    details.Add(R.Obj("description", Dyn.Get(info, "Description"), "error_code", Dyn.Get(info, "ErrorCode")));
                }
            }
            catch (Exception) { }
            throw new ToolError("UPDATE_FAILED", "The model update failed with " + errors + " error(s); the change was rolled back.",
                "The new value probably makes a later feature impossible (e.g. a blend larger than the edge).", details);
        }
    }
}
