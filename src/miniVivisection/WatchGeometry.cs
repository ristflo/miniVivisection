using System;
using System.Collections;
using System.Collections.Generic;
using Color = System.Drawing.Color;
using Rhino;
using Rhino.Geometry;

namespace MiniVivisection
{
    /// <summary>One drawable piece of a watched value (a copy -- the script may change
    /// the original once it continues): geometry, or a vector arrow from the world
    /// origin, or a plane's axes.</summary>
    internal sealed class WatchItem
    {
        public GeometryBase Geometry { get; set; }

        public Line? Arrow { get; set; }

        /// <summary>The value this item was made from (a Point3d, a Line, a curve, ...):
        /// edited geometry goes back as this type.</summary>
        public object Source { get; set; }

        public Plane? Plane { get; set; }

        /// <summary>For breps and meshes: what is drawn shaded (a brep's edges alone
        /// can be one seam line, as for a sphere).</summary>
        public Mesh DisplayMesh { get; set; }

        /// <summary>A Rhino object for this piece -- baking, editing; null for a plane.</summary>
        public GeometryBase ToDocumentGeometry()
        {
            if (Geometry != null)
                return Geometry.Duplicate();
            if (Arrow.HasValue)
                return new LineCurve(Arrow.Value);
            return null;
        }

        public BoundingBox Bounds
        {
            get
            {
                if (Geometry != null)
                    return Geometry.GetBoundingBox(false);
                if (Arrow.HasValue)
                    return Arrow.Value.BoundingBox;
                if (Plane.HasValue)
                    return new BoundingBox(Plane.Value.Origin, Plane.Value.Origin);
                return BoundingBox.Empty;
            }
        }
    }

    /// <summary>A watch and what of its value can be drawn.</summary>
    internal sealed class WatchedValue
    {
        public WatchedValue(string name, Color color, List<WatchItem> items, string label = null)
        {
            Name = name;
            Color = color;
            Items = items;
            var bounds = BoundingBox.Empty;
            foreach (var item in items)
                bounds.Union(item.Bounds);
            Bounds = bounds;
            Label = items.Count == 1 ? label ?? name : string.Format("{0} ({1})", label ?? name, items.Count);
            LabelPoint = items.Count > 0 ? LabelPointOf(items[0]) : Point3d.Origin;
        }

        public string Name { get; }

        public string Label { get; }

        public Color Color { get; }

        public List<WatchItem> Items { get; }

        public BoundingBox Bounds { get; }

        public Point3d LabelPoint { get; }

        private static Point3d LabelPointOf(WatchItem item)
        {
            if (item.Arrow.HasValue)
                return item.Arrow.Value.To;
            if (item.Plane.HasValue)
                return item.Plane.Value.Origin;
            return LabelPointOf(item.Geometry, item.Bounds);
        }

        /// <summary>Where a name dot goes for <paramref name="geometry"/>: a curve's middle,
        /// a point, else the middle of its box.</summary>
        internal static Point3d LabelPointOf(GeometryBase geometry, BoundingBox bounds)
        {
            switch (geometry)
            {
                case Curve curve:
                    return curve.PointAt(curve.Domain.Mid);
                case Point point:
                    return point.Location;
                case TextDot dot:
                    return dot.Point;
            }
            if (!bounds.IsValid && geometry != null)
                bounds = geometry.GetBoundingBox(false);
            return bounds.IsValid ? bounds.Center : Point3d.Origin;
        }
    }

    /// <summary>Turns watch values into drawable copies.</summary>
    internal static class WatchGeometry
    {
        private const int MaxItems = 10000;
        private const int MaxDepth = 4;

        /// <summary>Distinct colors for the watches.</summary>
        private static readonly Color[] Palette =
        {
            Color.FromArgb(255, 140, 0),   // orange
            Color.FromArgb(0, 190, 255),   // sky blue
            Color.FromArgb(230, 50, 200),  // magenta
            Color.FromArgb(90, 200, 40),   // green
            Color.FromArgb(240, 200, 0),   // yellow
            Color.FromArgb(150, 90, 255),  // violet
        };

        /// <summary>A watch's color, the same for the same expression in every session
        /// (the same color wherever the expression shows up).</summary>
        public static Color ColorFor(string name)
        {
            var hash = 0;
            foreach (var c in name)
                hash = unchecked(hash * 31 + c);
            return Palette[(hash & int.MaxValue) % Palette.Length];
        }

        private static bool s_reportedPipeline;

