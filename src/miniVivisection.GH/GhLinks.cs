using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using MiniVivisection;

namespace MiniVivisection.GH
{
    /// <summary>
    /// Grasshopper's side of MarkdownLinks: gh:Name -- the canvas centered on the object (its
    /// nickname, name or instance id; a group by its name too; the active definition first,
    /// then the other open ones), a red circle around it for a moment -- and ghview:Name, the
    /// canvas's named view restored. A definition before the name (gh:grasshopper/gh11.gh#Ring)
    /// is opened first when it isn't open (File > Open's way: ScriptAccess_OpenDocument), and
    /// made the canvas's. The .rhp follows a gh: link of a Markdown text in Rhino by reflection
    /// (Follow), starting Grasshopper first when the link names a definition.
    /// </summary>
    internal static class GhLinks
    {
        private static readonly Color MarkColor = Color.FromArgb(230, 30, 30);
        private const double MarkSeconds = 2.5;
        private const int Glide = 250; // ms: the canvas moving there

        private static readonly List<(IGH_DocumentObject Object, DateTime Until)> s_marks = new List<(IGH_DocumentObject, DateTime)>();
        private static System.Windows.Forms.Timer s_timer;
        private static GH_Canvas s_canvas;

        /// <summary>When the .gha loads: this copy of MarkdownLinks follows gh: links here.</summary>
        public static void Install() => MarkdownLinks.Grasshopper = Follow;

        /// <summary>A gh: or ghview: link's target (a relative definition from
        /// <paramref name="folder"/>, the linking file's); what went wrong, or null.</summary>
        public static string Follow(string scheme, string target, string folder)
        {
            var canvas = Instances.ActiveCanvas;
            var editor = Instances.DocumentEditor;
            if (canvas == null || editor == null)
                return "Open Grasshopper first: the link goes to " + scheme + ":" + target + ".";
            if (!editor.Visible)
                editor.Show();
            if (editor.WindowState == System.Windows.Forms.FormWindowState.Minimized)
                editor.WindowState = System.Windows.Forms.FormWindowState.Normal;

            // A definition named: open (or opened now), on the canvas
            var (file, name) = MarkdownLinks.Split(target);
            GH_Document only = null;
            if (file != null)
            {
                var (path, error) = MarkdownLinks.Resolve(file, folder);
                if (path == null)
                    return error;
                only = Opened(path);
                if (only == null)
                {
                    editor.ScriptAccess_OpenDocument(path);
                    only = Opened(path);
                    if (only == null)
                        return "Grasshopper didn't open " + path + ".";
                }
                if (!ReferenceEquals(canvas.Document, only))
                    canvas.Document = only;
                if (name.Length == 0)
                    return null;
            }

            if (scheme == "ghview")
            {
                var named = canvas.Document?.Properties.ViewList.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
                if (named == null)
                    return "No named view \"" + name + "\" on the Grasshopper canvas (ghview:" + target + ").";
                named.SetToViewport(canvas, Glide);
                return null;
            }
            var (doc, found) = Find(name, canvas.Document, only);
            if (found == null)
                return "No Grasshopper object named \"" + name + "\" (gh:" + target + ").";
            if (!ReferenceEquals(doc, canvas.Document))
                canvas.Document = doc;
            var bounds = found.Attributes.Bounds;
            var center = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            new GH_NamedView("", center, canvas.Viewport.Zoom, GH_NamedViewType.center).SetToViewport(canvas, Glide);
            Mark(canvas, found);
            return null;
        }

        // The open definition saved as path
        private static GH_Document Opened(string path) => Instances.DocumentServer.FirstOrDefault(d =>
            !string.IsNullOrEmpty(d.FilePath) && string.Equals(System.IO.Path.GetFullPath(d.FilePath), path, StringComparison.OrdinalIgnoreCase));

        // The object a link names: by instance id, else nickname, else name -- in the definition
        // the link names (only), else the active definition first, then the other open ones
        private static (GH_Document Document, IGH_DocumentObject Object) Find(string target, GH_Document active, GH_Document only = null)
        {
            var documents = new List<GH_Document>();
            if (only != null)
                documents.Add(only);
            else if (active != null)
                documents.Add(active);
            if (only == null)
                documents.AddRange(Instances.DocumentServer.Where(d => !ReferenceEquals(d, active)));
            var isId = Guid.TryParse(target, out var id);
            foreach (var doc in documents)
            {
                var found = isId ? doc.FindObject(id, true)
                    : doc.Objects.FirstOrDefault(o => string.Equals(o.NickName, target, StringComparison.OrdinalIgnoreCase))
                      ?? doc.Objects.FirstOrDefault(o => string.Equals(o.Name, target, StringComparison.OrdinalIgnoreCase));
                if (found?.Attributes != null)
                    return (doc, found.Attributes.GetTopLevel?.DocObject ?? found);
            }
            return (null, null);
        }

        /// <summary>A red circle around <paramref name="found"/> on the canvas, fading out after a moment.</summary>
        public static void Mark(GH_Canvas canvas, IGH_DocumentObject found)
        {
            if (!ReferenceEquals(s_canvas, canvas))
            {
                if (s_canvas != null)
                    s_canvas.CanvasPostPaintObjects -= Paint;
                s_canvas = canvas;
                canvas.CanvasPostPaintObjects += Paint;
            }
            s_marks.RemoveAll(m => m.Until < DateTime.UtcNow);
            s_marks.Add((found, DateTime.UtcNow.AddSeconds(MarkSeconds)));
            if (s_timer == null)
            {
                s_timer = new System.Windows.Forms.Timer { Interval = 50 };
                s_timer.Tick += (s, e) =>
                {
                    s_marks.RemoveAll(m => m.Until < DateTime.UtcNow.AddSeconds(-0.2));
                    s_canvas?.Invalidate();
                    if (s_marks.Count == 0)
                        s_timer.Stop();
                };
            }
            s_timer.Start();
            canvas.Invalidate();
        }

        /// <summary>For a test: what's marked now.</summary>
        internal static IGH_DocumentObject[] Marked => s_marks.Where(m => m.Until > DateTime.UtcNow).Select(m => m.Object).ToArray();

        private static void Paint(GH_Canvas canvas)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var (found, until) in s_marks)
                {
                    var left = (until - now).TotalSeconds;
                    if (left <= 0 || found.Attributes == null || !ReferenceEquals(found.OnPingDocument(), canvas.Document))
                        continue;
                    var bounds = found.Attributes.Bounds;
                    var center = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                    var radius = (float)Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height) / 2 + 12;
                    var alpha = (int)(255 * Math.Min(1, left / 0.8));
                    var zoom = Math.Max(0.05f, canvas.Viewport.Zoom);
                    using (var pen = new Pen(Color.FromArgb(alpha, MarkColor), 4f / zoom))
                        canvas.Graphics.DrawEllipse(pen, center.X - radius, center.Y - radius, 2 * radius, 2 * radius);
                }
            }
            catch (Exception)
            {
                // a paint isn't the place for it
            }
        }
    }
}
