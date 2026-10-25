using System;
using System.Collections.Generic;
using Color = System.Drawing.Color;
using System.Linq;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>Draws the watched values of every paused Script Editor, each watch in
    /// its own color with its expression as a dot. Nothing is added to the document.</summary>
    internal sealed class WatchConduit : DisplayConduit
    {
        private const int CurveThickness = 3;
        private const int PointSize = 6;
        private const double PlaneAxisPixels = 60;

        private readonly object _lock = new object();
        private readonly Dictionary<IDebugControls, List<WatchedValue>> _byEditor =
            new Dictionary<IDebugControls, List<WatchedValue>>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Color, DisplayMaterial> _materials = new Dictionary<Color, DisplayMaterial>();
        private readonly HashSet<string> _hidden = new HashSet<string>(StringComparer.Ordinal);
        private WatchedValue[] _values = new WatchedValue[0];
        private WatchedValue _hover;
        private readonly Dictionary<string, WatchedValue> _shown = new Dictionary<string, WatchedValue>(StringComparer.Ordinal);
        private BoundingBox _bounds = BoundingBox.Empty;
        private HashSet<(string Name, int Index)> _selected = new HashSet<(string, int)>();

        /// <summary>show(value) in the Python tab: drawn until hidden or cleared, whatever
        /// the debugger does.</summary>
        public void SetShown(string label, WatchedValue value)
        {
            lock (_lock)
            {
                _shown[label] = value;
                Update();
            }
        }

        public bool HideShown(string label)
        {
            lock (_lock)
            {
                var removed = _shown.Remove(label);
                if (removed)
                    Update();
                return removed;
            }
        }

        public int ClearShown()
        {
            lock (_lock)
            {
                var count = _shown.Count;
                _shown.Clear();
                Update();
                return count;
            }
        }

        /// <summary>The value under the mouse in the code (HoverPreview), drawn on top
        /// and thicker; null hides it. Returns whether that changed anything.</summary>
        public bool SetHover(WatchedValue value)
        {
            lock (_lock)
            {
                if (_hover == null && value == null)
                    return false;
                _hover = value;
                Update();
                return true;
            }
        }

        /// <summary>Selected watches -- (name, -1) for all of a watch's items, (name, i) for
        /// one -- drawn in Rhino's selection color, thicker. Returns whether that changed.</summary>
        public bool SetSelected(HashSet<(string Name, int Index)> selected)
        {
            lock (_lock)
            {
                if (_selected.SetEquals(selected))
                    return false;
                _selected = selected;
                return true;
            }
        }

        private bool IsSelected(string name, int index)
        {
            lock (_lock)
                return _selected.Contains((name, -1)) || _selected.Contains((name, index));
        }

        /// <summary>The drawn item nearest the click (Rhino's own pick test: a pick
        /// frustum at <paramref name="client"/>), its debugger and its index; null when
        /// nothing drawn is there.</summary>
        public (IDebugControls Controls, WatchedValue Value, int Index)? HitTest(RhinoView view, System.Drawing.Point client)
        {
            List<(IDebugControls Controls, WatchedValue Value)> drawn;
            lock (_lock)
                drawn = _byEditor.SelectMany(p => p.Value.Select(v => (p.Key, v))).Where(d => !_hidden.Contains(d.v.Name)).ToList();
            if (drawn.Count == 0)
                return null;
            using (var pick = new Rhino.Input.Custom.PickContext())
            {
                pick.View = view;
                pick.PickStyle = Rhino.Input.Custom.PickStyle.PointPick;
                pick.SetPickTransform(view.ActiveViewport.GetPickTransform(client));
                pick.UpdateClippingPlanes();
                (IDebugControls, WatchedValue, int)? best = null;
                var bestDistance = double.MaxValue;
                var bestDepth = double.MaxValue;
                foreach (var (controls, value) in drawn)
                {
                    for (var i = 0; i < value.Items.Count; i++)
                    {
                        if (!Hit(pick, value.Items[i], out var depth, out var distance))
                            continue;
                        if (distance < bestDistance || distance == bestDistance && depth < bestDepth)
                        {
                            best = (controls, value, i);
                            bestDistance = distance;
                            bestDepth = depth;
                        }
                    }
                }
                return best;
            }
        }

        private static bool Hit(Rhino.Input.Custom.PickContext pick, WatchItem item, out double depth, out double distance)
        {
            depth = distance = double.MaxValue;
            if (item.Arrow.HasValue)
                return pick.PickFrustumTest(item.Arrow.Value, out _, out depth, out distance);
            switch (item.Geometry)
            {
                case Point point:
                    return pick.PickFrustumTest(point.Location, out depth, out distance);
                case Curve curve:
                    var nurbs = curve.ToNurbsCurve();
                    return nurbs != null && pick.PickFrustumTest(nurbs, out _, out depth, out distance);
                case PointCloud cloud:
                    return pick.PickFrustumTest(cloud, out _, out depth, out distance);
            }
            if (item.DisplayMesh != null)
                return pick.PickFrustumTest(item.DisplayMesh, Rhino.Input.Custom.PickContext.MeshPickStyle.ShadedModePicking,
                    out _, out depth, out distance, out _, out _);
            if (item.Plane.HasValue)
                return pick.PickFrustumTest(item.Plane.Value.Origin, out depth, out distance);
            return false;
        }

        /// <summary>The watched values drawn right now (copies; for baking).</summary>
        public WatchedValue[] Values => _values;

        /// <summary>Watches edited as Rhino objects (EditSession) aren't drawn: the
        /// objects show them.</summary>
        public void SetHidden(IEnumerable<string> expressions)
        {
            lock (_lock)
            {
                _hidden.Clear();
                _hidden.UnionWith(expressions);
            }
        }

        /// <summary>Drawn pieces right now, over all watches.</summary>
        public int Count => _values.Sum(v => v.Items.Count);

        public void Set(IDebugControls editor, List<WatchedValue> values)
        {
            lock (_lock)
            {
                _byEditor[editor] = values;
                Update();
            }
        }

        public void Clear(IDebugControls editor)
        {
            lock (_lock)
            {
                if (_byEditor.Remove(editor))
                    Update();
            }
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                _byEditor.Clear();
                Update();
            }
        }

        private void Update()
        {
            _values = _byEditor.Values.SelectMany(v => v).Concat(_shown.Values).ToArray();
            var bounds = BoundingBox.Empty;
            foreach (var value in _values)
                bounds.Union(value.Bounds);
            if (_hover != null)
                bounds.Union(_hover.Bounds);
            _bounds = bounds;
        }

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (_bounds.IsValid)
                e.IncludeBoundingBox(_bounds);
            // A plane's axes are PlaneAxisPixels long whatever the zoom: that much around its
            // origin in this viewport, else the far clipping plane cuts them off
            var values = _values;
            var hover = _hover;
            foreach (var value in hover == null ? values : values.Concat(new[] { hover }))
            {
                foreach (var item in value.Items)
                {
                    if (item.Plane is Plane plane && e.Viewport.GetWorldToScreenScale(plane.Origin, out var pixelsPerUnit) && pixelsPerUnit > 0)
                    {
                        var box = new BoundingBox(plane.Origin, plane.Origin);
                        box.Inflate(PlaneAxisPixels / pixelsPerUnit);
                        e.IncludeBoundingBox(box);
                    }
                }
            }
        }

        /// <summary>Where the name dot of a value being edited goes: at its Rhino objects
        /// (EditSession), or null.</summary>
        public Func<string, Point3d?> EditedLabelPoint { get; set; }

        private bool IsHidden(WatchedValue value)
        {
            lock (_lock)
                return _hidden.Contains(value.Name);
        }

        private DisplayMaterial MaterialFor(Color color)
        {
            if (!_materials.TryGetValue(color, out var material))
            {
                material = new DisplayMaterial(color, 0.55);
                _materials[color] = material;
            }
            return material;
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            try
            {
                foreach (var value in _values)
                {
                    if (!IsHidden(value))
                        DrawValue(e, value, CurveThickness);
                }
                var hover = _hover;
                if (hover != null)
                    DrawValue(e, hover, CurveThickness + 3);
            }
            catch (Exception ex)
            {
                ReportDrawError(ex);
            }
        }

        private bool _reportedDrawError;

        // A broken item would fail at every redraw: reported once.
        private void ReportDrawError(Exception ex)
        {
            if (_reportedDrawError)
                return;
            _reportedDrawError = true;
            Log.Error(LogArea.Draw, "drawing the watches: {0}", Log.Describe(ex));
        }

        private void DrawValue(DrawEventArgs e, WatchedValue value, int thickness)
        {
            var selectedColor = Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor;
            for (var i = 0; i < value.Items.Count; i++)
            {
                var item = value.Items[i];
                var selected = IsSelected(value.Name, i);
                var color = selected ? selectedColor : value.Color;
                var width = selected ? thickness + 2 : thickness;
                if (item.DisplayMesh != null)
                    e.Display.DrawMeshShaded(item.DisplayMesh, MaterialFor(color));
                switch (item.Geometry)
                {
                    case Curve curve:
                        e.Display.DrawCurve(curve, color, width);
                        break;
                    case Point point:
                        e.Display.DrawPoint(point.Location, PointStyle.RoundSimple, PointSize + width - CurveThickness, color);
                        break;
                    case PointCloud cloud:
                        e.Display.DrawPointCloud(cloud, PointSize / 2, color);
                        break;
                    case Brep brep:
                        e.Display.DrawBrepWires(brep, color, selected ? 2 : 1);
                        break;
                    case Mesh mesh:
                        e.Display.DrawMeshWires(mesh, color);
                        break;
                }
                if (item.Arrow.HasValue)
                    e.Display.DrawArrow(item.Arrow.Value, color);
            }
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            try
            {
                DrawLabels(e);
            }
            catch (Exception ex)
            {
                ReportDrawError(ex);
            }
        }

        private void DrawLabels(DrawEventArgs e)
        {
            foreach (var value in _values)
            {
                if (IsHidden(value))
                {
                    // edited: Rhino objects instead of the drawing -- the dot follows them
                    if (value.Items.Count > 0 && EditedLabelPoint?.Invoke(value.Name) is Point3d edited)
                        DrawNameDot(e, edited, value.Label, value.Color, Color.White);
                    continue;
                }
                foreach (var item in value.Items)
                {
                    if (item.Geometry is TextDot dot)
                        e.Display.DrawDot(dot.Point, dot.Text, value.Color, Color.White);
                    if (item.Plane.HasValue)
                        DrawPlaneAxes(e, item.Plane.Value);
                }
                if (value.Items.Count > 0)
                    DrawNameDot(e, value.LabelPoint, value.Label, value.Color, Color.White);
            }
            var hover = _hover;
            if (hover != null && hover.Items.Count > 0)
                DrawNameDot(e, hover.LabelPoint, hover.Label, hover.Color, Color.Black);
        }

        /// <summary>A value's name dot (Options: "Name dots", "Name dot offset", "Name dot
        /// opacity"): in screen space, up and right of the value by the offset in pixels at
        /// any zoom, with a tail from its bottom edge down to the value, so a point stays
        /// visible.</summary>
        private static void DrawNameDot(DrawEventArgs e, Point3d at, string text, Color color, Color textColor)
        {
            if (!MiniVivisectionSettings.NameDots)
                return;
            var alpha = (int)Math.Round(255 * MiniVivisectionSettings.NameDotOpacity / 100.0);
            var fill = Color.FromArgb(alpha, color);
            var ink = Color.FromArgb(Math.Max(alpha, 160), textColor);
            var offset = MiniVivisectionSettings.NameDotOffset;
            if (offset == 0)
            {
                e.Display.DrawDot(at, text, fill, ink);
                return;
            }
            var screen = e.Viewport.WorldToClient(at);
            if (!screen.IsValid)
                return;
            // The offset is the tail's length: from the value to the dot's bottom edge.
            var step = offset / Math.Sqrt(2);
            var halfHeight = DotHalfHeight(e, text);
            var bottom = new Point2d(screen.X + step, screen.Y - step);
            if (MiniVivisectionSettings.NameDotTail)
                DrawTail(e, at, screen, bottom, fill);
            else
                e.Display.Draw2dLine(new System.Drawing.PointF((float)screen.X, (float)screen.Y), new System.Drawing.PointF((float)bottom.X, (float)bottom.Y), fill, 1.5f);
            e.Display.DrawDot((float)bottom.X, (float)(bottom.Y - halfHeight), text, fill, ink);
        }

        /// <summary>Half a name dot's height in pixels: its text's (Rhino's dots: 14 px text)
        /// and the dot's margin.</summary>
        private static double DotHalfHeight(DrawEventArgs e, string text)
        {
            text = text ?? "";
            if (s_dotHalfHeights.TryGetValue(text, out var known))
                return known; // measured once per text, not every frame
            var half = 10.0;
            try
            {
                var box = e.Display.Measure2dText(text, new Point2d(0, 0), true, 0, 14, "Arial");
                if (box.Height > 0)
                    half = box.Height / 2.0 + 3;
            }
            catch (Exception)
            {
                // the usual size then
            }
            if (s_dotHalfHeights.Count > 500)
                s_dotHalfHeights.Clear(); // labels come and go with the watches
            s_dotHalfHeights[text] = half;
            return half;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> s_dotHalfHeights =
            new System.Collections.Concurrent.ConcurrentDictionary<string, double>(StringComparer.Ordinal);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DisplayMaterial> s_tailMaterials =
            new System.Collections.Concurrent.ConcurrentDictionary<int, DisplayMaterial>();

        /// <summary>The tail's material for a color with its transparency: made once per color.</summary>
        private static DisplayMaterial TailMaterial(Color fill) =>
            s_tailMaterials.GetOrAdd(fill.ToArgb(), _ => new DisplayMaterial
            {
                Diffuse = Color.Black,
                Specular = Color.Black,
                Emission = Color.FromArgb(255, fill), // the dot's color, unlit
                Transparency = 1 - fill.A / 255.0,
            });

        /// <summary>A speech bubble's tail from the dot's bottom edge to its value: wide at
        /// the dot, curving down like a slide (steep below the dot, flatter at the value),
        /// tapering to a point at the value. Shaped in screen pixels (the same at any zoom),
        /// drawn on the plane through the value facing the camera.</summary>
        private static void DrawTail(DrawEventArgs e, Point3d at, Point2d tip, Point2d dot, Color fill)
        {
            var dx = tip.X - dot.X;
            var dy = tip.Y - dot.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 2)
                return;
            dx /= length;
            dy /= length;
            var nx = dy; // across the tail, to the side under the straight line: the curve sags
            var ny = -dx;
            var half = Math.Min(7.0, Math.Max(3.0, length * 0.2)); // half the width at the dot
            var bend = length * 0.22; // how far the tail curves to one side
            var bendX = dot.X + dx * length * 0.5 + nx * bend;
            var bendY = dot.Y + dy * length * 0.5 + ny * bend;
            var plane = new Plane(at, e.Viewport.CameraZ);
            const int steps = 14;
            // Both sides from the dot to the tip: quadratic curves bent alike, their control
            // points apart, so the tail is wide at the dot and sharp at the tip.
            var sides = new Point3d[2][];
            for (var side = 0; side < 2; side++)
            {
                var sign = side == 0 ? 1 : -1;
                var startX = dot.X + half * sign; // the top edge: flat along the dot's bottom
                var startY = dot.Y;
                var controlX = bendX + nx * half * 0.6 * sign;
                var controlY = bendY + ny * half * 0.6 * sign;
                sides[side] = new Point3d[steps + 1];
                for (var i = 0; i <= steps; i++)
                {
                    var t = i / (double)steps;
                    var u = 1 - t;
                    var x = u * u * startX + 2 * u * t * controlX + t * t * tip.X;
                    var y = u * u * startY + 2 * u * t * controlY + t * t * tip.Y;
                    if (!e.Viewport.GetFrustumLine(x, y, out var ray) || !Rhino.Geometry.Intersect.Intersection.LinePlane(ray, plane, out var along))
                        return;
                    sides[side][i] = ray.PointAt(along);
                }
            }
            // One closed outline: down one side to the tip, back up the other; its flat top
            // edge closes it. Filled as Rhino meshes it (DrawPolygon would fan from the first
            // point and cut straight across the curved outline).
            var outline = new Polyline(sides[0]);
            for (var i = steps - 1; i >= 0; i--)
                outline.Add(sides[1][i]);
            outline.Add(outline[0]);
            var mesh = Mesh.CreateFromClosedPolyline(outline);
            if (mesh == null)
                return;
            e.Display.DrawMeshShaded(mesh, TailMaterial(fill));
        }

        /// <summary>A plane's axes, 60 pixels long at any zoom: x red, y green, z blue.</summary>
        private static void DrawPlaneAxes(DrawEventArgs e, Plane plane)
        {
            if (!e.Viewport.GetWorldToScreenScale(plane.Origin, out var pixelsPerUnit) || pixelsPerUnit <= 0)
                return;
            var length = PlaneAxisPixels / pixelsPerUnit;
            e.Display.DrawArrow(new Line(plane.Origin, plane.XAxis * length), Color.Red);
            e.Display.DrawArrow(new Line(plane.Origin, plane.YAxis * length), Color.LimeGreen);
            e.Display.DrawArrow(new Line(plane.Origin, plane.ZAxis * length), Color.Blue);
        }
    }
}
