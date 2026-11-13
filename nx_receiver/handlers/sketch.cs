// tools: create_sketch, sketch_line, sketch_circle, sketch_rectangle, sketch_arc
// Sketches. Coordinates are 2D in the sketch frame (mm); angles in degrees.
//
// Design: every curve added by sketch_* is an ordinary NX curve named "<SKETCH>_C<n>"; extrude
// and revolve collect the curves by that name. The sketch frame (origin, x, y) is stored as a
// part attribute "NXAI_<SKETCH>". On top of that we TRY to create a real NX sketch feature with
// the UF_SKET functions and add the curves to it. If that fails (UNVERIFIED API guesses), the
// tools silently continue in "curves" mode - modelling still works, only the feature tree differs.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class SketchTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Part part = Nx.Work();
            if (tool == "create_sketch") return Create(part, a);

            string name = a.Str("sketch").ToUpperInvariant();
            Frame fr = Nx.LoadFrame(part, name);
            List<double[][]> lines = new List<double[][]>();
            double[] arc = null; // cx, cy, r, a0, a1 (radians)
            if (tool == "sketch_line")
            {
                double[] s = a.Vec("start", 2), e = a.Vec("end", 2);
                if (Math.Abs(s[0] - e[0]) < 1e-9 && Math.Abs(s[1] - e[1]) < 1e-9) throw new ToolError("INVALID_PARAMS", "Line has zero length.");
                lines.Add(new double[][] { s, e });
            }
            else if (tool == "sketch_rectangle")
            {
                double[] c1 = a.Vec("corner1", 2), c2 = a.Vec("corner2", 2);
                if (Math.Abs(c1[0] - c2[0]) < 1e-9 || Math.Abs(c1[1] - c2[1]) < 1e-9) throw new ToolError("INVALID_PARAMS", "Rectangle has zero width or height.");
                double[][] p = new double[][] { c1, new double[] { c2[0], c1[1] }, c2, new double[] { c1[0], c2[1] } };
                for (int i = 0; i < 4; i++) lines.Add(new double[][] { p[i], p[(i + 1) % 4] });
            }
            else if (tool == "sketch_circle")
            {
                double[] c = a.Vec("center", 2);
                arc = new double[] { c[0], c[1], a.Num("radius"), 0, 2 * Math.PI };
            }
            else
            {
                double[] c = a.Vec("center", 2);
                double a0 = a.Num("start_angle"), a1 = a.Num("end_angle");
                while (a1 <= a0) a1 += 360;
                if (a1 - a0 > 360) throw new ToolError("INVALID_PARAMS", "Arc spans more than 360 degrees.");
                arc = new double[] { c[0], c[1], a.Num("radius"), a0 * Math.PI / 180, a1 * Math.PI / 180 };
            }

            int next = Nx.SketchCurves(part, name).Count + 1;
            List<object> notes = new List<object>();
            object sket = null;
            Tag sketchTag = Tag.Null;
            bool active = false;
            if (fr.Mode == "sketch")
            {
                try
                {
                    sket = Dyn.Get(Nx.UF, "Sket");
                    object[] init = new object[] { name, Tag.Null };
                    Dyn.Call(sket, "InitializeSketch", init); // re-opens the existing sketch by name
                    sketchTag = (Tag)init[1];
                    active = true;
                }
                catch (Exception ex) { notes.Add("could not activate the NX sketch (" + Dispatcher.Unwrap(ex).Message + "); curve created outside the sketch"); }
            }
            List<Tag> tags = new List<Tag>();
            try
            {
                foreach (double[][] seg in lines)
                {
                    Line line = part.Curves.CreateLine(Vec.P(fr.To3d(seg[0][0], seg[0][1])), Vec.P(fr.To3d(seg[1][0], seg[1][1])));
                    line.SetName(name + "_C" + (next++));
                    tags.Add(line.Tag);
                }
                if (arc != null)
                {
                    double[] n = fr.Normal;
                    Matrix3x3 m = new Matrix3x3();
                    m.Xx = fr.X[0]; m.Xy = fr.X[1]; m.Xz = fr.X[2];
                    m.Yx = fr.Y[0]; m.Yy = fr.Y[1]; m.Yz = fr.Y[2];
                    m.Zx = n[0]; m.Zy = n[1]; m.Zz = n[2];
                    NXMatrix orientation = part.NXMatrices.Create(m);
                    Arc curve = part.Curves.CreateArc(Vec.P(fr.To3d(arc[0], arc[1])), orientation, arc[2], arc[3], arc[4]);
                    curve.SetName(name + "_C" + (next++));
                    tags.Add(curve.Tag);
                }
                if (active)
                {
                    try
                    {
                        Dyn.Call(sket, "AddObjects", sketchTag, tags.Count, tags.ToArray());
                        Dyn.TryCall(sket, "UpdateSketch", sketchTag);
                    }
                    catch (Exception ex) { notes.Add("curve not added to the NX sketch: " + Dispatcher.Unwrap(ex).Message); }
                }
            }
            finally
            {
                if (active) Dyn.TryCall(sket, "TerminateSketch");
            }
            Dictionary<string, object> d = R.Obj("sketch", name, "curve_count", next - 1, "added", tool.Substring(7), "mode", fr.Mode);
            if (notes.Count > 0) d["notes"] = notes;
            return d;
        }

        private static object Create(Part part, Args a)
        {
            Frame fr = new Frame();
            fr.Origin = a.Vec("origin", 3, new double[] { 0, 0, 0 });
            if (a.Has("face"))
            {
                Face face = Nx.ResolveFace(a.Raw("face"), null);
                FaceData fd = Nx.AskFace(face);
                double[] n = Vec.Unit(fd.Dir);
                if (fd.NormDir < 0) n = Vec.Mul(n, -1); // UNVERIFIED: norm_dir = -1 means the face normal opposes `dir`
                fr.X = Math.Abs(n[2]) < 0.99 ? Vec.Unit(Vec.Cross(new double[] { 0, 0, 1 }, n)) : new double[] { 1, 0, 0 };
                fr.Y = Vec.Cross(n, fr.X);
                fr.Origin = fd.Point;
            }
            else if (a.Has("x_axis") && a.Has("normal"))
            {
                double[] n = Vec.Unit(a.Vec("normal", 3));
                double[] x = a.Vec("x_axis", 3);
                x = Vec.Unit(Vec.Sub(x, Vec.Mul(n, Vec.Dot(x, n)))); // make x perpendicular to the normal
                fr.X = x;
                fr.Y = Vec.Cross(n, x);
            }
            else
            {
                string plane = a.Str("plane", "XY").ToUpperInvariant();
                if (plane == "XY") { fr.X = new double[] { 1, 0, 0 }; fr.Y = new double[] { 0, 1, 0 }; }
                else if (plane == "XZ") { fr.X = new double[] { 1, 0, 0 }; fr.Y = new double[] { 0, 0, 1 }; }
                else if (plane == "YZ") { fr.X = new double[] { 0, 1, 0 }; fr.Y = new double[] { 0, 0, 1 }; }
                else throw new ToolError("INVALID_PARAMS", "plane must be XY, XZ or YZ.");
            }

            string name = a.Str("name", null);
            if (name == null)
            {
                for (int i = 1; ; i++)
                {
                    name = "SK" + i;
                    try { Nx.LoadFrame(part, name); } catch (ToolError) { break; }
                }
            }
            else
            {
                name = name.ToUpperInvariant();
                if (!Regex.IsMatch(name, "^[A-Z0-9_]{1,24}$")) throw new ToolError("INVALID_PARAMS", "Sketch name may only contain letters, digits and underscore (max 24).");
                bool exists = true;
                try { Nx.LoadFrame(part, name); } catch (ToolError) { exists = false; }
                if (exists) throw new ToolError("INVALID_PARAMS", "Sketch '" + name + "' already exists.");
            }

            Dictionary<string, object> d = R.Obj("sketch", name);
            try
            {
                // UNVERIFIED: UF_SKET_initialize_sketch + UF_SKET_create_sketch(option 2 = by csys;
                // matrix = x axis, y axis, origin) + UF_SKET_terminate_sketch
                object sket = Dyn.Get(Nx.UF, "Sket");
                object[] init = new object[] { name, Tag.Null };
                Dyn.Call(sket, "InitializeSketch", init);
                try
                {
                    double[] matrix = new double[] { fr.X[0], fr.X[1], fr.X[2], fr.Y[0], fr.Y[1], fr.Y[2], fr.Origin[0], fr.Origin[1], fr.Origin[2] };
                    object[] create = new object[] { name, 2, matrix, new Tag[2], new int[2], 1, Tag.Null };
                    Dyn.Call(sket, "CreateSketch", create);
                }
                finally { Dyn.TryCall(sket, "TerminateSketch"); }
                fr.Mode = "sketch";
            }
            catch (Exception ex)
            {
                fr.Mode = "curves";
                d["note"] = "No NX sketch feature was created (" + Dispatcher.Unwrap(ex).Message + "); using plain curves instead - extrude and revolve work the same.";
            }
            Nx.SaveFrame(part, name, fr);
            d["mode"] = fr.Mode;
            d["origin"] = R.Nums(fr.Origin);
            d["x_axis"] = R.Nums(fr.X);
            d["y_axis"] = R.Nums(fr.Y);
            d["normal"] = R.Nums(fr.Normal);
            return d;
        }
    }
}
