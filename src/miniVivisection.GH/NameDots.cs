using System;
using System.Collections.Concurrent;
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace MiniVivisection.GH
{
    /// <summary>
    /// V Edit's name dots in Rhino: a text dot up and right of an edit object, with a tail down
    /// to it -- the plug-in's (WatchConduit), in its look: miniVivisection's settings "Name dot
    /// pointer" (Tail, Line), "Name dot offset" and "Name dot opacity", read from the plug-in's
    /// saved settings; its defaults (Tail, 30, 50) when there are none.
    /// </summary>
    internal static class NameDots
    {
        private static readonly Guid PlugInId = new Guid("c198ec17-3078-46a0-8f9b-94a40e9ba298"); // miniVivisection.rhp
        private static DateTime s_read;
        private static bool s_tail = true;
        private static int s_offset = 30;
        private static int s_opacity = 50;

        /// <summary>The plug-in's dot settings, read again every few seconds (they can change).</summary>
        private static void ReadSettings()
        {
            if (DateTime.UtcNow - s_read < TimeSpan.FromSeconds(3))
                return;
            s_read = DateTime.UtcNow;
            try
            {
                var settings = Rhino.PlugIns.PlugIn.GetPluginSettings(PlugInId, false);
                if (settings == null)
                    return;
                if (settings.TryGetString("NameDotPointer", out var pointer))
                    s_tail = pointer != "Line";
                if (settings.TryGetInteger("NameDotOffset", out var offset))
                    s_offset = Math.Max(0, Math.Min(200, offset));
                if (settings.TryGetInteger("NameDotOpacity", out var opacity))
                    s_opacity = Math.Max(10, Math.Min(100, opacity));
            }
            catch (Exception)
            {
                // the defaults
            }
        }

        public static void Draw(DrawEventArgs e, Point3d at, string text, Color color)
        {
            ReadSettings();
            var alpha = (int)Math.Round(255 * s_opacity / 100.0);
            var fill = Color.FromArgb(alpha, color);
            var ink = Color.FromArgb(Math.Max(alpha, 160), Color.White);
            if (s_offset == 0)
            {
                e.Display.DrawDot(at, text, fill, ink);
                return;
            }
            var screen = e.Viewport.WorldToClient(at);
            if (!screen.IsValid)
                return;
            var step = s_offset / Math.Sqrt(2);
            var halfHeight = DotHalfHeight(e, text);
            var bottom = new Point2d(screen.X + step, screen.Y - step);
            if (s_tail)
                DrawTail(e, at, screen, bottom, fill);
            else
                e.Display.Draw2dLine(new PointF((float)screen.X, (float)screen.Y), new PointF((float)bottom.X, (float)bottom.Y), fill, 1.5f);
            e.Display.DrawDot((float)bottom.X, (float)(bottom.Y - halfHeight), text, fill, ink);
        }

        private static readonly ConcurrentDictionary<string, double> s_halfHeights = new ConcurrentDictionary<string, double>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<int, DisplayMaterial> s_materials = new ConcurrentDictionary<int, DisplayMaterial>();

        private static double DotHalfHeight(DrawEventArgs e, string text)
        {
            text = text ?? "";
            if (s_halfHeights.TryGetValue(text, out var known))
                return known;
            var half = 10.0;
            try
            {
                var box = e.Display.Measure2dText(text, new Point2d(0, 0), true, 0, 14, "Arial");
                if (box.Height > 0)
                    half = box.Height / 2.0 + 3;
            }
            catch (Exception)
            {
                // the usual size
            }
            if (s_halfHeights.Count > 500)
                s_halfHeights.Clear();
            s_halfHeights[text] = half;
            return half;
        }

        private static DisplayMaterial TailMaterial(Color fill) =>
            s_materials.GetOrAdd(fill.ToArgb(), _ => new DisplayMaterial
            {
                Diffuse = Color.Black,
                Specular = Color.Black,
                Emission = Color.FromArgb(255, fill),
                Transparency = 1 - fill.A / 255.0,
            });

        /// <summary>The speech-bubble tail: wide at the dot, curving down to a point at the
        /// object, shaped in screen pixels on the plane through it facing the camera.</summary>
        private static void DrawTail(DrawEventArgs e, Point3d at, Point2d tip, Point2d dot, Color fill)
        {
            var dx = tip.X - dot.X;
            var dy = tip.Y - dot.Y;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 2)
                return;
            dx /= length;
            dy /= length;
            var nx = dy;
            var ny = -dx;
            var half = Math.Min(7.0, Math.Max(3.0, length * 0.2));
            var bend = length * 0.22;
            var bendX = dot.X + dx * length * 0.5 + nx * bend;
            var bendY = dot.Y + dy * length * 0.5 + ny * bend;
            var plane = new Plane(at, e.Viewport.CameraZ);
            const int steps = 14;
            var sides = new Point3d[2][];
            for (var side = 0; side < 2; side++)
            {
                var sign = side == 0 ? 1 : -1;
                var startX = dot.X + half * sign;
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
            var outline = new Polyline(sides[0]);
            for (var i = steps - 1; i >= 0; i--)
                outline.Add(sides[1][i]);
            outline.Add(outline[0]);
            var mesh = Mesh.CreateFromClosedPolyline(outline);
            if (mesh != null)
                e.Display.DrawMeshShaded(mesh, TailMaterial(fill));
        }

        /// <summary>Where a dot points: a point itself, a curve's middle, else the box's center.</summary>
        public static Point3d LabelPoint(GeometryBase geometry)
        {
            switch (geometry)
            {
                case Rhino.Geometry.Point point:
                    return point.Location;
                case Curve curve:
                    return curve.PointAtNormalizedLength(0.5);
                default:
                    return geometry.GetBoundingBox(false).Center;
            }
        }
    }
}
