using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;

namespace MiniVivisection.GH
{
    /// <summary>
    /// While a script component is paused in its solution (the .rhp's GrasshopperPausedData, by
    /// reflection):
    ///
    /// - <see cref="Recompute"/>: what changed meanwhile (the objects whose ExpireSolution the
    ///   pause guard held back) is computed again with what lies downstream of it -- now, not
    ///   only when the pause ends. The paused component isn't run again: its outputs show the data
    ///   of its last finished run (<see cref="Remember"/>, taken before a new run clears it), so
    ///   what's downstream of it computes from that. Script components aren't run either (one
    ///   would start a second debug run inside the pause): their outputs as they are. The held
    ///   changes are still done properly when the solution ends (the guard's replay).
    /// - <see cref="Mark"/>: everything downstream of the paused component -- its data most
    ///   probably isn't right any more -- gets an orange veil and a dashed border on the canvas,
    ///   the wires from the paused component and between such objects a dashed orange line.
    /// </summary>
    public static class PausedSolution
    {
        private static readonly ConditionalWeakTable<IGH_Component, Dictionary<Guid, IGH_Structure>> s_last = new ConditionalWeakTable<IGH_Component, Dictionary<Guid, IGH_Structure>>();

        /// <summary>A script component's outputs before its data is cleared (its ExpireSolution, the .rhp's
        /// guard): kept as its last good data. Not when they're empty (a run that put out nothing yet).</summary>
        public static void Remember(object component)
        {
            if (!(component is IGH_Component c) || c.Params.Output.All(p => p.VolatileDataCount == 0))
                return;
            var copies = new Dictionary<Guid, IGH_Structure>();
            foreach (var output in c.Params.Output)
            {
                var data = output.VolatileData;
                var copy = data.GetType().GetMethod("ShallowDuplicate", Type.EmptyTypes)?.Invoke(data, null) as IGH_Structure;
                if (copy != null)
                    copies[output.InstanceGuid] = copy;
            }
            s_last.Remove(c);
            s_last.Add(c, copies);
        }

        // Inputs filled from a frozen component's last data while paused: expired when the pause
        // ends (ExpireAfterPause), so they take its new data -- Grasshopper's own expiry of what
        // changed doesn't reach them.
        private static readonly List<IGH_Param> s_dirty = new List<IGH_Param>();

        /// <summary>The pause is over (the .rhp's guard, before its replay): the inputs filled from a paused or
        /// script component's last data expired; their documents (each wants a new solution).</summary>
        public static object[] ExpireAfterPause()
        {
            List<IGH_Param> dirty;
            lock (s_dirty)
            {
                dirty = s_dirty.ToList();
                s_dirty.Clear();
            }
            var docs = new List<GH_Document>();
            foreach (var input in dirty)
            {
                var doc = input.OnPingDocument();
                if (doc == null)
                    continue;
                input.ExpireSolution(false);
                if (!docs.Contains(doc))
                    docs.Add(doc);
            }
            return docs.Cast<object>().ToArray();
        }

        /// <summary>Whether a component has remembered data (for the tests).</summary>
        public static bool HasLast(object component) => component is IGH_Component c && s_last.TryGetValue(c, out _);

        internal static bool IsScript(IGH_DocumentObject obj) => obj.GetType().GetMethod("SetSource", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null) != null;

        private static IGH_DocumentObject TopOf(object item) =>
            item is IGH_DocumentObject obj ? obj.Attributes?.GetTopLevel?.DocObject ?? obj : null;

