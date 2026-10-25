using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.UI;

namespace MiniVivisection
{
    /// <summary>
    /// A double-click on a Markdown text in a Rhino view (MarkdownText's, or V Text's baked) --
    /// on a link too -- opens its Markdown in MarkdownText's dialog, the caret at the part
    /// double-clicked, instead of Rhino's own text editor. A link is followed (MarkdownLinks) as
    /// the setting "Markdown links: follow with" says: Click -- a plain click, once the
    /// double-click time has passed without a second click --, Ctrl+click -- at once --, or
    /// Off; the press is taken from Rhino (no selection). Not while a command runs. A task's box
    /// ("- [ ]") clicked so is checked or unchecked (its Markdown changed, the text updated).
    ///
    /// The part under the mouse: a text's box (in its plane) on screen, a curve within a few
    /// pixels; the smallest text first. The link under it: the text's glyphs
    /// (TextEntity.CreateCurvesGrouped, one group per character that isn't blank), counted as
    /// the part's links are (in its non-blank characters) -- while its text is as it was made.
    ///
    /// Over a link, a hint by the mouse says where it goes and how to follow it ("Rhino: zoom to
    /// Circle -- click"; off: where to switch it on): drawn in the view (HintConduit), the
    /// parts with links found again after the document changes, their glyphs' boxes kept per object.
    /// </summary>
    internal sealed class MarkdownTextMouse : MouseCallback
    {
        private static MarkdownTextMouse s_instance;

        /// <summary>For a test: told what a double-click hit (the part, the link there) instead of editing it.</summary>
        internal static Action<RhinoObject, string> Probe = null;

        /// <summary>For a test: told the link a click follows instead of following it.</summary>
        internal static Action<string> FollowProbe = null;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        // A link pressed (followed when let go on it), and a click's link waiting out the double-click time
        private static (string Url, System.Drawing.Point At, bool Ctrl)? s_down;
        private static string s_waiting;
        private static int s_click; // counts the clicks: a follow waiting is for the last one only

        public static void Install()
        {
            if (s_instance != null)
                return;
            s_instance = new MarkdownTextMouse { Enabled = true };
            MarkdownLinkLetters.Install();
            // Links that open a file: not under a paused script, nor under a Grasshopper run
            MarkdownLinks.FileLinksWait = () =>
                GrasshopperPauseGuard.AnyPaused() ? "a script is paused" :
                GrasshopperRuns.Solving ? "a Grasshopper script component computes" : null;
            // An object changed: what was known of it forgotten, only its -- the parts with links,
            // their glyphs and filled letters (MarkdownLinkLetters) are dear to find again: a text's
            // glyphs take milliseconds, a model's hundreds of links seconds
            RhinoDoc.AddRhinoObject += (s, e) => Changed(e.TheObject?.Document, e.ObjectId, e.TheObject?.Geometry, e.TheObject?.Attributes);
            RhinoDoc.DeleteRhinoObject += (s, e) => Changed(e.TheObject?.Document, e.ObjectId, null, null);
            RhinoDoc.ReplaceRhinoObject += (s, e) => Changed(e.NewRhinoObject?.Document, e.ObjectId, e.NewRhinoObject?.Geometry, e.NewRhinoObject?.Attributes);
            RhinoDoc.ModifyObjectAttributes += (s, e) => Changed(e.Document, e.RhinoObject?.Id ?? Guid.Empty, e.RhinoObject?.Geometry, e.NewAttributes);
            RhinoDoc.EndOpenDocument += (s, e) => Forget();
            RhinoDoc.NewDocument += (s, e) => Forget();
            RhinoDoc.CloseDocument += (s, e) => Forget(closing: true);
        }

        // A part with links (a text) or a task box (a curve, a character)
        private static bool Linked(GeometryBase geometry, ObjectAttributes attributes) =>
            attributes != null && (geometry is TextEntity && attributes.GetUserString(RhinoTextLayout.LinksKey) != null || attributes.GetUserString(RhinoTextLayout.TaskKey) != null);

