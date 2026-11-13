// A tiny STAND-IN for the NX Open assemblies, used only by CI.
//
// It contains no Siemens code: just empty declarations of the few NX Open members that
// nx-ai-bridge references with static typing, written from the public API documentation.
// It lets CI (a) compile every receiver source with the real .NET Framework C# 5 compiler and
// (b) run the kernel end-to-end without NX. It proves nothing about NX's real behaviour.
using System;
using System.Collections;
using System.Collections.Generic;

namespace NXOpen
{
    public enum Tag : uint { Null = 0 }

    public class NXException : ApplicationException
    {
        public int ErrorCode;
        public NXException(int code, string message) : base(message) { ErrorCode = code; }
    }

    public struct Point3d { public double X, Y, Z; public Point3d(double x, double y, double z) { X = x; Y = y; Z = z; } }
    public struct Vector3d { public double X, Y, Z; public Vector3d(double x, double y, double z) { X = x; Y = y; Z = z; } }
    public struct Matrix3x3 { public double Xx, Xy, Xz, Yx, Yy, Yz, Zx, Zy, Zz; }

    public class TaggedObject { public Tag Tag { get { return Tag.Null; } } }
    public class NXObject : TaggedObject
    {
        private string name = "";
        public string Name { get { return name; } }
        public void SetName(string value) { name = value; }
    }
    public class SmartObject : NXObject { public enum UpdateOption { DontUpdate, WithinModeling, AfterModeling } }
    public class Point : SmartObject { }
    public class Direction : SmartObject { }
    public class NXMatrix : NXObject { }
    public class Unit : NXObject { }
    public class Curve : NXObject { }
    public class Line : Curve { }
    public class Arc : Curve { }
    public interface IBody { }
    public class Edge : NXObject
    {
        public enum EdgeType { Linear, Circular, Elliptical, Intersection, Spline, SpCurve, Foreign, ConstantParameter, TrimmedCurve, Convergent, Undefined }
        public EdgeType SolidEdgeType { get { return EdgeType.Linear; } }
        public double GetLength() { return 0; }
        public void GetVertices(out Point3d vertex1, out Point3d vertex2) { vertex1 = new Point3d(); vertex2 = new Point3d(); }
    }
    public class Face : NXObject
    {
        public enum FaceType { Rubber, Planar, Cylindrical, Conical, Spherical, SurfaceOfRevolution, Parametric, Blending, Offset, Swept, Convergent, Undefined }
        public FaceType SolidFaceType { get { return FaceType.Planar; } }
    }
    public class Body : NXObject, IBody
    {
        public bool IsSolidBody { get { return true; } }
        public Edge[] GetEdges() { return new Edge[0]; }
        public Face[] GetFaces() { return new Face[0]; }
    }
    public class Expression : NXObject
    {
        public double Value { get { return 0; } }
        public string RightHandSide { get { return ""; } set { } }
    }

    public class Collection : IEnumerable
    {
        public IEnumerator GetEnumerator() { return new ArrayList().GetEnumerator(); }
    }
    public class BodyCollection : Collection { }
    public class ExpressionCollection : Collection { }
    public class SectionCollection : Collection { }
    public class ScRuleFactory { }
    public class ScCollectorCollection : Collection { }
    public class AxisCollection : Collection { }
    public class PointCollection : Collection { public Point CreatePoint(Point3d coordinates) { return new Point(); } }
    public class DirectionCollection : Collection
    {
        public Direction CreateDirection(Point3d origin, Vector3d vector, SmartObject.UpdateOption option) { return new Direction(); }
    }
    public class NXMatrixCollection : Collection { public NXMatrix Create(Matrix3x3 element) { return new NXMatrix(); } }
    public class CurveCollection : Collection
    {
        public Line CreateLine(Point3d start, Point3d end) { return new Line(); }
        public Arc CreateArc(Point3d center, NXMatrix matrix, double radius, double startAngle, double endAngle) { return new Arc(); }
    }

    public class BasePart : NXObject { public string FullPath { get { return "stub.prt"; } } }
    public class Part : BasePart
    {
        public BodyCollection Bodies { get { return new BodyCollection(); } }
        public Features.FeatureCollection Features { get { return new Features.FeatureCollection(); } }
        public ExpressionCollection Expressions { get { return new ExpressionCollection(); } }
        public CurveCollection Curves { get { return new CurveCollection(); } }
        public PointCollection Points { get { return new PointCollection(); } }
        public DirectionCollection Directions { get { return new DirectionCollection(); } }
        public AxisCollection Axes { get { return new AxisCollection(); } }
        public SectionCollection Sections { get { return new SectionCollection(); } }
        public ScRuleFactory ScRuleFactory { get { return new ScRuleFactory(); } }
        public ScCollectorCollection ScCollectors { get { return new ScCollectorCollection(); } }
        public NXMatrixCollection NXMatrices { get { return new NXMatrixCollection(); } }
    }
    public class PartCollection : Collection { public Part Work { get { return null; } } }
    public class ListingWindow { public void Open() { } public void WriteLine(string text) { Console.WriteLine(text); } }
    public class Update { public int DoUpdate(Session.UndoMarkId mark) { return 0; } }

    public class Session
    {
        public enum LibraryUnloadOption { Immediately, Explicitly, AtTermination }
        public enum MarkVisibility { Visible, Invisible, AnyVisibility }
        public enum UndoMarkId { Null = 0 }
        private static readonly Session Instance = new Session();
        private readonly PartCollection parts = new PartCollection();
        public static Session GetSession() { return Instance; }
        public PartCollection Parts { get { return parts; } }
        public ListingWindow ListingWindow { get { return new ListingWindow(); } }
        public Update UpdateManager { get { return new Update(); } }
        public UndoMarkId SetUndoMark(MarkVisibility visibility, string name) { return UndoMarkId.Null; }
        public void UndoToMark(UndoMarkId mark, string name) { }
        public void DeleteUndoMark(UndoMarkId mark, string name) { }
    }
}

namespace NXOpen.Features
{
    public class Feature : NXOpen.NXObject
    {
        public string GetFeatureName() { return "STUB(0)"; }
        public string FeatureType { get { return "STUB"; } }
        public int Timestamp { get { return 0; } }
        public bool Suppressed { get { return false; } }
        public NXOpen.Expression[] GetExpressions() { return new NXOpen.Expression[0]; }
    }
    public class FeatureCollection : NXOpen.Collection { }
}

namespace NXOpen.Utilities
{
    public static class NXObjectManager
    {
        public static NXOpen.TaggedObject Get(NXOpen.Tag tag) { return null; }
    }
}

namespace NXOpen.UF
{
    public class UFModl { public void AskBoundingBox(NXOpen.Tag tag, double[] box) { } }
    public class UFSession
    {
        private static readonly UFSession Instance = new UFSession();
        public static UFSession GetUFSession() { return Instance; }
        public UFModl Modl { get { return new UFModl(); } }
    }
}