        /// <summary>What's downstream of <paramref name="from"/> (not the objects themselves), through
        /// their outputs' recipients; <paramref name="stop"/>: not gone through (but included).</summary>
        internal static List<IGH_DocumentObject> Downstream(IEnumerable<IGH_DocumentObject> from, Func<IGH_DocumentObject, bool> stop)
        {
            var found = new List<IGH_DocumentObject>();
            var seen = new HashSet<IGH_DocumentObject>();
            var queue = new Queue<IGH_DocumentObject>(from);
            foreach (var start in from)
                seen.Add(start);
            while (queue.Count > 0)
            {
                var obj = queue.Dequeue();
                var outputs = obj is IGH_Component component ? component.Params.Output.AsEnumerable() : obj is IGH_Param param ? new[] { param } : Enumerable.Empty<IGH_Param>();
                foreach (var output in outputs)
                {
                    foreach (var recipient in output.Recipients)
                    {
                        var owner = TopOf(recipient);
                        if (owner == null || !seen.Add(owner))
                            continue;
                        found.Add(owner);
                        if (!stop(owner))
                            queue.Enqueue(owner);
                    }
                }
            }
            return found;
        }

        private static FieldInfo DataField(IGH_Param param)
        {
            for (var type = param.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField("m_data", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            return null;
        }

        /// <summary>The changed objects (<paramref name="held"/>) and what's downstream of them, computed
        /// again while <paramref name="paused"/> is paused in its solution; what was done, for V Log.</summary>
        public static string Recompute(object paused, object[] held)
        {
            var pausedComponent = paused as IGH_Component;
            var doc = pausedComponent?.OnPingDocument();
            if (doc == null || held == null || held.Length == 0)
                return null;
            bool Frozen(IGH_DocumentObject obj) => ReferenceEquals(obj, pausedComponent) || IsScript(obj);
            var starts = held.Select(TopOf).Where(o => o != null && !Frozen(o) && ReferenceEquals(o.OnPingDocument(), doc)).Distinct().ToList();
            var targets = starts.Concat(Downstream(starts, Frozen).Where(o => !Frozen(o))).Distinct().ToList();
            if (targets.Count == 0)
                return null;
            var fromPaused = new HashSet<IGH_DocumentObject>(Downstream(new[] { (IGH_DocumentObject)pausedComponent }, _ => false));

            // Script components not computed in this solution (the paused one: in the middle of its run)
            // show their last data and count as collected: a pull from them mustn't run them
            var swapped = new List<(IGH_Param Param, FieldInfo Field, object Data, GH_SolutionPhase Phase)>();
            foreach (var script in doc.Objects.OfType<IGH_Component>().Where(c => IsScript(c) && (ReferenceEquals(c, pausedComponent) || c.Phase != GH_SolutionPhase.Computed)))
            {
                s_last.TryGetValue(script, out var last);
                foreach (var output in script.Params.Output)
                {
                    var field = DataField(output);
                    var now = field?.GetValue(output);
                    if (field != null && last != null && last.TryGetValue(output.InstanceGuid, out var copy) && copy.GetType() == now?.GetType())
                        field.SetValue(output, copy.GetType().GetMethod("ShallowDuplicate", Type.EmptyTypes)?.Invoke(copy, null) ?? copy);
                    swapped.Add((output, field, now, output.Phase));
                    output.Phase = GH_SolutionPhase.Collected;
                }
            }
            var computed = 0;
            var problems = new List<string>();
            try
            {
                foreach (var obj in targets)
                {
                    if (obj is IGH_Component component)
                    {
                        foreach (var input in component.Params.Input)
                            input.ClearData();
                        component.ClearData();
                    }
                    else if (obj is IGH_Param param)
                        param.ClearData();
                }
                // what takes data from a frozen component: expired again when the pause ends
                lock (s_dirty)
                {
                    foreach (var component in targets.OfType<IGH_Component>())
                        s_dirty.AddRange(component.Params.Input.Where(i => i.Sources.Any(src => Frozen(TopOf(src))) && !s_dirty.Contains(i)));
                    s_dirty.AddRange(targets.OfType<IGH_Param>().Where(pr => pr.Sources.Any(src => Frozen(TopOf(src))) && !s_dirty.Contains(pr)));
                }
                foreach (var obj in doc.Objects.Where(targets.Contains).OfType<IGH_ActiveObject>())
                {
                    if (obj.Phase == GH_SolutionPhase.Computed)
                        continue;
                    try
                    {
                        obj.CollectData();
                        obj.ComputeData();
                        computed++;
                    }
                    catch (Exception ex)
                    {
                        problems.Add(obj.NickName + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                foreach (var (param, field, data, phase) in swapped)
                {
                    if (field != null && data != null)
                        field.SetValue(param, data);
                    param.Phase = phase;
                }
            }
            Instances.InvalidateCanvas();
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
            var fromOld = targets.Count(fromPaused.Contains);
            return string.Format("computed {0} object(s) again{1}{2}", computed,
                fromOld > 0 ? ", " + fromOld + " of them from the paused component's last data" : "",
                problems.Count > 0 ? "; failed: " + string.Join("; ", problems.Take(3)) : "");
        }

        // --- marking what depends on the paused component ------------------------------------------

        private static readonly Color Veil = Color.FromArgb(70, 255, 150, 30);
        private static readonly Color Edge = Color.FromArgb(230, 120, 20);
        private static IGH_Component s_marked;
        private static HashSet<IGH_DocumentObject> s_stale = new HashSet<IGH_DocumentObject>();
        private static GH_Canvas s_canvas;

        /// <summary>The objects marked now (for the tests).</summary>
        public static int MarkedCount => s_stale.Count;

        /// <summary>Marks what's downstream of <paramref name="paused"/> (null: no marks).</summary>
        public static void Mark(object paused)
        {
            s_marked = paused as IGH_Component;
            s_stale = s_marked == null ? new HashSet<IGH_DocumentObject>() : new HashSet<IGH_DocumentObject>(Downstream(new[] { (IGH_DocumentObject)s_marked }, _ => false));
            var canvas = Instances.ActiveCanvas;
            if (!ReferenceEquals(canvas, s_canvas))
            {
                if (s_canvas != null)
                    s_canvas.CanvasPostPaintObjects -= Paint;
                s_canvas = canvas;
                if (canvas != null)
                    canvas.CanvasPostPaintObjects += Paint;
            }
            canvas?.Invalidate();
        }

        private static void Paint(GH_Canvas canvas)
        {
            if (s_marked == null || s_stale.Count == 0 || !ReferenceEquals(canvas.Document, s_marked.OnPingDocument()))
                return;
            var g = canvas.Graphics;
            var smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using (var wire = new Pen(Edge, 2.5f) { DashStyle = DashStyle.Dash })
                {
                    foreach (var obj in s_stale)
                    {
                        var inputs = obj is IGH_Component c ? c.Params.Input.AsEnumerable() : obj is IGH_Param p ? new[] { p } : Enumerable.Empty<IGH_Param>();
                        foreach (var input in inputs)
                        {
                            foreach (var source in input.Sources)
                            {
                                var owner = TopOf(source);
                                if (!ReferenceEquals(owner, s_marked) && !s_stale.Contains(owner))
                                    continue;
                                using (var path = GH_Painter.ConnectionPath(source.Attributes.OutputGrip, input.Attributes.InputGrip, GH_WireDirection.right, GH_WireDirection.left))
                                    g.DrawPath(wire, path);
                            }
                        }
                    }
                }
                using (var veil = new SolidBrush(Veil))
                using (var border = new Pen(Edge, 2f) { DashStyle = DashStyle.Dash })
                using (var font = new Font(GH_FontServer.StandardAdjusted.FontFamily, 6f, FontStyle.Bold))
                using (var text = new SolidBrush(Edge))
                {
                    foreach (var obj in s_stale)
                    {
                        if (obj.Attributes == null)
                            continue;
                        var box = obj.Attributes.Bounds;
                        box.Inflate(3, 3);
                        g.FillRectangle(veil, box);
                        g.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
                        g.DrawString("out of date? (paused upstream)", font, text, box.X, box.Bottom + 1);
                    }
                }
            }
            finally
            {
                g.SmoothingMode = smoothing;
            }
        }
    }
}