        // One object changed (added, deleted -- geometry null --, replaced, its attributes): its glyphs and
        // letters forgotten, whether it's a part with links known again
        private static void Changed(RhinoDoc doc, Guid id, GeometryBase geometry, ObjectAttributes attributes)
        {
            if (id == Guid.Empty)
                return;
            lock (s_glyphs)
            {
                foreach (var key in s_glyphs.Keys.Where(k => k.Item1 == id).ToList())
                    s_glyphs.Remove(key);
            }
            MarkdownLinkLetters.Forget(id);
            if (s_linked == null || (doc != null && !ReferenceEquals(doc, s_linkedDoc)))
                return; // found when needed
            if (geometry != null && Linked(geometry, attributes))
                s_linked.Add(id);
            else
                s_linked.Remove(id);
        }

        // What was found in the document: found again next time (the document closing: its hint goes too)
        private static void Forget(bool closing = false)
        {
            s_linked = null;
            lock (s_glyphs)
                s_glyphs.Clear();
            MarkdownLinkLetters.Forget(Guid.Empty);
            if (!closing)
                return;
            s_linkedDoc = null;
            if (s_hint != null)
            {
                s_hint.Enabled = false;
                s_hint.Text = null;
            }
        }

        // --- the hint over a link -------------------------------------------------------------------

        private static HashSet<Guid> s_linked;  // the parts with links and the task boxes (null: to find again)
        private static RhinoDoc s_linkedDoc;
        private static long s_lastMove;
        // By the object's id and serial number: RhinoCommon makes a new RhinoObject wrapper at
        // every lookup, so a table keyed by the wrapper never finds anything again
        private static readonly Dictionary<(Guid, uint), BoundingBox[]> s_glyphs = new Dictionary<(Guid, uint), BoundingBox[]>();
        private static HintConduit s_hint;

        private sealed class HintConduit : DisplayConduit
        {
            public Guid Viewport;
            public System.Drawing.Point At;
            public string Text;

            protected override void DrawForeground(DrawEventArgs e)
            {
                if (Text == null || e.Viewport.Id != Viewport)
                    return;
                // As large as a tooltip on this display; the box from the text's width and the
                // font's height, the text centered in it
                var scale = Math.Max(1.0, e.Display.DpiScale);
                var size = (int)Math.Round(12 * scale);
                var pad = (int)Math.Round(5 * scale);
                var corner = new Point2d(At.X + 14 * scale, At.Y + 20 * scale);
                var measured = e.Display.Measure2dText(Text, corner, false, 0, size, "Segoe UI");
                var width = measured.Width > 0 ? measured.Width : (int)(size * 0.55 * Text.Length);
                var back = new System.Drawing.Rectangle((int)corner.X, (int)corner.Y, width + 2 * pad, (int)Math.Round(size * 1.3) + 2 * pad);
                e.Display.Draw2dRectangle(back, System.Drawing.Color.FromArgb(120, 120, 120), 1, System.Drawing.Color.FromArgb(255, 255, 225));
                e.Display.Draw2dText(Text, System.Drawing.Color.FromArgb(30, 30, 30), new Point2d(back.X + back.Width / 2.0, back.Y + back.Height / 2.0), true, size, "Segoe UI");
            }
        }

        /// <summary>For a test: the hint shown now (null: none).</summary>
        internal static string Hint => s_hint?.Enabled == true ? s_hint.Text : null;

        protected override void OnMouseMove(MouseCallbackEventArgs e)
        {
            try
            {
                if (e.MouseButton != MouseButton.None || Rhino.Commands.Command.InCommand())
                {
                    ShowHint(null, default, null);
                    return;
                }
                var now = System.Environment.TickCount64;
                if (now - s_lastMove < 50)
                    return;
                s_lastMove = now;
                var url = LinkUnder(e.View, e.ViewportPoint);
                ShowHint(e.View, e.ViewportPoint, url == null ? null : Describe(e.View.Document, url) + MarkdownLinks.HowToFollow(MiniVivisectionSettings.MarkdownLinkFollow, true));
            }
            catch (Exception)
            {
                // a hint isn't worth a message
            }
        }