        /// <summary>The drawable items of <paramref name="value"/>, the same way everywhere
        /// (the V Watch tab, bake, hover, show()): on the paused thread through the
        /// drawing pipeline -- visualizers, Interpret As (<paramref name="interpretation"/>,
        /// "auto" by default), containers --, elsewhere geometry only (Collect).
        /// <paramref name="how"/> is the pipeline's path ("as polyline", "by show_sensors",
        /// or empty); <paramref name="problem"/> a visualizer's error.</summary>
        public static List<WatchItem> Drawable(object value, string interpretation, bool onPausedThread,
            out string how, out bool pythonPending, out string problem)
        {
            how = "";
            problem = null;
            pythonPending = false;
            if (onPausedThread && PythonBridge.IsRunning)
            {
                try
                {
                    if (PythonBridge.TryVisualize(value, interpretation, MaxItems, out how, out var parts))
                    {
                        var items = new List<WatchItem>();
                        foreach (var (kind, part) in parts)
                        {
                            if (kind == "arrow" && part is Line line && line.IsValid)
                                items.Add(new WatchItem { Arrow = line, Source = line });
                            else
                                Collect(part, true, items, 0, ref pythonPending);
                        }
                        return items;
                    }
                }
                catch (Exception ex)
                {
                    var message = ScriptEditorHook.FirstLine(ex);
                    if (message.Contains("No module named 'minivivisection_visualize'"))
                    {
                        if (!s_reportedPipeline)
                        {
                            s_reportedPipeline = true;
                            UserLog.Warning("The drawing pipeline (python/minivivisection_visualize.py) wasn't found next to the plug-in; drawing geometry only.");
                        }
                    }
                    else
                    {
                        problem = message;
                        Log.Debug(LogArea.Draw, "pipeline: {0}", Log.Describe(ex));
                    }
                }
            }
            return Collect(value, onPausedThread, out pythonPending);
        }

        /// <summary>The drawable items of <paramref name="value"/>: geometry, Rhino object
        /// ids, .NET collections and -- on the paused thread only -- Python lists,
        /// tuples and sets. <paramref name="pythonPending"/> says a Python collection
        /// was skipped because this isn't the paused thread.</summary>
        public static List<WatchItem> Collect(object value, bool onPausedThread, out bool pythonPending)
        {
            var items = new List<WatchItem>();
            pythonPending = false;
            Collect(value, onPausedThread, items, 0, ref pythonPending);
            return items;
        }

