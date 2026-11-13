// nx-ai-bridge nxlib - NX helpers shared by all tool handlers.
//
// BLIND-WRITTEN for NX 10 without access to NX. Rules used in this file:
//   * Strongly typed calls are used only for NX Open members the author is certain about
//     (they appear in virtually every recorded journal). A typo there breaks ALL handlers,
//     so keep the typed surface small.
//   * Everything else goes through Dyn (late binding): a wrong name becomes a runtime
//     API_MISMATCH error listing the members that really exist.
//   * Lines marked UNVERIFIED are best guesses that the self-check will confirm or refute.
// C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using NXOpen;
using NXOpen.UF;
using Feature = NXOpen.Features.Feature; // alias: avoids name clashes a blanket "using NXOpen.Features" could cause

namespace NxAiBridge
{
    public class Frame
    {
        public double[] Origin = new double[] { 0, 0, 0 };
        public double[] X = new double[] { 1, 0, 0 };
        public double[] Y = new double[] { 0, 1, 0 };
        public string Mode = "sketch"; // "sketch" = real NX sketch, "curves" = plain curves (fallback)

        public double[] Normal { get { return Vec.Cross(X, Y); } }

        public double[] To3d(double x, double y)
        {
            return new double[] { Origin[0] + X[0] * x + Y[0] * y, Origin[1] + X[1] * x + Y[1] * y, Origin[2] + X[2] * x + Y[2] * y };
        }

        public string Encode()
        {
            List<string> parts = new List<string>();
            foreach (double[] v in new double[][] { Origin, X, Y }) foreach (double d in v) parts.Add(d.ToString("R", CultureInfo.InvariantCulture));
            return Mode + ";" + string.Join(",", parts.ToArray());
        }

        public static Frame Decode(string text)
        {
            string[] halves = text.Split(';');
            string[] n = halves[1].Split(',');
            Frame f = new Frame();
            f.Mode = halves[0];
            for (int i = 0; i < 3; i++)
            {
                f.Origin[i] = double.Parse(n[i], CultureInfo.InvariantCulture);
                f.X[i] = double.Parse(n[3 + i], CultureInfo.InvariantCulture);
                f.Y[i] = double.Parse(n[6 + i], CultureInfo.InvariantCulture);
            }
            return f;
        }
    }

    public static class Vec
    {
        public static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        public static double[] Cross(double[] a, double[] b) { return new double[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        public static double[] Add(double[] a, double[] b) { return new double[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] }; }
        public static double[] Sub(double[] a, double[] b) { return new double[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        public static double[] Mul(double[] a, double s) { return new double[] { a[0] * s, a[1] * s, a[2] * s }; }
        public static double Len(double[] a) { return Math.Sqrt(Dot(a, a)); }

        public static double[] Unit(double[] a)
        {
            double l = Len(a);
            if (l < 1e-12) throw new ToolError("INVALID_PARAMS", "Direction vector has zero length.");
            return Mul(a, 1 / l);
        }

        public static double[] Of(Point3d p) { return new double[] { p.X, p.Y, p.Z }; }
        public static Point3d P(double[] a) { return new Point3d(a[0], a[1], a[2]); }
        public static Vector3d V(double[] a) { return new Vector3d(a[0], a[1], a[2]); }

        /// <summary>Distance from point p to the axis-aligned box [xmin,ymin,zmin,xmax,ymax,zmax].</summary>
        public static double DistToBox(double[] p, double[] box)
        {
            double s = 0;
            for (int i = 0; i < 3; i++)
            {
                double d = Math.Max(Math.Max(box[i] - p[i], 0), p[i] - box[i + 3]);
                s += d * d;
            }
            return Math.Sqrt(s);
        }

        public static double DistToSegment(double[] p, double[] a, double[] b)
        {
            double[] ab = Sub(b, a);
            double l2 = Dot(ab, ab);
            double t = l2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, Dot(Sub(p, a), ab) / l2));
            return Len(Sub(p, Add(a, Mul(ab, t))));
        }
    }