        private static void ShowHint(RhinoView view, System.Drawing.Point at, string text)
        {
            if (text == null)
            {
                if (s_hint?.Enabled == true)
                {
                    s_hint.Enabled = false;
                    s_hint.Text = null;
                    RhinoDoc.ActiveDoc?.Views.Redraw();
                }
                return;
            }
            if (s_hint == null)
                s_hint = new HintConduit();
            var viewport = view.ActiveViewport.Id;
            if (s_hint.Enabled && s_hint.Text == text && s_hint.Viewport == viewport && Math.Abs(s_hint.At.X - at.X) + Math.Abs(s_hint.At.Y - at.Y) < 3)
                return;
            s_hint.Viewport = viewport;
            s_hint.At = at;
            s_hint.Text = text;
            s_hint.Enabled = true;
            view.Redraw();
        }

        /// <summary>The text parts with links in <paramref name="doc"/>, visible and not locked (kept up
        /// to date as objects change). Also MarkdownLinkLetters'.</summary>
        internal static IEnumerable<RhinoObject> LinkedParts(RhinoDoc doc)
        {
            if (s_linked == null || !ReferenceEquals(s_linkedDoc, doc))
            {
                s_linkedDoc = doc;
                s_linked = new HashSet<Guid>(doc.Objects.GetObjectList(new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true, IncludeLights = false, IncludeGrips = false })
                    .Where(o => Linked(o.Geometry, o.Attributes)).Select(o => o.Id));
            }
            foreach (var id in s_linked.ToList())
            {
                var o = doc.Objects.FindId(id);
                if (o != null && o.Visible && !o.IsLocked && o.Geometry is TextEntity && o.Attributes.GetUserString(RhinoTextLayout.LinksKey) != null)
                    yield return o;
            }
        }

        /// <summary>The task boxes in <paramref name="doc"/> -- their curves, or their character --,
        /// visible (found with the parts with links).</summary>
        internal static IEnumerable<RhinoObject> TaskBoxes(RhinoDoc doc)
        {
            if (s_linked == null || !ReferenceEquals(s_linkedDoc, doc))
                LinkedParts(doc).Count(); // finds them
            foreach (var id in s_linked.ToList())
            {
                var o = doc.Objects.FindId(id);
                if (o != null && o.Visible && !o.IsLocked && o.Attributes.GetUserString(RhinoTextLayout.TaskKey) != null)
                    yield return o;
            }
        }

        /// <summary>A task box clicked: its "link" -- task:id (one of its curves).</summary>
        private const string TaskScheme = "task:";

        // A task box's curve at p (its box on screen, a few pixels around), or null
        private static RhinoObject TaskBoxAt(RhinoDoc doc, RhinoViewport viewport, Point2d p)
        {
            foreach (var o in TaskBoxes(doc))
            {
                var box = o.Geometry.GetBoundingBox(true);
                if (!box.IsValid)
                    continue;
                var screen = box.GetCorners().Select(c => viewport.WorldToClient(c)).ToArray();
                if (p.X >= screen.Min(s => s.X) - 3 && p.X <= screen.Max(s => s.X) + 3 && p.Y >= screen.Min(s => s.Y) - 3 && p.Y <= screen.Max(s => s.Y) + 3)
                    return o;
            }
            return null;
        }

        // What a link (or a task box) under the mouse does, for the hint
        private static string Describe(RhinoDoc doc, string url)
        {
            if (!url.StartsWith(TaskScheme, StringComparison.Ordinal))
                return MarkdownLinks.Describe(url);
            var task = Guid.TryParse(url.Substring(TaskScheme.Length), out var id) ? MarkdownRhinoText.TaskOf(doc?.Objects.FindId(id)) : null;
            return task?.Done == true ? "Task: uncheck it" : "Task: check it";
        }

