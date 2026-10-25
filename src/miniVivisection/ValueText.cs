using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino.Geometry;

namespace MiniVivisection
{
    /// <summary>
    /// A value of the paused script as text for Claude (the evaluate tool): the type and
    /// the value, for Rhino geometry its key data -- point coordinates, curve ends and
    /// length, bounding boxes -- and lists item by item. On the paused thread: Python
    /// values need its GIL.
    /// </summary>
    internal static class ValueText
    {
        private const int MaxItems = 20;
        private const int MaxText = 300;

        public static string Describe(object value) => Describe(value, nested: false);

        private static string Describe(object value, bool nested)
        {
            switch (value)
            {
                case null:
                    return "None";
                case string text:
                    return "str " + Shorten("'" + text + "'");
                case bool flag:
                    return flag ? "True" : "False";
                case int number:
                    return number.ToString(CultureInfo.InvariantCulture);
                case long number:
                    return number.ToString(CultureInfo.InvariantCulture);
                case double number:
                    return N(number);
                case float number:
                    return N(number);
                case Point3d point:
                    return "Point3d " + P(point);
                case Point3f point:
                    return "Point3f " + P(new Point3d(point));
                case Vector3d vector:
                    return string.Format("Vector3d {0}, length {1}", P(new Point3d(vector)), N(vector.Length));
                case Line line:
                    return string.Format("Line from {0} to {1}, length {2}", P(line.From), P(line.To), N(line.Length));
                case Plane plane:
                    return string.Format("Plane, origin {0}, normal {1}", P(plane.Origin), P(new Point3d(plane.Normal)));
                case Circle circle:
                    return string.Format("Circle, center {0}, radius {1}, normal {2}", P(circle.Center), N(circle.Radius), P(new Point3d(circle.Normal)));
                case Arc arc:
                    return string.Format("Arc, center {0}, radius {1}, from {2} to {3}", P(arc.Center), N(arc.Radius), P(arc.StartPoint), P(arc.EndPoint));
                case Box box:
                    return "Box " + B(box.BoundingBox);
                case BoundingBox bounds:
                    return "BoundingBox " + B(bounds);
                case Interval interval:
                    return string.Format("Interval [{0}, {1}]", N(interval.T0), N(interval.T1));
                case Curve curve:
                    return string.Format("{0}: degree {1}, {2}, length {3}, from {4} to {5}", curve.GetType().Name, curve.Degree,
                        curve.IsClosed ? "closed" : "open", N(curve.GetLength()), P(curve.PointAtStart), P(curve.PointAtEnd));
                case Brep brep:
                    return string.Format("Brep: {0} face(s), {1} edge(s), {2}, bounding box {3}", brep.Faces.Count, brep.Edges.Count,
                        brep.IsSolid ? "solid" : "open", B(brep.GetBoundingBox(true)));
                case Mesh mesh:
                    return string.Format("Mesh: {0} vertices, {1} faces, {2}, bounding box {3}", mesh.Vertices.Count, mesh.Faces.Count,
                        mesh.IsClosed ? "closed" : "open", B(mesh.GetBoundingBox(true)));
                case GeometryBase geometry:
                    return string.Format("{0}, bounding box {1}", geometry.GetType().Name, B(geometry.GetBoundingBox(true)));
            }
            if (PythonBridge.IsPython(value))
            {
                var items = PythonBridge.Items(value);
                return items != null ? List("Python sequence", items, nested) : "Python value " + Shorten(value.ToString());
            }
            if (value is IEnumerable sequence && !nested)
                return List(value.GetType().Name, sequence.Cast<object>().Take(MaxItems + 1).ToList(), nested: false);
            if (value is IFormattable formattable)
                return value.GetType().Name + " " + formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.GetType().Name + " " + Shorten(value.ToString());
        }

        private static string List(string kind, IList<object> items, bool nested)
        {
            if (nested)
                return string.Format("{0} of {1}", kind, items.Count);
            var lines = items.Take(MaxItems).Select((item, i) => string.Format("  [{0}] {1}", i, Describe(item, nested: true)));
            var more = items.Count > MaxItems ? "\n  ..." : "";
            return string.Format("{0} of {1}{2}:\n{3}{4}", kind, items.Count > MaxItems ? "more than " : "", Math.Min(items.Count, MaxItems), string.Join("\n", lines), more);
        }

        private static string P(Point3d point) => string.Format("({0}, {1}, {2})", N(point.X), N(point.Y), N(point.Z));

        private static string B(BoundingBox bounds) =>
            bounds.IsValid ? string.Format("{0} to {1}", P(bounds.Min), P(bounds.Max)) : "(invalid)";

        private static string N(double number) => number.ToString("G6", CultureInfo.InvariantCulture);

        private static string Shorten(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return text.Length <= MaxText ? text : text.Substring(0, MaxText) + "...";
        }
    }
}