    public class FaceData
    {
        public int Type;
        public double[] Point = new double[3];
        public double[] Dir = new double[3];
        public double[] Box = new double[6];
        public double Radius;
        public int NormDir;
    }

    public static class Nx
    {
        public static Session S { get { return Session.GetSession(); } }
        public static UFSession UF { get { return UFSession.GetUFSession(); } }

        public static Part Work()
        {
            Part part = S.Parts.Work;
            if (part == null) throw new ToolError("NO_WORK_PART", "There is no work part.", "Call new_part or open_part first.");
            return part;
        }

        /// <summary>Number formatting for NX expressions (always '.' as decimal separator).</summary>
        public static string Num(double v)
        {
            return v.ToString("0.############", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ bodies
        public static List<Body> Bodies(Part part)
        {
            List<Body> list = new List<Body>();
            foreach (Body b in part.Bodies) list.Add(b);
            return list;
        }

        /// <summary>Stable, human readable reference of a body: its NX object name (assigned on first use: B1, B2, ...).</summary>
        public static string BodyRef(Body body)
        {
            string name = body.Name;
            if (!string.IsNullOrEmpty(name)) return name;
            int max = 0;
            foreach (Body b in Bodies(Work()))
            {
                string n = b.Name;
                int k;
                if (!string.IsNullOrEmpty(n) && n.Length > 1 && (n[0] == 'B' || n[0] == 'b') && int.TryParse(n.Substring(1), out k)) max = Math.Max(max, k);
            }
            name = "B" + (max + 1);
            body.SetName(name);
            return name;
        }

        public static List<object> BodyNames(Part part)
        {
            List<object> names = new List<object>();
            foreach (Body b in Bodies(part)) names.Add(BodyRef(b));
            return names;
        }

        public static Feature FindFeature(Part part, string name)
        {
            foreach (Feature f in part.Features)
            {
                if (string.Equals(f.GetFeatureName(), name, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return null;
        }

        public static Body[] FeatureBodies(Feature f)
        {
            try
            {
                Body[] bodies = Dyn.Call(f, "GetBodies") as Body[];
                if (bodies != null && bodies.Length > 0) return bodies;
            }
            catch (Exception) { }
            try
            {
                // UNVERIFIED fallback: UF_MODL_ask_feat_body
                object[] a = new object[] { f.Tag, Tag.Null };
                Dyn.Call(Dyn.Get(UF, "Modl"), "AskFeatBody", a);
                Body b = NXOpen.Utilities.NXObjectManager.Get((Tag)a[1]) as Body;
                if (b != null) return new Body[] { b };
            }
            catch (Exception) { }
            return new Body[0];
        }

        /// <summary>Resolve a body reference: body name, feature name, or null/empty = the only body of the part.</summary>
        public static Body ResolveBody(string reference)
        {
            Part part = Work();
            List<Body> bodies = Bodies(part);
            if (string.IsNullOrEmpty(reference))
            {
                if (bodies.Count == 1) return bodies[0];
                if (bodies.Count == 0) throw new ToolError("NOT_FOUND", "The work part has no body.");
                throw new ToolError("INVALID_PARAMS", "The part has " + bodies.Count + " bodies; specify which one. Bodies: " + Json.Write(BodyNames(part)));
            }
            foreach (Body b in bodies) if (string.Equals(b.Name, reference, StringComparison.OrdinalIgnoreCase)) return b;
            Feature f = FindFeature(part, reference);
            if (f != null)
            {
                Body[] fb = FeatureBodies(f);
                if (fb.Length > 0) return fb[0];
            }
            throw new ToolError("NOT_FOUND", "Body '" + reference + "' not found. Bodies: " + Json.Write(BodyNames(part)), "Use get_model_info to list bodies.");
        }

        public static double[] BBox(Body body)
        {
            double[] box = new double[6];
            UF.Modl.AskBoundingBox(body.Tag, box);
            return box;
        }

        /// <summary>Returns { volume mm^3, area mm^2, cx, cy, cz } for the given bodies.</summary>
        public static double[] MassProps(Body[] bodies)
        {
            Exception first = null;
            try
            {
                // UF_MODL_ask_mass_props_3d, units 3 = grams and centimetres
                Tag[] tags = new Tag[bodies.Length];
                for (int i = 0; i < bodies.Length; i++) tags[i] = bodies[i].Tag;
                double[] acc = new double[11];
                acc[0] = 0.99;
                double[] props = new double[47];
                double[] stats = new double[13];
                object[] args = new object[] { tags, tags.Length, 1, 3, 1.0, 1, acc, props, stats };
                Dyn.Call(Dyn.Get(UF, "Modl"), "AskMassProps3d", args);
                props = (double[])args[7]; // works whether the wrapper fills the array or declares it "out"
                return new double[] { props[1] * 1000.0, props[0] * 100.0, props[3] * 10.0, props[4] * 10.0, props[5] * 10.0 };
            }
            catch (Exception ex) { first = ex; }
            try
            {
                // fallback: MeasureManager (base units of a millimetre part are mm, mm^2, mm^3)
                Part part = Work();
                object uc = Dyn.Get(part, "UnitCollection");
                Unit[] units = new Unit[5];
                units[0] = (Unit)Dyn.Call(uc, "GetBase", "Area");
                units[1] = (Unit)Dyn.Call(uc, "GetBase", "Volume");
                units[2] = (Unit)Dyn.Call(uc, "GetBase", "Mass");
                units[3] = (Unit)Dyn.Call(uc, "GetBase", "Length");
                units[4] = Dyn.TryCall(uc, "GetBase", "Force") as Unit;
                IBody[] ib = new IBody[bodies.Length];
                for (int i = 0; i < bodies.Length; i++) ib[i] = bodies[i];
                object mb = Dyn.Call(Dyn.Get(part, "MeasureManager"), "NewMassProperties", units, 0.99, ib);
                Point3d c = (Point3d)Dyn.Get(mb, "Centroid");
                return new double[] { Convert.ToDouble(Dyn.Get(mb, "Volume")), Convert.ToDouble(Dyn.Get(mb, "Area")), c.X, c.Y, c.Z };
            }
            catch (Exception) { }
            throw first;
        }

        public static Dictionary<string, object> BodyInfo(Body body)
        {
            Dictionary<string, object> d = R.Obj("name", BodyRef(body));
            try { d["solid"] = body.IsSolidBody; } catch (Exception) { }
            try
            {
                double[] box = BBox(body);
                d["bbox"] = R.Nums(box);
                d["size"] = R.Nums(box[3] - box[0], box[4] - box[1], box[5] - box[2]);
            }
            catch (Exception ex) { d["bbox_error"] = ex.Message; }
            try
            {
                double[] m = MassProps(new Body[] { body });
                d["volume"] = R.Round(m[0]);
                d["area"] = R.Round(m[1]);
                d["centroid"] = R.Nums(m[2], m[3], m[4]);
            }
            catch (Exception ex) { d["volume_error"] = Dispatcher.Unwrap(ex).Message; }
            return d;
        }

        /// <summary>Standard result of a modelling tool: feature name, body, volume and bounding box.</summary>
        public static Dictionary<string, object> FeatureResult(Feature f)
        {
            Dictionary<string, object> d = R.Obj("feature", f.GetFeatureName(), "type", f.FeatureType);
            Body[] bodies = FeatureBodies(f);
            List<object> names = new List<object>();
            foreach (Body b in bodies) names.Add(BodyRef(b));
            d["bodies"] = names;
            if (bodies.Length > 0) AddBody(d, bodies[0]);
            List<object> exprs = ExpressionNames(f);
            if (exprs.Count > 0) d["expressions"] = exprs;
            return d;
        }

        public static void AddBody(Dictionary<string, object> d, Body body)
        {
            Dictionary<string, object> info = BodyInfo(body);
            d["body"] = info["name"];
            foreach (string key in new string[] { "volume", "bbox", "volume_error", "bbox_error" })
                if (info.ContainsKey(key)) d[key] = info[key];
        }

        public static List<object> ExpressionNames(Feature f)
        {
            List<object> names = new List<object>();
            try
            {
                foreach (Expression e in f.GetExpressions()) names.Add(e.Name);
            }
            catch (Exception) { }
            return names;
        }

        // ------------------------------------------------------------------ geometry builders
        public static Direction MakeDirection(Part part, double[] origin, double[] vector)
        {
            return part.Directions.CreateDirection(Vec.P(origin), Vec.V(Vec.Unit(vector)), SmartObject.UpdateOption.WithinModeling);
        }

        public static object MakeAxis(Part part, double[] origin, double[] vector)
        {
            Point point = part.Points.CreatePoint(Vec.P(origin));
            Direction dir = MakeDirection(part, origin, vector);
            return Dyn.Call(part.Axes, "CreateAxis", point, dir, SmartObject.UpdateOption.WithinModeling);
        }

        /// <summary>Apply the "boolean"/"target" parameters to a feature builder that has a BooleanOption/BooleanOperation member.</summary>
        public static void SetBuilderBoolean(object builder, Args a)
        {
            string op = a.Str("boolean", "none");
            string member = Dyn.Has(builder, "BooleanOption") ? "BooleanOption" : "BooleanOperation";
            object bo = Dyn.Get(builder, member);
            if (op == "none") { Dyn.Set(bo, "Type", "Create"); return; }
            string type = op == "unite" ? "Unite" : op == "subtract" ? "Subtract" : op == "intersect" ? "Intersect" : null;
            if (type == null) throw new ToolError("INVALID_PARAMS", "boolean must be none, unite, subtract or intersect.");
            Body target = ResolveBody(a.Str("target", null));
            Dyn.Set(bo, "Type", type);
            Dyn.Call(bo, "SetTargetBodies", new object[] { new Body[] { target } });
        }

        /// <summary>Boolean between existing bodies. op: unite | subtract | intersect. Returns the boolean feature (may be null with the UF fallback).</summary>
        public static Feature Boolean(string op, Body target, Body[] tools, List<object> notes)
        {
            Part part = Work();
            string type = op == "unite" ? "Unite" : op == "subtract" ? "Subtract" : "Intersect";
            Exception first = null;
            object builder = null;
            try
            {
                builder = Dyn.Call(part.Features, "CreateBooleanBuilderUsingCollector", new object[] { null });
                Dyn.TrySet(builder, "Tolerance", 0.01);
                Dyn.Set(builder, "Operation", type);
                Dyn.Call(Dyn.Get(builder, "Targets"), "Add", target);
                object collector = Dyn.Call(part.ScCollectors, "CreateCollector");
                object rule;
                try { rule = Dyn.Call(part.ScRuleFactory, "CreateRuleBodyDumb", tools, true); }
                catch (ApiMismatch) { rule = Dyn.Call(part.ScRuleFactory, "CreateRuleBodyDumb", new object[] { tools }); }
                Dyn.Call(collector, "ReplaceRules", Dyn.MakeArray("NXOpen.SelectionIntentRule", rule), false);
                Dyn.Set(builder, "ToolBodyCollector", collector);
                object committed = Dyn.Has(builder, "CommitFeature") ? Dyn.Call(builder, "CommitFeature") : Dyn.Call(builder, "Commit");
                Dyn.TryCall(builder, "Destroy");
                return committed as Feature;
            }
            catch (Exception ex)
            {
                first = Dispatcher.Unwrap(ex);
                if (builder != null) Dyn.TryCall(builder, "Destroy");
                if (!(first is ApiMismatch)) throw; // a real NX error (e.g. tool outside target): do not retry
            }
            // UNVERIFIED fallback: UF_MODL_*_bodies_with_retained_options
            if (notes != null) notes.Add("BooleanBuilder failed (" + first.Message + "); used UF fallback");
            string fn = type + "BodiesWithRetainedOptions";
            object modl = Dyn.Get(UF, "Modl");
            Tag result = Tag.Null;
            foreach (Body tool in tools)
            {
                object[] args = new object[] { target.Tag, false, tool.Tag, false, Tag.Null };
                Dyn.Call(modl, fn, args);
                result = (Tag)args[4];
            }
            return NXOpen.Utilities.NXObjectManager.Get(result) as Feature;
        }

        /// <summary>Create a cylinder body (no boolean). Returns the feature.</summary>
        public static Feature CreateCylinder(double[] origin, double[] direction, double diameter, double height, List<object> notes)
        {
            Part part = Work();
            Exception first = null;
            object builder = null;
            try
            {
                builder = Dyn.Call(part.Features, "CreateCylinderBuilder", new object[] { null });
                Dyn.SetPath(builder, "BooleanOption.Type", "Create");
                Dyn.Set(builder, "Type", "AxisDiameterAndHeight");
                Dyn.SetPath(builder, "Diameter.RightHandSide", Num(diameter));
                Dyn.SetPath(builder, "Height.RightHandSide", Num(height));
                Dyn.Set(builder, "Axis", MakeAxis(part, origin, direction));
                Feature f = (Feature)Dyn.Call(builder, "CommitFeature");
                Dyn.TryCall(builder, "Destroy");
                return f;
            }
            catch (Exception ex)
            {
                first = Dispatcher.Unwrap(ex);
                if (builder != null) Dyn.TryCall(builder, "Destroy");
                if (!(first is ApiMismatch)) throw;
            }
            // fallback: UF_MODL_create_cyl1
            if (notes != null) notes.Add("CylinderBuilder failed (" + first.Message + "); used UF_MODL_create_cyl1");
            object[] args = new object[] { Dyn.Enum("NXOpen.UF.FeatureSigns", "Nullsign"), origin, Num(height), Num(diameter), Vec.Unit(direction), Tag.Null };
            Dyn.Call(Dyn.Get(UF, "Modl"), "CreateCyl1", args);
            return (Feature)NXOpen.Utilities.NXObjectManager.Get((Tag)args[5]);
        }

        // ------------------------------------------------------------------ edges and faces
        /// <summary>Point at the middle (parameter 0.5) of a curve or edge. UF_MODL_ask_curve_props.</summary>
        public static double[] PointOnCurve(Tag curveOrEdge)
        {
            object[] a = new object[] { curveOrEdge, 0.5, new double[3], new double[3], new double[3], new double[3], 0.0, 0.0 };
            Dyn.Call(Dyn.Get(UF, "Modl"), "AskCurveProps", a);
            return (double[])a[2];
        }

        public static double[] EdgeMid(Edge e)
        {
            try { return PointOnCurve(e.Tag); }
            catch (Exception) { }
            Point3d p1, p2;
            e.GetVertices(out p1, out p2);
            return Vec.Mul(Vec.Add(Vec.Of(p1), Vec.Of(p2)), 0.5);
        }

        public static Dictionary<string, object> EdgeInfo(string bodyRef, int index, Edge e)
        {
            Dictionary<string, object> d = R.Obj("id", "E:" + bodyRef + ":" + index, "body", bodyRef);
            try { d["type"] = e.SolidEdgeType.ToString().ToLowerInvariant(); } catch (Exception) { }
            try { d["length"] = R.Round(e.GetLength()); } catch (Exception) { }
            try
            {
                Point3d p1, p2;
                e.GetVertices(out p1, out p2);
                d["start"] = R.Nums(p1.X, p1.Y, p1.Z);
                d["end"] = R.Nums(p2.X, p2.Y, p2.Z);
            }
            catch (Exception) { }
            try { d["midpoint"] = R.Nums(EdgeMid(e)); } catch (Exception) { }
            return d;
        }

        private static double EdgeDistance(Edge e, double[] p)
        {
            double best = double.MaxValue;
            try { best = Vec.Len(Vec.Sub(EdgeMid(e), p)); } catch (Exception) { }
            try
            {
                Point3d p1, p2;
                e.GetVertices(out p1, out p2);
                if (e.SolidEdgeType == Edge.EdgeType.Linear) best = Math.Min(best, Vec.DistToSegment(p, Vec.Of(p1), Vec.Of(p2)));
            }
            catch (Exception) { }
            return best;
        }

        private static void ParseId(string id, char kind, out Body body, out int index)
        {
            // "E:<body>:<index>" - the body name itself may contain ':' only in theory; split from both ends
            int first = id.IndexOf(':');
            int last = id.LastIndexOf(':');
            if (id.Length < 5 || char.ToUpperInvariant(id[0]) != kind || first != 1 || last <= first || !int.TryParse(id.Substring(last + 1), out index))
                throw new ToolError("INVALID_PARAMS", "Bad id '" + id + "' (expected '" + kind + ":<body>:<index>').");
            body = ResolveBody(id.Substring(first + 1, last - first - 1));
        }

        public static Edge ResolveEdge(object selector)
        {
            string id = selector as string;
            if (id != null)
            {
                Body body;
                int index;
                ParseId(id, 'E', out body, out index);
                Edge[] edges = body.GetEdges();
                if (index < 0 || index >= edges.Length) throw new ToolError("NOT_FOUND", "Edge '" + id + "' does not exist; the body has " + edges.Length + " edges.", "Edge ids change when the body changes: call list_edges again.");
                return edges[index];
            }
            Dictionary<string, object> map = selector as Dictionary<string, object>;
            if (map == null || !map.ContainsKey("near")) throw new ToolError("INVALID_PARAMS", "An edge selector must be an id string or {\"near\": [x, y, z]}.");
            Args a = new Args(map);
            double[] p = a.Vec("near", 3);
            List<Body> bodies = a.Has("body") ? new List<Body>(new Body[] { ResolveBody(a.Str("body")) }) : Bodies(Work());
            Edge best = null;
            double bestDist = double.MaxValue;
            foreach (Body b in bodies)
            {
                foreach (Edge e in b.GetEdges())
                {
                    double dist = EdgeDistance(e, p);
                    if (dist < bestDist) { bestDist = dist; best = e; }
                }
            }
            if (best == null) throw new ToolError("NOT_FOUND", "No edges found.");
            return best;
        }

        /// <summary>Edges from the "edges" / "all_edges_of_body" parameters.</summary>
        public static Edge[] ResolveEdges(Args a)
        {
            if (a.Has("all_edges_of_body")) return ResolveBody(a.Str("all_edges_of_body")).GetEdges();
            List<object> selectors = a.List("edges");
            if (selectors.Count == 0) throw new ToolError("INVALID_PARAMS", "Give `edges` or `all_edges_of_body`.", "Call list_edges to see the edges of a body.");
            List<Edge> edges = new List<Edge>();
            foreach (object sel in selectors)
            {
                Edge e = ResolveEdge(sel);
                if (!edges.Contains(e)) edges.Add(e);
            }
            return edges.ToArray();
        }

        public static FaceData AskFace(Face f)
        {
            // UF_MODL_ask_face_data(face, &type, point[3], dir[3], box[6], &radius, &rad_data, &norm_dir)
            object[] a = new object[] { f.Tag, 0, new double[3], new double[3], new double[6], 0.0, 0.0, 0 };
            Dyn.Call(Dyn.Get(UF, "Modl"), "AskFaceData", a);
            FaceData d = new FaceData();
            d.Type = Convert.ToInt32(a[1]);
            d.Point = (double[])a[2];
            d.Dir = (double[])a[3];
            d.Box = (double[])a[4];
            d.Radius = Convert.ToDouble(a[5]);
            d.NormDir = Convert.ToInt32(a[7]);
            return d;
        }

        public static Dictionary<string, object> FaceInfo(string bodyRef, int index, Face f)
        {
            Dictionary<string, object> d = R.Obj("id", "F:" + bodyRef + ":" + index, "body", bodyRef);
            try { d["type"] = f.SolidFaceType.ToString().ToLowerInvariant(); } catch (Exception) { }
            try
            {
                FaceData fd = AskFace(f);
                d["point"] = R.Nums(fd.Point);
                // for planar faces `direction` is the plane normal, for cylinders/cones the axis
                d["direction"] = R.Nums(fd.Dir);
                d["bbox"] = R.Nums(fd.Box);
                if (fd.Radius != 0) d["radius"] = R.Round(fd.Radius);
            }
            catch (Exception ex) { d["data_error"] = Dispatcher.Unwrap(ex).Message; }
            return d;
        }

        public static Face ResolveFace(object selector, string defaultBody)
        {
            string id = selector as string;
            if (id != null)
            {
                Body body;
                int index;
                ParseId(id, 'F', out body, out index);
                Face[] faces = body.GetFaces();
                if (index < 0 || index >= faces.Length) throw new ToolError("NOT_FOUND", "Face '" + id + "' does not exist; the body has " + faces.Length + " faces.", "Face ids change when the body changes: call list_faces again.");
                return faces[index];
            }
            Dictionary<string, object> map = selector as Dictionary<string, object>;
            if (map == null || !(map.ContainsKey("near") || map.ContainsKey("normal")))
                throw new ToolError("INVALID_PARAMS", "A face selector must be an id string, {\"near\": [x, y, z]} or {\"normal\": [x, y, z]}.");
            Args a = new Args(map);
            string bodyRef = a.Str("body", defaultBody);
            List<Body> bodies = !string.IsNullOrEmpty(bodyRef) ? new List<Body>(new Body[] { ResolveBody(bodyRef) }) : Bodies(Work());
            Face best = null;
            double bestScore = double.MaxValue;
            double[] near = a.Has("near") ? a.Vec("near", 3) : null;
            double[] normal = a.Has("normal") ? Vec.Unit(a.Vec("normal", 3)) : null;
            foreach (Body b in bodies)
            {
                foreach (Face f in b.GetFaces())
                {
                    FaceData fd;
                    try { fd = AskFace(f); } catch (ApiMismatch) { throw; } catch (Exception) { continue; }
                    double score;
                    if (near != null) score = Vec.DistToBox(near, fd.Box);
                    else
                    {
                        // the outermost planar face perpendicular to `normal` necessarily faces outward
                        if (f.SolidFaceType != Face.FaceType.Planar || Math.Abs(Vec.Dot(fd.Dir, normal)) < 0.999) continue;
                        score = -Vec.Dot(fd.Point, normal);
                    }
                    if (score < bestScore) { bestScore = score; best = f; }
                }
            }
            if (best == null) throw new ToolError("NOT_FOUND", normal != null ? "No planar face with that normal." : "No faces found.", "Call list_faces to see the faces of a body.");
            return best;
        }

        // ------------------------------------------------------------------ sketch bookkeeping
        private static readonly Dictionary<string, string> FrameMemory = new Dictionary<string, string>();

        private static string FrameKey(Part part, string sketch) { return part.Tag + "/" + sketch.ToUpperInvariant(); }

        public static void SaveFrame(Part part, string sketch, Frame frame)
        {
            string title = "NXAI_" + sketch.ToUpperInvariant();
            string value = frame.Encode();
            FrameMemory[FrameKey(part, sketch)] = value;
            // persist in the part file so the sketch survives save/open (needed for batch mode)
            try { Dyn.Call(part, "SetUserAttribute", title, -1, value, Dyn.Enum("NXOpen.Update+Option", "Now")); }
            catch (Exception)
            {
                try { Dyn.Call(part, "SetAttribute", title, value); } catch (Exception) { }
            }
        }

        public static Frame LoadFrame(Part part, string sketch)
        {
            string value;
            if (FrameMemory.TryGetValue(FrameKey(part, sketch), out value)) return Frame.Decode(value);
            string title = "NXAI_" + sketch.ToUpperInvariant();
            try { value = Dyn.Call(part, "GetStringAttribute", title) as string; } catch (Exception) { value = null; }
            if (string.IsNullOrEmpty(value))
            {
                try { value = Dyn.Call(part, "GetUserAttributeAsString", title, Dyn.Enum("NXOpen.NXObject+AttributeType", "String"), -1) as string; }
                catch (Exception) { value = null; }
            }
            if (string.IsNullOrEmpty(value))
                throw new ToolError("NOT_FOUND", "Sketch '" + sketch + "' not found.", "Sketch names are returned by create_sketch.");
            return Frame.Decode(value);
        }

        /// <summary>All curves that the sketch_* tools added to the sketch (they are named "<SKETCH>_C<n>").</summary>
        public static List<Curve> SketchCurves(Part part, string sketch)
        {
            string prefix = sketch.ToUpperInvariant() + "_C";
            List<Curve> curves = new List<Curve>();
            foreach (Curve c in part.Curves)
            {
                string n = c.Name;
                if (!string.IsNullOrEmpty(n) && n.ToUpperInvariant().StartsWith(prefix)) curves.Add(c);
            }
            return curves;
        }
    }

    /// <summary>Wraps every modifying tool call in a visible NX undo mark; failures are rolled back.</summary>
    public class NxHooks : ISessionHooks
    {
        private static readonly List<Session.UndoMarkId> Marks = new List<Session.UndoMarkId>();
        private static readonly List<string> NoMark = new List<string>(new string[] {
            "nx_status", "get_model_info", "nx_api_lookup", "list_features", "list_expressions", "list_edges", "list_faces",
            "export_step", "export_stl", "screenshot", "new_part", "open_part", "close_part", "save_part", "save_part_as", "undo" });
        private static readonly List<string> ResetsMarks = new List<string>(new string[] { "new_part", "open_part", "close_part" });

        public object Begin(string tool)
        {
            if (NoMark.Contains(tool)) return null;
            if (Nx.S.Parts.Work == null) return null;
            return Nx.S.SetUndoMark(Session.MarkVisibility.Visible, "AI: " + tool);
        }

        public void Commit(string tool, object token)
        {
            if (ResetsMarks.Contains(tool)) Marks.Clear();
            if (token != null) Marks.Add((Session.UndoMarkId)token);
        }

        public void Rollback(string tool, object token)
        {
            if (token == null) return;
            Session.UndoMarkId mark = (Session.UndoMarkId)token;
            Nx.S.UndoToMark(mark, null);
            Nx.S.DeleteUndoMark(mark, null);
        }

        /// <summary>Undo the last `steps` successful tool calls. Returns how many were undone.</summary>
        public static int Undo(int steps)
        {
            if (steps < 1) throw new ToolError("INVALID_PARAMS", "steps must be >= 1");
            if (Marks.Count == 0)
            {
                // marks from an earlier receiver run are unknown: fall back to NX's own undo list (UNVERIFIED member name)
                try { Dyn.Call(Nx.S, "UndoLastNVisibleMarks", steps); return steps; }
                catch (Exception) { throw new ToolError("NOTHING_TO_UNDO", "Nothing to undo."); }
            }
            int n = Math.Min(steps, Marks.Count);
            Session.UndoMarkId mark = Marks[Marks.Count - n];
            Nx.S.UndoToMark(mark, null);
            Marks.RemoveRange(Marks.Count - n, n);
            return n;
        }
    }
}