        /// <summary>A copy of the document object's geometry -- looked up on the UI thread (the
        /// document is its; a watch is collected on the paused script's thread while Rhino is
        /// usable, and an object deleted or moved meanwhile must not be read there), waited for
        /// up to two seconds. Null when there is no such object, or the UI thread doesn't answer.</summary>
        private static GeometryBase GeometryOf(Guid id)
        {
            GeometryBase Look() => RhinoDoc.ActiveDoc?.Objects.FindId(id)?.Geometry?.Duplicate();
            if (!RhinoApp.InvokeRequired)
                return Look();
            GeometryBase result = null;
            using (var done = new System.Threading.ManualResetEventSlim())
            {
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    try
                    {
                        result = Look();
                    }
                    catch (Exception)
                    {
                        // nothing to draw
                    }
                    finally
                    {
                        done.Set();
                    }
                }));
                return done.Wait(2000) ? result : null;
            }
        }

        private static void Collect(object value, bool onPausedThread, List<WatchItem> items, int depth, ref bool pythonPending)
        {
            if (value == null || items.Count >= MaxItems)
                return;
            var item = AsItem(value);
            if (item != null)
            {
                item.Source = value;
                items.Add(item);
                return;
            }
            if (value is Guid id)
            {
                var geometry = GeometryOf(id);
                if (geometry != null)
                    Collect(geometry, onPausedThread, items, depth, ref pythonPending);
                return;
            }
            if (depth >= MaxDepth)
                return;
            if (PythonBridge.IsPython(value))
            {
                if (!onPausedThread)
                {
                    pythonPending = true;
                    return;
                }
                foreach (var element in PythonBridge.Items(value) ?? new List<object>())
                    Collect(element, onPausedThread, items, depth + 1, ref pythonPending);
                return;
            }
            // Materialized collections only: enumerating a lazy sequence could run
            // the script's code.
            if (value is ICollection collection && !(value is string))
            {
                foreach (var element in collection)
                    Collect(element, onPausedThread, items, depth + 1, ref pythonPending);
            }
        }

        private static WatchItem AsItem(object value)
        {
            switch (value)
            {
                case Curve curve:
                    return new WatchItem { Geometry = curve.DuplicateCurve() };
                case Line line when line.IsValid:
                    return new WatchItem { Geometry = new LineCurve(line) };
                case Polyline polyline when polyline.IsValid:
                    return new WatchItem { Geometry = new PolylineCurve(polyline) };
                case Arc arc when arc.IsValid:
                    return new WatchItem { Geometry = new ArcCurve(arc) };
                case Circle circle when circle.IsValid:
                    return new WatchItem { Geometry = new ArcCurve(circle) };
                case Ellipse ellipse:
                    return new WatchItem { Geometry = ellipse.ToNurbsCurve() };
                case Rectangle3d rectangle when rectangle.IsValid:
                    return new WatchItem { Geometry = rectangle.ToNurbsCurve() };
                case Point3d point when point.IsValid:
                    return new WatchItem { Geometry = new Point(point) };
                case Point3f point when point.IsValid:
                    return new WatchItem { Geometry = new Point(new Point3d(point)) };
                case Point point:
                    return new WatchItem { Geometry = new Point(point.Location) };
                case Vector3d vector when vector.IsValid && !vector.IsZero:
                    return new WatchItem { Arrow = new Line(Point3d.Origin, vector) };
                case Vector3f vector when vector.IsValid && !vector.IsZero:
                    return new WatchItem { Arrow = new Line(Point3d.Origin, new Vector3d(vector)) };
                case Plane plane when plane.IsValid:
                    return new WatchItem { Plane = plane };
                case Box box when box.IsValid:
                    return FromBrep(box.ToBrep());
                case BoundingBox bounds when bounds.IsValid:
                    return FromBrep(bounds.ToBrep());
                case Brep brep:
                    return FromBrep(brep.DuplicateBrep());
                case Extrusion extrusion:
                    return FromBrep(extrusion.ToBrep());
                case Surface surface:
                    return FromBrep(surface.ToBrep());
                case Mesh mesh:
                    return FromMesh(mesh.DuplicateMesh());
                case SubD subd:
                    return FromMesh(Rhino.Geometry.Mesh.CreateFromSubD(subd, 0));
                case PointCloud cloud:
                    return new WatchItem { Geometry = cloud.Duplicate() };
                case TextDot dot:
                    return new WatchItem { Geometry = dot.Duplicate() };
                default:
                    return null;
            }
        }

        private static WatchItem FromBrep(Brep brep)
        {
            if (brep == null)
                return null;
            var display = new Mesh();
            foreach (var part in Rhino.Geometry.Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? new Mesh[0])
                display.Append(part);
            return new WatchItem { Geometry = brep, DisplayMesh = display.Vertices.Count > 0 ? display : null };
        }

        private static WatchItem FromMesh(Mesh mesh) => mesh == null ? null : new WatchItem { Geometry = mesh, DisplayMesh = mesh };

        /// <summary>A drawable copy of one value -- what <see cref="Collect"/> does for a
        /// single piece; null when it isn't one.</summary>
        public static WatchItem ItemOf(object value) => AsItem(value);

        /// <summary><paramref name="geometry"/> (a Rhino object edited in the viewport) as
        /// the type the script's variable had -- a Point3d from a point, a Line, Circle or
        /// Arc from a curve, a Box from a brep, ...; null when it can't be one.</summary>
        public static object ToOriginalType(GeometryBase geometry, object original)
        {
            switch (original)
            {
                case Point3d _:
                    return geometry is Point point ? (object)point.Location : null;
                case Point3f _:
                    return geometry is Point pointF ? (object)new Point3f((float)pointF.Location.X, (float)pointF.Location.Y, (float)pointF.Location.Z) : null;
                case Vector3d _:
                    return geometry is Curve arrow ? (object)(arrow.PointAtEnd - arrow.PointAtStart) : null;
                case Line _:
                    return geometry is Curve lineCurve ? (object)new Line(lineCurve.PointAtStart, lineCurve.PointAtEnd) : null;
                case Polyline _:
                    return geometry is Curve polylineCurve && polylineCurve.TryGetPolyline(out var polyline) ? (object)polyline : null;
                case Circle _:
                    return geometry is Curve circleCurve && circleCurve.TryGetCircle(out var circle) ? (object)circle : null;
                case Arc _:
                    return geometry is Curve arcCurve && arcCurve.TryGetArc(out var arc) ? (object)arc : null;
                case Box box:
                    return geometry is Brep boxBrep ? (object)new Box(box.Plane, boxBrep) : null;
                case BoundingBox _:
                    return geometry.GetBoundingBox(true);
                case Curve _:
                    return geometry as Curve;
                case Brep _:
                case Extrusion _: // before Surface, its base class
                    return geometry is Extrusion extrusion ? extrusion.ToBrep() : geometry as Brep;
                case Surface _:
                    return geometry is Brep surfaceBrep && surfaceBrep.Faces.Count == 1 ? surfaceBrep.Faces[0].DuplicateSurface() : geometry as Surface;
                case Mesh _:
                    return geometry as Mesh;
                case PointCloud _:
                    return geometry as PointCloud;
                case Point _:
                    return geometry as Point;
                case TextDot _:
                    return geometry as TextDot;
                default:
                    return null;
            }
        }
    }
}