        /// <summary>The link under <paramref name="point"/> in <paramref name="view"/> -- the parts
        /// with links only, their glyphs' boxes kept -- or null.</summary>
        internal static string LinkUnder(RhinoView view, System.Drawing.Point point)
        {
            var doc = view?.Document;
            if (doc == null)
                return null;
            var viewport = view.ActiveViewport;
            var at = new Point2d(point.X, point.Y);
            var task = TaskBoxAt(doc, viewport, at);
            if (task != null)
                return TaskScheme + task.Id.ToString("D");
            foreach (var o in LinkedParts(doc))
            {
                var text = (TextEntity)o.Geometry;
                var quad = Quad(text, viewport);
                if (quad == null || !Inside(quad, at, 2))
                    continue;
                var url = LinkAt(doc, o, viewport, at);
                if (url != null)
                    return url;
            }
            return null;
        }

        // A text part's glyphs' boxes (world), kept for it
        private static BoundingBox[] GlyphBoxes(RhinoDoc doc, RhinoObject part, TextEntity text)
        {
            var key = (part.Id, part.RuntimeSerialNumber);
            lock (s_glyphs)
            {
                if (s_glyphs.TryGetValue(key, out var known))
                    return known;
            }
            BoundingBox[] boxes;
            var style = doc.DimStyles.FindId(text.DimensionStyleId) ?? doc.DimStyles.Current;
            var glyphs = text.CreateCurvesGrouped(style, false, 1.0, 0.0);
            boxes = (glyphs ?? new List<Curve[]>()).Select(glyph =>
            {
                var box = BoundingBox.Empty;
                foreach (var curve in glyph)
                    box.Union(curve.GetBoundingBox(true));
                return box;
            }).ToArray();
            lock (s_glyphs)
                s_glyphs[key] = boxes;
            return boxes;
        }

