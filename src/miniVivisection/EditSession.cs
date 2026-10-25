using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Watched values edited as real Rhino objects, written back into the paused
    /// script. "Edit in Rhino" adds a value -- or each item of a list -- as an object on
    /// layer "Script Editor Watch::Edit". When one is changed with Rhino's tools (moved,
    /// control points dragged, undone, ...), its new geometry is turned back into the
    /// variable's type and assigned in the paused frame (<c>crv = ...</c>,
    /// <c>curves[2] = ...</c>). At every pause the objects take the script's current
    /// values; they are removed when the run ends. Python scripts only: assigning needs
    /// the paused Python frame.
    /// </summary>
    internal sealed class EditSession : IDisposable
    {
        private sealed class Target
        {
            public IDebugControls Controls;
            public string Watch;       // the edited expression (hidden from the drawing)
            public string Expression;  // what is assigned: the watch, or watch[i]
            public object Original;    // the script's value (or the drawn item's source), for its type
            public string Mode = "geometry"; // geometry: item by item; inverse / raw: the watch as a whole (minivivisection_visualize.edit_mode)
            public int Index;          // inverse / raw: the drawn item's place
            public string Interpretation = "auto";
            public string Frame;       // the paused frame it came from (file, function, thread); null: not known
        }

        /// <summary>Why <paramref name="target"/> can't be written back now: the script is paused
        /// in another frame than the one the edit started in (stepped out, or another thread) --
        /// the name there is another variable, or none, and assigning would make a new one.
        /// Null: it can. On the paused thread.</summary>
        private static string OtherFrame(Target target)
        {
            if (target.Frame == null)
                return null;
            var now = PythonBridge.PausedFrameKey();
            if (now == null || now == target.Frame)
                return null;
            return "the script is paused in another place now (" + Describe(now) + ", the edit came from " + Describe(target.Frame) +
                   ") -- not written back; edit it again here";
        }

        private static string Describe(string frameKey)
        {
            var parts = frameKey.Split('|');
            return parts.Length >= 2 ? (parts[1] == "<module>" ? "the script" : parts[1] + "()") + " in " + System.IO.Path.GetFileName(parts[0]) : frameKey;
        }

        private readonly ScriptEditorHook _hook;
        private readonly WatchConduit _conduit;
        private readonly object _lock = new object();
        private readonly Dictionary<Guid, Target> _targets = new Dictionary<Guid, Target>();
        private readonly HashSet<Guid> _ownChanges = new HashSet<Guid>();
        private readonly HashSet<Guid> _replacing = new HashSet<Guid>();
        // Objects deleted while edited: Rhino's undo (and redo) of an edit deletes the object
        // and undeletes the earlier one under the same id -- linked again then.
        private readonly Dictionary<Guid, Target> _deleted = new Dictionary<Guid, Target>();
        private readonly List<(Target Target, GeometryBase Geometry)> _pending = new List<(Target Target, GeometryBase Geometry)>();
        private bool _flushQueued;

        public EditSession(ScriptEditorHook hook, WatchConduit conduit)
        {
            _hook = hook;
            _conduit = conduit;
            _conduit.EditedLabelPoint = LabelPointOf;
            RhinoDoc.ReplaceRhinoObject += OnReplace;
            RhinoDoc.DeleteRhinoObject += OnDelete;
            RhinoDoc.UndeleteRhinoObject += OnUndelete;
            RhinoDoc.CloseDocument += OnClose;
        }

        // The model closed (replaced by another): its edit objects are gone with it; what was
        // being edited isn't any more -- the next pause shows its watches in the new model
        private void OnClose(object sender, DocumentEventArgs e)
        {
            lock (_lock)
            {
                if (_targets.Count == 0 && _deleted.Count == 0 && _pending.Count == 0)
                    return;
                _targets.Clear();
                _deleted.Clear();
                _pending.Clear();
                _ownChanges.Clear();
                _replacing.Clear();
            }
            Log.Info(LogArea.Edit, "the model closed: its edit objects are forgotten");
        }

        public int Count
        {
            get
            {
                lock (_lock)
                    return _targets.Count;
            }
        }

        public void Dispose()
        {
            RhinoDoc.ReplaceRhinoObject -= OnReplace;
            RhinoDoc.DeleteRhinoObject -= OnDelete;
            RhinoDoc.UndeleteRhinoObject -= OnUndelete;
            RhinoDoc.CloseDocument -= OnClose;
        }

        /// <summary>"Edit in Rhino": <paramref name="expression"/>'s value as Rhino objects
        /// -- drawn as by <paramref name="interpretation"/> (Interpret As).</summary>
        public void Start(IDebugControls controls, string expression, string interpretation = "auto")
        {
            if (!ScriptEditorHook.IsPythonPause(controls))
            {
                UserLog.Warning("Edit in Rhino works for Python 3 scripts only (not IronPython 2 or C#): {0} isn't edited.", expression);
                return;
            }
            if (!_hook.RunOnPausedThread(controls, () => StartOnPausedThread(controls, expression, interpretation)))
                UserLog.Warning("Can't edit {0}: {1}.", expression, WatchActions.NotReachable);
        }

        /// <summary>How a value can be edited (minivivisection_visualize.edit_mode); "geometry"
        /// without the pipeline (no Python: C#).</summary>
        public static string ModeOf(object value, string interpretation)
        {
            if (!PythonBridge.IsRunning)
                return "geometry";
            try
            {
                return PythonBridge.EditMode(value, interpretation);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Edit, "edit mode: {0}", Log.Describe(ex));
                return "geometry";
            }
        }

        private void StartOnPausedThread(IDebugControls controls, string expression, string interpretation)
        {
            if (!ScriptEditorHook.TryEvaluate(controls, expression, out var value, out var error))
            {
                UserLog.Warning("Can't edit {0}: {1}", expression, error);
                return;
            }
            var frame = PythonBridge.PausedFrameKey();
            var mode = ModeOf(value, interpretation);
            if (mode.StartsWith("readonly"))
            {
                UserLog.Warning("Can't edit {0} in Rhino: {1}.", expression, mode.Substring(mode.IndexOf(':') + 1));
                return;
            }
            if (mode != "geometry")
            {
                // The drawn items, written back as a whole: through the inverse, or into the raw data.
                var drawn = WatchGeometry.Drawable(value, interpretation, true, out _, out _, out var problem);
                if (problem != null)
                {
                    UserLog.Warning("Can't edit {0}: {1}", expression, problem);
                    return;
                }
                var whole = drawn.Select((item, i) => (Target: new Target { Controls = controls, Watch = expression, Expression = expression, Original = item.Source, Mode = mode, Index = i, Interpretation = interpretation, Frame = frame },
                        Geometry: item.ToDocumentGeometry(), Arrow: item.Arrow.HasValue))
                    .Where(o => o.Geometry != null).ToList();
                if (whole.Count == 0)
                {
                    UserLog.Warning("{0} has nothing to edit as Rhino objects.", expression);
                    return;
                }
                RhinoApp.InvokeOnUiThread(new Action(() => AddObjects(controls, expression, whole)));
                return;
            }
            // A list's items one by one, each written back as expression[i].
            var elements = PythonBridge.IsPython(value)
                ? PythonBridge.Items(value)
                : value is IList list && !(value is string) ? list.Cast<object>().ToList() : null;
            var pieces = elements != null
                ? elements.Select((element, i) => (Expression: string.Format("{0}[{1}]", expression, i), Value: element)).ToList()
                : new List<(string Expression, object Value)> { (expression, value) };

            var objects = new List<(Target Target, GeometryBase Geometry, bool Arrow)>();
            foreach (var piece in pieces)
            {
                if (piece.Value is Guid)
                {
                    UserLog.Info("{0} is a Rhino object already -- edit it directly.", piece.Expression);
                    continue;
                }
                var item = WatchGeometry.ItemOf(piece.Value);
                var geometry = item?.ToDocumentGeometry();
                if (geometry == null)
                    continue;
                var target = new Target { Controls = controls, Watch = expression, Expression = piece.Expression, Original = piece.Value, Frame = frame };
                objects.Add((target, geometry, item.Arrow.HasValue));
            }
            if (objects.Count == 0)
            {
                UserLog.Warning("{0} has nothing to edit as Rhino objects ({1}).", expression, ScriptEditorHook.TypeName(value));
                return;
            }
            RhinoApp.InvokeOnUiThread(new Action(() => AddObjects(controls, expression, objects)));
        }

        private void AddObjects(IDebugControls controls, string expression, List<(Target Target, GeometryBase Geometry, bool Arrow)> objects)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;
            RemoveObjects(doc, t => t.Controls == controls && t.Watch == expression); // editing it again: fresh objects
            var layer = WatchActions.LayerIndex(doc, "Edit");
            var color = WatchGeometry.ColorFor(expression);
            foreach (var o in objects)
            {
                var attributes = WatchActions.Attributes(layer, o.Target.Expression, color);
                if (o.Arrow)
                    attributes.ObjectDecoration = ObjectDecoration.EndArrowhead;
                var id = doc.Objects.Add(o.Geometry, attributes);
                if (id == Guid.Empty)
                    continue;
                lock (_lock)
                    _targets[id] = o.Target;
            }
            UpdateHidden();
            doc.Views.Redraw();
            UserLog.Info("Editing {0} as {1} Rhino object(s) on layer {2}::Edit -- changes go back into the script while it is paused.",
                expression, objects.Count, WatchActions.RootLayer);
        }

        /// <summary>A Rhino edit of one of the objects: its geometry back into the
        /// script, as the variable's type -- once Rhino is done. This event comes
        /// before the replace is finished: the document still holds the old object, and
        /// writing back here redraws (Refresh) with the object at its old place for a
        /// moment. So the edit is queued and written back from the next UI message, all
        /// edits of one command together, with one refresh.</summary>
        private void OnReplace(object sender, RhinoReplaceObjectEventArgs e)
        {
            Target target;
            lock (_lock)
            {
                if (!_targets.TryGetValue(e.ObjectId, out target))
                    return;
                _replacing.Add(e.ObjectId); // a delete follows, as part of the replace
                if (_ownChanges.Contains(e.ObjectId))
                    return;
            }
            // The new geometry now: the object may be replaced again before the queue runs.
            Queue(target, e.NewRhinoObject);
        }

        /// <summary>An edit of an edited object to write back, from the next UI message.</summary>
        private void Queue(Target target, Rhino.DocObjects.RhinoObject changed)
        {
            var geometry = target.Mode == "geometry" ? changed?.Geometry?.Duplicate() : null;
            if (target.Mode == "geometry" && geometry == null)
                return;
            lock (_lock)
            {
                _pending.RemoveAll(p => p.Target == target);
                _pending.Add((target, geometry));
                if (_flushQueued)
                    return;
                _flushQueued = true;
            }
            Eto.Forms.Application.Instance.AsyncInvoke(WritePending);
        }

        /// <summary>The queued edits back into the script (UI thread, after the command
        /// that made them): per paused script, one job on its paused thread and one
        /// refresh.</summary>
        private void WritePending()
        {
            List<(Target Target, GeometryBase Geometry)> pending;
            lock (_lock)
            {
                pending = _pending.ToList();
                _pending.Clear();
                _flushQueued = false;
            }
            foreach (var group in pending.GroupBy(p => p.Target.Controls))
            {
                var controls = group.Key;
                var jobs = new List<Func<bool>>();
                var wholes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (target, geometry) in group)
                {
                    var name = target.Mode == "geometry" ? target.Expression : target.Watch;
                    if (controls.State != DebugControlsState.Paused)
                    {
                        UserLog.Warning("{0}: the script isn't paused -- not written back.", name);
                        continue;
                    }
                    if (target.Mode != "geometry")
                    {
                        // The whole watch at once, once: its other objects edited in the same command come along.
                        if (wholes.Add(target.Watch + "|" + target.Mode))
                        {
                            var geometries = WholeGeometry(target);
                            if (geometries != null)
                                jobs.Add(() => WriteBackWhole(target, geometries));
                        }
                        continue;
                    }
                    var value = WatchGeometry.ToOriginalType(geometry, target.Original);
                    if (value == null)
                    {
                        UserLog.Warning("{0}: a {1} can't become a {2} again -- not written back.",
                            target.Expression, geometry.GetType().Name, target.Original?.GetType().Name ?? "value");
                        continue;
                    }
                    jobs.Add(() => WriteBack(target, value));
                }
                if (jobs.Count == 0)
                    continue;
                var reached = _hook.RunOnPausedThread(controls, () =>
                {
                    var wrote = false;
                    foreach (var job in jobs)
                        wrote |= job();
                    if (wrote)
                        _hook.Refresh(controls);
                });
                if (!reached)
                    UserLog.Warning("{0} not written back: {1}.", string.Join(", ", group.Select(p => p.Target.Mode == "geometry" ? p.Target.Expression : p.Target.Watch).Distinct()),
                        WatchActions.NotReachable);
            }
        }

        /// <summary>Inverse / raw: every object of the watch, in drawing order, as the
        /// script's types; null when one is gone.</summary>
        private List<object> WholeGeometry(Target target)
        {
            List<KeyValuePair<Guid, Target>> group;
            lock (_lock)
                group = _targets.Where(t => t.Value.Controls == target.Controls && t.Value.Watch == target.Watch && t.Value.Mode == target.Mode)
                    .OrderBy(t => t.Value.Index).ToList();
            var doc = RhinoDoc.ActiveDoc;
            var geometries = new List<object>();
            foreach (var member in group)
            {
                var geometry = doc?.Objects.FindId(member.Key)?.Geometry?.Duplicate();
                if (geometry == null)
                {
                    UserLog.Warning("{0}: one of its objects is gone -- not written back.", target.Watch);
                    return null;
                }
                geometries.Add(WatchGeometry.ToOriginalType(geometry, member.Value.Original) ?? geometry);
            }
            return geometries;
        }

        /// <summary>Inverse / raw, on the paused thread: the inverse sees the whole edited
        /// geometry.</summary>
        private bool WriteBackWhole(Target target, List<object> geometries)
        {
            if (OtherFrame(target) is string elsewhere)
            {
                UserLog.Warning("{0}: {1}.", target.Watch, elsewhere);
                return false;
            }
            try
            {
                var how = PythonBridge.WriteBack(target.Watch, target.Interpretation, geometries);
                UserLog.Success("Wrote the edited {0} back ({1}).", target.Watch, how);
                return true;
            }
            catch (Exception ex)
            {
                UserLog.Error("Couldn't write {0} back: {1}", target.Watch, ScriptEditorHook.FirstLine(ex));
                return false;
            }
        }

        private bool WriteBack(Target target, object value)
        {
            if (OtherFrame(target) is string elsewhere)
            {
                UserLog.Warning("{0}: {1}.", target.Expression, elsewhere);
                return false;
            }
            try
            {
                PythonBridge.Assign(target.Expression, value);
                target.Original = value;
                UserLog.Success("Wrote the edited {0} back into the script.", target.Expression);
                return true;
            }
            catch (Exception ex)
            {
                UserLog.Error("Couldn't write {0} back: {1}", target.Expression, ScriptEditorHook.FirstLine(ex));
                return false;
            }
        }

        private void OnDelete(object sender, RhinoObjectEventArgs e)
        {
            lock (_lock)
            {
                if (_replacing.Remove(e.ObjectId) || _ownChanges.Contains(e.ObjectId))
                    return;
                if (!_targets.TryGetValue(e.ObjectId, out var target))
                    return;
                _targets.Remove(e.ObjectId);
                _deleted[e.ObjectId] = target;
            }
            UpdateHidden(); // deleted by the user: no longer edited -- unless an undo brings it back
        }

        /// <summary>Undo or redo of an edit (or of a delete): the object is back, with the
        /// geometry of that step -- edited again, and that geometry goes into the script.</summary>
        private void OnUndelete(object sender, RhinoObjectEventArgs e)
        {
            Target target;
            lock (_lock)
            {
                if (!_deleted.TryGetValue(e.ObjectId, out target))
                    return;
                _deleted.Remove(e.ObjectId);
                if (target.Controls.State != DebugControlsState.Paused)
                    return; // the run has ended: a plain object now
                _targets[e.ObjectId] = target;
            }
            UpdateHidden();
            Log.Debug(LogArea.Edit, "{0}: back by undo/redo, written back", target.Expression);
            Queue(target, e.TheObject);
        }

        /// <summary>At a pause (on the paused thread): the objects take the script's
        /// current values.</summary>
        public void Update(IDebugControls controls)
        {
            List<KeyValuePair<Guid, Target>> all;
            lock (_lock)
                all = _targets.Where(t => t.Value.Controls == controls).ToList();
            if (all.Count == 0)
                return;
            var updates = new List<(Guid Id, GeometryBase Geometry)>();
            // Inverse / raw: the watch drawn again, item by item into its objects (same count).
            foreach (var group in all.Where(t => t.Value.Mode != "geometry").GroupBy(t => t.Value.Watch))
            {
                var members = group.OrderBy(t => t.Value.Index).ToList();
                if (!ScriptEditorHook.TryEvaluate(controls, group.Key, out var whole, out _))
                    continue;
                var drawn = WatchGeometry.Drawable(whole, members[0].Value.Interpretation, true, out _, out _, out _);
                if (drawn.Count != members.Count)
                {
                    Log.Info(LogArea.Edit, "{0}: {1} item(s) drawn now, {2} object(s) edited -- left as they are", group.Key, drawn.Count, members.Count);
                    continue;
                }
                for (var i = 0; i < members.Count; i++)
                {
                    members[i].Value.Original = drawn[i].Source;
                    var geometry = drawn[i].ToDocumentGeometry();
                    if (geometry != null)
                        updates.Add((members[i].Key, geometry));
                }
            }
            var targets = all.Where(t => t.Value.Mode == "geometry").ToList();
            foreach (var target in targets)
            {
                if (!ScriptEditorHook.TryEvaluate(controls, target.Value.Expression, out var value, out _))
                    continue; // e.g. the list got shorter: the object stays as it is
                target.Value.Original = value;
                var geometry = WatchGeometry.ItemOf(value)?.ToDocumentGeometry();
                if (geometry != null)
                    updates.Add((target.Key, geometry));
            }
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                    return;
                foreach (var update in updates)
                {
                    lock (_lock)
                        _ownChanges.Add(update.Id);
                    try
                    {
                        Replace(doc, update.Id, update.Geometry);
                    }
                    finally
                    {
                        lock (_lock)
                        {
                            _ownChanges.Remove(update.Id);
                            _replacing.Remove(update.Id);
                        }
                    }
                }
                doc.Views.Redraw();
            }));
        }

        /// <summary>The watches with objects being edited.</summary>
        public List<string> Watches(IDebugControls controls)
        {
            lock (_lock)
                return _targets.Values.Where(t => t.Controls == controls).Select(t => t.Watch).Distinct().ToList();
        }

        /// <summary>The objects being edited for these (watch, item index) pairs; index -1:
        /// all of the watch's.</summary>
        public List<Guid> ObjectsOf(IDebugControls controls, HashSet<(string Name, int Index)> selected)
        {
            lock (_lock)
            {
                return _targets.Where(t => t.Value.Controls == controls && selected.Any(s => s.Name == t.Value.Watch &&
                        (s.Index < 0 || (t.Value.Mode == "geometry" ? t.Value.Expression == s.Name + "[" + s.Index + "]" : t.Value.Index == s.Index))))
                    .Select(t => t.Key).ToList();
            }
        }

        /// <summary>Whether <paramref name="watch"/> has objects being edited.</summary>
        public bool IsEditing(IDebugControls controls, string watch)
        {
            lock (_lock)
                return _targets.Values.Any(t => t.Controls == controls && t.Watch == watch);
        }

        /// <summary>The edited expressions of the objects <paramref name="ids"/> (the
        /// watch, and what is written back: the watch or watch[i]).</summary>
        public List<(IDebugControls Controls, string Watch, string Expression)> TargetsOf(IEnumerable<Guid> ids)
        {
            lock (_lock)
                return ids.Where(_targets.ContainsKey).Select(id => _targets[id]).Select(t => (t.Controls, t.Watch, t.Expression)).ToList();
        }

        /// <summary>Stops editing <paramref name="watch"/>: its objects go (UI thread).</summary>
        public void Stop(IDebugControls controls, string watch)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null && RemoveObjects(doc, t => t.Controls == controls && t.Watch == watch) > 0)
                doc.Views.Redraw();
        }

        /// <summary>The run ended: its objects go.</summary>
        public void End(IDebugControls controls)
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc != null && RemoveObjects(doc, t => t.Controls == controls) > 0)
                    doc.Views.Redraw();
            }));
        }

        /// <summary>MiniVivisection StopEditing: all objects go.</summary>
        public int EndAll(RhinoDoc doc)
        {
            var removed = RemoveObjects(doc, t => true);
            doc.Views.Redraw();
            return removed;
        }

        private int RemoveObjects(RhinoDoc doc, Func<Target, bool> which)
        {
            List<Guid> ids;
            lock (_lock)
            {
                ids = _targets.Where(t => which(t.Value)).Select(t => t.Key).ToList();
                foreach (var id in ids)
                    _ownChanges.Add(id);
            }
            foreach (var id in ids)
            {
                // hidden or locked meanwhile too: they are the edit's objects
                MarkdownRhinoText.DeleteOwn(doc, doc.Objects.FindId(id));
            }
            lock (_lock)
            {
                foreach (var id in ids)
                {
                    _targets.Remove(id);
                    _ownChanges.Remove(id);
                }
            }
            UpdateHidden();
            return ids.Count;
        }

        /// <summary>The name dot's place for an edited watch: its first object, where it is
        /// now (it moves with the object). Drawing, UI thread.</summary>
        private Point3d? LabelPointOf(string watch)
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return null;
            Guid id;
            lock (_lock)
            {
                var first = _targets.Where(t => t.Value.Watch == watch).OrderBy(t => t.Value.Index).FirstOrDefault();
                if (first.Value == null)
                    return null;
                id = first.Key;
            }
            var geometry = doc.Objects.FindId(id)?.Geometry;
            return geometry == null ? (Point3d?)null : WatchedValue.LabelPointOf(geometry, BoundingBox.Unset);
        }

        private void UpdateHidden()
        {
            List<string> watches;
            lock (_lock)
                watches = _targets.Values.Select(t => t.Watch).Distinct().ToList();
            _conduit.SetHidden(watches);
        }

        private static void Replace(RhinoDoc doc, Guid id, GeometryBase geometry)
        {
            switch (geometry)
            {
                case Curve curve:
                    doc.Objects.Replace(id, curve);
                    break;
                case Brep brep:
                    doc.Objects.Replace(id, brep);
                    break;
                case Mesh mesh:
                    doc.Objects.Replace(id, mesh);
                    break;
                case Point point:
                    doc.Objects.Replace(id, point);
                    break;
                case PointCloud cloud:
                    doc.Objects.Replace(id, cloud);
                    break;
                case TextDot dot:
                    doc.Objects.Replace(id, dot);
                    break;
            }
        }
    }
}