        // A press on a link, clicked as the setting says: taken from Rhino (no selection), followed when let go there
        protected override void OnMouseDown(MouseCallbackEventArgs e)
        {
            try
            {
                s_down = null;
                if (e.MouseButton != MouseButton.Left || e.ShiftKeyDown || Rhino.Commands.Command.InCommand())
                    return;
                var follow = MiniVivisectionSettings.MarkdownLinkFollow;
                if (!(follow == "Click" && !e.CtrlKeyDown || follow == "Ctrl+click" && e.CtrlKeyDown))
                    return;
                var url = LinkUnder(e.View, e.ViewportPoint);
                if (url == null)
                    return;
                e.Cancel = true;
                s_down = (url, e.ViewportPoint, e.CtrlKeyDown);
                ShowHint(null, default, null);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.General, "MarkdownText: a press on a link: {0}", Log.Describe(ex));
            }
        }

        protected override void OnMouseUp(MouseCallbackEventArgs e)
        {
            try
            {
                var down = s_down;
                if (down == null || e.MouseButton != MouseButton.Left)
                    return;
                var (url, at, ctrl) = down.Value;
                s_down = null;
                e.Cancel = true;
                // A hand moves a little while it clicks: Windows' drag distance, twice, is still a click
                var moved = Math.Max(Math.Abs(e.ViewportPoint.X - at.X), Math.Abs(e.ViewportPoint.Y - at.Y));
                var slack = 2 * Math.Max(4, Math.Max(GetSystemMetrics(SM_CXDRAG), GetSystemMetrics(SM_CYDRAG)));
                var there = LinkUnder(e.View, e.ViewportPoint) == url;
                if (moved > slack || !there)
                {
                    Log.Info(LogArea.General, "MarkdownText: a press on {0} let go {1} ({2} px away): not followed", url, there ? "too far" : "off the link", moved);
                    return;
                }
                // Ctrl+click: at once; a plain click: once a double-click can't come any more (it edits instead)
                FollowSoon(url, ctrl ? 0 : (int)Math.Max(100, GetDoubleClickTime()));
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.General, "MarkdownText: a click on a link: {0}", Log.Describe(ex));
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private const int SM_CXDRAG = 68, SM_CYDRAG = 69;

        /// <summary>The link followed after <paramref name="delay"/> ms -- on Rhino's UI thread, out
        /// of the mouse's handling -- unless a double-click came meanwhile (s_waiting cleared). A
        /// thread's timer and RhinoApp.InvokeOnUiThread: neither an Eto timer nor Rhino's Idle event,
        /// which a real click didn't always get to.</summary>
        private static void FollowSoon(string url, int delay)
        {
            var mine = ++s_click;
            s_waiting = url;
            Log.Info(LogArea.General, "MarkdownText: link {0} clicked: followed {1}", url, delay == 0 ? "now" : "in " + delay + " ms (unless double-clicked)");
            Action follow = () =>
            {
                if (mine != s_click || s_waiting != url)
                    return; // a double-click, or another click, since
                s_waiting = null;
                if (FollowProbe != null)
                {
                    FollowProbe(url);
                    return;
                }
                string error;
                if (url.StartsWith(TaskScheme, StringComparison.Ordinal))
                {
                    // A task box: checked or unchecked
                    var doc = RhinoDoc.ActiveDoc;
                    var box = doc != null && Guid.TryParse(url.Substring(TaskScheme.Length), out var id) ? doc.Objects.FindId(id) : null;
                    error = box == null ? "The task box is gone." : MarkdownRhinoText.ToggleTask(doc, box);
                }
                else
                {
                    // A relative file in the link: from the .3dm's folder
                    var path = RhinoDoc.ActiveDoc?.Path;
                    error = MarkdownLinks.Follow(url, string.IsNullOrEmpty(path) ? null : System.IO.Path.GetDirectoryName(path));
                }
                if (error != null)
                    RhinoApp.WriteLine("miniVivisection: " + error);
            };
            System.Threading.Timer timer = null;
            timer = new System.Threading.Timer(_ =>
            {
                timer?.Dispose();
                RhinoApp.InvokeOnUiThread(follow);
            }, null, delay, System.Threading.Timeout.Infinite);
        }

        // A double-click anywhere on the text, a link too: its Markdown edited (a click's link waiting isn't followed)
        protected override void OnMouseDoubleClick(MouseCallbackEventArgs e)
        {
            try
            {
                s_waiting = null; // a click's follow waiting: not now
                s_down = null;
                if (e.MouseButton != MouseButton.Left || e.CtrlKeyDown || e.ShiftKeyDown || Rhino.Commands.Command.InCommand())
                    return;
                var hit = Hit(e.View, e.ViewportPoint);
                if (hit == null)
                    return;
                e.Cancel = true; // not Rhino's text editor
                var (part, url) = hit.Value;
                if (Probe != null)
                {
                    Probe(part, url);
                    return;
                }
                var id = part.Id;
                EventHandler then = null;
                then = (s, a) =>
                {
                    RhinoApp.Idle -= then;
                    MarkdownTextCommand.EditPart(id);
                };
                RhinoApp.Idle += then;
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.General, "MarkdownText: double-click: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The Markdown text part at <paramref name="point"/> (the view's client
        /// coordinates), and the link there (null: none); null when there's none.</summary>
        internal static (RhinoObject Part, string Url)? Hit(RhinoView view, System.Drawing.Point point)
        {
            var doc = view?.Document;
            if (doc == null)
                return null;
            var viewport = view.ActiveViewport;
            var at = new Point2d(point.X, point.Y);
            RhinoObject bestText = null;
            var bestArea = double.MaxValue;
            RhinoObject bestCurve = null;
            var bestDistance = 5.0;
            var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = false, HiddenObjects = false, VisibleFilter = true, IncludeLights = false, IncludeGrips = false };
            foreach (var o in doc.Objects.GetObjectList(settings))
            {
                if (!MarkdownRhinoText.IsPart(o) || !o.Visible)
                    continue;
                if (o.Geometry is TextEntity text)
                {
                    var quad = Quad(text, viewport);
                    if (quad == null || !Inside(quad, at, 3))
                        continue;
                    var area = Math.Abs(Area(quad));
                    if (area < bestArea)
                    {
                        bestArea = area;
                        bestText = o;
                    }
                }
                else if (o.Geometry is Curve curve && bestText == null)
                {
                    var distance = Distance(curve, viewport, at);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestCurve = o;
                    }
                }
            }
            if (bestText != null)
                return (bestText, LinkAt(doc, bestText, viewport, at));
            if (bestCurve != null)
                return (bestCurve, null);
            return null;
        }

        // A text's box, in its plane, on screen (four corners in order)
        private static Point2d[] Quad(TextEntity text, RhinoViewport viewport)
        {
            text.GetBoundingBox(text.Plane, out var box);
            if (!box.IsValid)
                return null;
            var corners = box.GetCorners();
            return corners.Take(4).Select(c => viewport.WorldToClient(c)).ToArray();
        }

        private static double Area(Point2d[] polygon)
        {
            var area = 0.0;
            for (var i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2;
        }

        // Inside a convex polygon, or within margin pixels of its edges
        private static bool Inside(Point2d[] polygon, Point2d p, double margin)
        {
            var sign = 0;
            var inside = true;
            for (var i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                var cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                var s = Math.Sign(cross);
                if (s == 0)
                    continue;
                if (sign == 0)
                    sign = s;
                else if (s != sign)
                    inside = false;
            }
            if (inside)
                return true;
            for (var i = 0; i < polygon.Length; i++)
            {
                if (SegmentDistance(polygon[i], polygon[(i + 1) % polygon.Length], p) <= margin)
                    return true;
            }
            return false;
        }

        private static double SegmentDistance(Point2d a, Point2d b, Point2d p)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = dx * dx + dy * dy;
            var t = length <= 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length));
            var x = a.X + t * dx - p.X;
            var y = a.Y + t * dy - p.Y;
            return Math.Sqrt(x * x + y * y);
        }

        // A curve's distance on screen: its polyline's segments, else points along it
        private static double Distance(Curve curve, RhinoViewport viewport, Point2d p)
        {
            Point3d[] points;
            if (curve.TryGetPolyline(out var polyline))
                points = polyline.ToArray();
            else
                points = Enumerable.Range(0, 33).Select(i => curve.PointAt(curve.Domain.ParameterAt(i / 32.0))).ToArray();
            var screen = points.Select(q => viewport.WorldToClient(q)).ToArray();
            var best = double.MaxValue;
            for (var i = 0; i + 1 < screen.Length; i++)
                best = Math.Min(best, SegmentDistance(screen[i], screen[i + 1], p));
            return best;
        }

        // The link of a text part under p: its glyph there, counted in its non-blank characters
        internal static string LinkAt(RhinoDoc doc, RhinoObject part, RhinoViewport viewport, Point2d p)
        {
            var links = RhinoTextLayout.ParseLinks(part.Attributes.GetUserString(RhinoTextLayout.LinksKey));
            if (links.Count == 0 || !(part.Geometry is TextEntity text))
                return null;
            if (RhinoTextLayout.Shown(text) != part.Attributes.GetUserString(RhinoTextLayout.ShownKey))
                return null; // changed in Rhino: its characters may not be where they were
            var index = 0;
            foreach (var box in GlyphBoxes(doc, part, text))
            {
                if (box.IsValid)
                {
                    var screen = box.GetCorners().Select(c => viewport.WorldToClient(c)).ToArray();
                    var minX = screen.Min(s => s.X) - 2;
                    var maxX = screen.Max(s => s.X) + 2;
                    var minY = screen.Min(s => s.Y) - 2;
                    var maxY = screen.Max(s => s.Y) + 2;
                    if (p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY)
                    {
                        var at = index;
                        return links.Where(l => at >= l.Start && at < l.Start + l.Length).Select(l => l.Url).FirstOrDefault();
                    }
                }
                index++;
            }
            return null;
        }

        /// <summary>For a test: the glyphs' count of a text part, and its non-blank characters' (they're counted alike).</summary>
        internal static (int Glyphs, int Characters) GlyphsOf(RhinoDoc doc, RhinoObject part)
        {
            if (!(part.Geometry is TextEntity text))
                return (0, 0);
            var style = doc.DimStyles.FindId(text.DimensionStyleId) ?? doc.DimStyles.Current;
            var glyphs = text.CreateCurvesGrouped(style, false, 1.0, 0.0);
            return (glyphs?.Count ?? 0, RhinoTextLayout.Shown(text).Count(c => !char.IsWhiteSpace(c)));
        }
    }
}
