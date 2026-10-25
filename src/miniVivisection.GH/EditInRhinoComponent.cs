using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MiniVivisection.GH
{
    /// <summary>
    /// V Edit: edit geometry of a Grasshopper definition directly in Rhino. The items at the
    /// Index positions of every branch show in Rhino as ordinary objects ("edit objects",
    /// blue): select and edit them there with any tool that changes the object itself
    /// (Gumball, control points, Move, Rebuild...; Join or Explode make new objects, not taken). The outputs pass the input on unchanged until then. When an edit object
    /// changes in Rhino (RhinoDoc.ReplaceRhinoObject), its new geometry replaces that item --
    /// the definition downstream recomputes, the object turns red-orange, the component says
    /// "edited". From then on every solution puts the edit in place of the incoming item, also
    /// after the input changed; edits are saved with the definition. Reset sel. (the button
    /// under the component) drops the edits of the edit objects selected in Rhino.
    ///
    /// Outputs keep the input's tree exactly: Geometry with the edits in place, Edited a
    /// True/False for every item. Grasshopper's preview (the eye) draws the Geometry output;
    /// the edit objects are the component's own, shown or hidden with "Edit objects in Rhino"
    /// (its menu), with name dots ("Name dots in Rhino"). They're removed while the component
    /// is disabled, its definition isn't the one on the canvas, Grasshopper is minimized or
    /// hidden, and while the Rhino model is saved (never part of it).
    /// </summary>
    public sealed class EditInRhinoComponent : GH_Component
    {
        private static readonly Color ShownColor = Color.FromArgb(0, 150, 215);
        private static readonly Color EditedColor = Color.FromArgb(225, 51, 6);
        private const string OwnerKey = "minivivisection.gh";

        private static readonly List<WeakReference<EditInRhinoComponent>> s_all = new List<WeakReference<EditInRhinoComponent>>();
        private static bool s_eventsOn;
        private static bool s_quiet; // our own changes to the Rhino model
        private static object s_followedEditor;
        private static object s_followedCanvas;
        private static DotConduit s_conduit;

        /// <summary>An item shown in Rhino in this solution.</summary>
        private sealed class Shown
        {
            public GeometryBase Incoming; // as it comes in
            public string Where; // "[3]", or "{0;1}[3]" in a tree
        }

        /// <summary>An edit object's name and dot: the component's nickname now, and where.</summary>
        private string Label(Shown shown) => NickName + " " + shown.Where;

        private readonly Dictionary<string, GeometryBase> _edits = new Dictionary<string, GeometryBase>(); // "{0;1}|3" -> edit
        private readonly Dictionary<string, string> _editedFrom = new Dictionary<string, string>(); // "{0;1}|3" -> the replaced item's type
        private readonly Dictionary<string, Shown> _shown = new Dictionary<string, Shown>();
        private readonly Dictionary<string, Guid> _objects = new Dictionary<string, Guid>();

        // This component in this session: the same definition opened twice -- or a copy made
        // from it -- has the same InstanceGuid, and one's orphan sweep mustn't take the other's
        // objects. Not saved.
        private readonly string _session = Guid.NewGuid().ToString("N");

        /// <summary>The start of this component's edit objects' user string ("{instance}|{session}|").</summary>
        private string Owner => InstanceGuid + "|" + _session + "|";
        private bool _showObjects = true;
        private bool _nameDots = true;
        private bool _showQueued;

        public EditInRhinoComponent()
            : base(GhNames.Of("Edit"), GhNames.Of("Edit"),
                GhNames.Text("Edit geometry in Rhino: the items at Index show as Rhino objects; your edits replace them in the output and are saved with the definition. " +
                "Reset sel. / Reset all drop edits."),
                "miniVivisection", "Debug")
        {
            lock (s_all)
            {
                s_all.RemoveAll(r => !r.TryGetTarget(out _));
                s_all.Add(new WeakReference<EditInRhinoComponent>(this));
            }
            StartEvents();
            // Renamed: the dots and the objects' names follow at once.
            ObjectChanged += (s, e) =>
            {
                if (e.Type == GH_ObjectEventType.NickName || e.Type == GH_ObjectEventType.NickNameAccepted)
                {
                    QueueShow();
                    RhinoDoc.ActiveDoc?.Views.Redraw();
                }
            };
        }

        public override Guid ComponentGuid => new Guid("b7d2e8a4-3c1f-4e6b-a9d0-5f2c7e1b8d63");

        protected override Bitmap Icon => Icons.EditInRhino;

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter("Geometry", "G", "Geometry: an item, a list or a tree.", GH_ParamAccess.tree);
            pManager.AddGenericParameter("Index", "i",
                "Which items: indices (in every branch), paths {0;2} (whole branches), or {0;2}[3] (one item). Nothing: item 0.", GH_ParamAccess.tree);
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGeometryParameter("Geometry", "G", "The input, in the same tree, with the edited items in place of the ones they replace.", GH_ParamAccess.tree);
            pManager.AddBooleanParameter("Edited", "E", "For every item, in the same tree: True where it's replaced by your edit.", GH_ParamAccess.tree);
        }

        private static string Key(GH_Path path, int index) => path + "|" + index;

        /// <summary>"[3]", or "{0;1}[3]" when the input has more than one branch.</summary>
        private static string Where(GH_Structure<IGH_GeometricGoo> tree, GH_Path path, int index) =>
            tree.PathCount > 1 ? path + "[" + index + "]" : "[" + index + "]";

        /// <summary>An item's kind, to recognize it again: the geometry's type (Curve, Brep, Point...).</summary>
        private static string KindOf(IGH_GeometricGoo goo) => ToGeometry(goo)?.ObjectType.ToString();

        protected override void SolveInstance(IGH_DataAccess da)
        {
            da.GetDataTree(0, out GH_Structure<IGH_GeometricGoo> tree);
            da.GetDataTree(1, out GH_Structure<IGH_Goo> indexTree);
            var unreadable = new List<string>();
            var allPaths = new HashSet<GH_Path>();
            foreach (var goo in indexTree?.AllData(true) ?? Enumerable.Empty<IGH_Goo>())
            {
                var pick = Pick.From(goo);
                if (pick == null)
                    unreadable.Add(goo?.ToString() ?? "null");
                else if (pick.Path != null)
                    allPaths.Add(pick.Path);
            }
            var geometryPaths = new HashSet<GH_Path>(tree?.Paths ?? new List<GH_Path>());
            var missing = allPaths.Where(p => !geometryPaths.Contains(p)).ToList();
            var geometry = new GH_Structure<IGH_GeometricGoo>();
            var edited = new GH_Structure<GH_Boolean>();
            _shown.Clear();
            var used = new HashSet<string>();
            var stale = new List<string>();
            var staleKeys = new HashSet<string>();
            var unpicked = new List<string>();
            var notGeometry = 0;
            for (var b = 0; b < (tree?.PathCount ?? 0); b++)
            {
                var path = tree.Paths[b];
                var branch = tree.Branches[b];
                geometry.EnsurePath(path);
                edited.EnsurePath(path);
                var picked = Picked(PicksFor(indexTree, path, tree.PathCount), path, branch.Count);
                for (var i = 0; i < branch.Count; i++)
                {
                    var key = Key(path, i);
                    var goo = branch[i];
                    var hasEdit = _edits.TryGetValue(key, out var edit);
                    if (hasEdit && _editedFrom.TryGetValue(key, out var from) && from != KindOf(goo))
                    {
                        // Another kind of item at this place now (the input's order changed
                        // upstream): the edit isn't for it -- kept, but not put in its place.
                        hasEdit = false;
                        staleKeys.Add(key);
                        stale.Add(Where(tree, path, i) + " (edited " + from + ", now " + (KindOf(goo) ?? "nothing") + ")");
                    }
                    if (hasEdit)
                    {
                        used.Add(key);
                        if (!picked.Contains(i))
                            unpicked.Add(Where(tree, path, i));
                    }
                    geometry.Append(hasEdit ? ToGoo(edit) ?? goo : goo, path);
                    edited.Append(new GH_Boolean(hasEdit), path);
                    if (!picked.Contains(i))
                        continue;
                    var incoming = ToGeometry(goo);
                    if (incoming == null && !hasEdit)
                    {
                        notGeometry++;
                        continue;
                    }
                    _shown[key] = new Shown { Incoming = incoming, Where = Where(tree, path, i) };
                }
            }
            da.SetDataTree(0, geometry);
            da.SetDataTree(1, edited);

            Message = _edits.Count == 0 ? (_shown.Count == 1 ? "1 in Rhino" : _shown.Count + " in Rhino") : _edits.Count + " edited";
            if (used.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, used.Count + (used.Count == 1 ? " item is" : " items are") +
                    " replaced by your edits in Rhino (red-orange). Select edit objects in Rhino and click Reset sel. to use the input again.");
            if (unpicked.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Edits of " + string.Join(", ", unpicked.Take(8)) + (unpicked.Count > 8 ? ", ..." : "") +
                    " are in the output but not in Rhino (Index doesn't pick them now): pick them again to see or reset them, or Reset all.");
            if (stale.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Edits not used -- another kind of item is there now (the input changed upstream): " +
                    string.Join(", ", stale.Take(8)) + (stale.Count > 8 ? ", ..." : "") + ". Reset all drops them.");
            var outside = _edits.Keys.Where(k => !used.Contains(k) && !staleKeys.Contains(k)).ToList();
            if (outside.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Edits of " + string.Join(", ", outside) + " are kept, but the input has no such items now.");
            if (unreadable.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Index: not an index, a path or a path with index: " + string.Join(", ", unreadable.Take(5)) + ".");
            if (missing.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Geometry has no branch " + string.Join(", ", missing) + ".");
            if (notGeometry > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, notGeometry + " picked item(s) aren't geometry Rhino can show as objects.");
            QueueShow();
        }

        /// <summary>One item of the Index input: an index, a path (a branch), or both ({0;2}[3]).</summary>
        private sealed class Pick
        {
            public GH_Path Path;
            public int? Index;

            private static readonly System.Text.RegularExpressions.Regex PathText =
                new System.Text.RegularExpressions.Regex(@"^\s*(\{[^}]*\})\s*(?:\[\s*(-?\d+)\s*\])?\s*$");

            public static Pick From(IGH_Goo goo)
            {
                switch (goo)
                {
                    case null:
                        return null;
                    case GH_Integer integer:
                        return new Pick { Index = integer.Value };
                    case GH_Number number when Math.Abs(number.Value - Math.Round(number.Value)) < 1e-9:
                        return new Pick { Index = (int)Math.Round(number.Value) };
                    case GH_StructurePath path when path.Value != null:
                        return new Pick { Path = path.Value };
                }
                var text = goo.ToString()?.Trim() ?? "";
                if (int.TryParse(text, out var index))
                    return new Pick { Index = index };
                var match = PathText.Match(text);
                if (!match.Success)
                    return null;
                var parsed = new GH_Path();
                if (!parsed.FromString(match.Groups[1].Value))
                    return null;
                return new Pick { Path = parsed, Index = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : (int?)null };
            }
        }

        /// <summary>The Index items for the Geometry branch at <paramref name="path"/>: one
        /// branch of them (an item, a list) is for every branch; an index tree's branch is for
        /// the Geometry branch with its path -- or, when Geometry is a single list, all of them.</summary>
        private static List<Pick> PicksFor(GH_Structure<IGH_Goo> indexTree, GH_Path path, int geometryBranches)
        {
            if (indexTree == null || indexTree.DataCount == 0)
                return new List<Pick> { new Pick { Index = 0 } };
            IEnumerable<IGH_Goo> items;
            if (indexTree.PathCount == 1)
                items = indexTree.Branches[0];
            else if (indexTree.PathExists(path))
                items = indexTree.get_Branch(path).OfType<IGH_Goo>();
            else if (geometryBranches == 1)
                items = indexTree.AllData(true);
            else
                items = Enumerable.Empty<IGH_Goo>();
            return items.Select(Pick.From).Where(p => p != null).ToList();
        }

        /// <summary>The positions picked in the branch at <paramref name="path"/> (count items):
        /// path-and-index picks for it; with paths, only their branches -- all items, or the
        /// indices' ones; else the indices.</summary>
        private static HashSet<int> Picked(List<Pick> picks, GH_Path path, int count)
        {
            int? Valid(int i)
            {
                var at = i < 0 ? i + count : i;
                return at >= 0 && at < count ? at : (int?)null;
            }
            var picked = new HashSet<int>();
            var indices = picks.Where(p => p.Path == null && p.Index.HasValue).Select(p => p.Index.Value).ToList();
            var branches = picks.Where(p => p.Path != null && !p.Index.HasValue).Select(p => p.Path).ToList();
            foreach (var exact in picks.Where(p => p.Path != null && p.Index.HasValue && p.Path == path))
            {
                if (Valid(exact.Index.Value) is int at)
                    picked.Add(at);
            }
            if (branches.Count > 0)
            {
                if (branches.Contains(path))
                {
                    if (indices.Count == 0)
                        picked.UnionWith(Enumerable.Range(0, count));
                    else
                        picked.UnionWith(indices.Select(Valid).Where(i => i.HasValue).Select(i => i.Value));
                }
            }
            else
            {
                picked.UnionWith(indices.Select(Valid).Where(i => i.HasValue).Select(i => i.Value));
            }
            return picked;
        }

        private static GeometryBase ToGeometry(IGH_GeometricGoo goo) => goo == null ? null : GH_Convert.ToGeometryBase(goo)?.Duplicate();

        private static IGH_GeometricGoo ToGoo(GeometryBase geometry) => geometry == null ? null : GH_Convert.ToGeometricGoo(geometry.Duplicate());

        // --- the edit objects in Rhino -------------------------------------------------------------

        private void QueueShow()
        {
            FollowGrasshopperWindow();
            if (_showQueued)
                return;
            _showQueued = true;
            RhinoApp.Idle += ShowOnIdle;
        }

        private void ShowOnIdle(object sender, EventArgs e)
        {
            RhinoApp.Idle -= ShowOnIdle;
            _showQueued = false;
            Show();
        }

        /// <summary>Whether the edit objects show now: on, enabled, its definition the one on the
        /// canvas, Grasshopper's window on screen.</summary>
        private bool ShouldShow()
        {
            var document = OnPingDocument();
            if (!_showObjects || Locked || document == null || document.Context == GH_DocumentContext.Close || document.Context == GH_DocumentContext.Unloaded)
                return false;
            var editor = Instances.DocumentEditor;
            if (editor == null || !editor.Visible || editor.WindowState == FormWindowState.Minimized)
                return false;
            return Instances.ActiveCanvas?.Document == document;
        }

        private void Show()
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;
            var wanted = ShouldShow() ? _shown : new Dictionary<string, Shown>();
            var modified = doc.Modified; // the edit objects are the component's, not the user's changes
            s_quiet = true;
            try
            {
                SweepOrphans(doc);
                foreach (var key in _objects.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
                {
                    Delete(doc, _objects[key]);
                    _objects.Remove(key);
                }
                foreach (var pair in wanted)
                {
                    var hasEdit = _edits.TryGetValue(pair.Key, out var edit);
                    var geometry = hasEdit ? edit : pair.Value.Incoming;
                    if (geometry == null)
                        continue;
                    var color = hasEdit ? EditedColor : ShownColor;
                    var name = "miniVivisection: " + Label(pair.Value);
                    var existing = _objects.TryGetValue(pair.Key, out var id) ? doc.Objects.FindId(id) : null;
                    if (existing != null && !existing.IsDeleted && GeometryBase.GeometryEquals(existing.Geometry, geometry))
                    {
                        if (existing.Attributes.ObjectColor != color || existing.Attributes.Name != name)
                        {
                            var attributes = existing.Attributes.Duplicate();
                            attributes.ObjectColor = color;
                            attributes.Name = name;
                            doc.Objects.ModifyAttributes(existing, attributes, true);
                        }
                        continue;
                    }
                    // Changed in place where it can be: the object keeps its id (the user's selection,
                    // and no deleted copy for Undo to bring back); else a new one.
                    if (existing != null && !existing.IsDeleted && existing.Geometry.ObjectType == geometry.ObjectType &&
                        doc.Objects.Replace(existing.Id, geometry.Duplicate(), true))
                    {
                        var attributes = doc.Objects.FindId(existing.Id).Attributes.Duplicate();
                        if (attributes.ObjectColor != color || attributes.Name != name)
                        {
                            attributes.ObjectColor = color;
                            attributes.Name = name;
                            doc.Objects.ModifyAttributes(existing.Id, attributes, true);
                        }
                        continue;
                    }
                    if (existing != null && !existing.IsDeleted)
                        DeleteObject(doc, existing);
                    var added = doc.CreateDefaultAttributes(); // the current layer, not layer 0 (off or locked, the objects couldn't be picked)
                    added.ColorSource = ObjectColorSource.ColorFromObject;
                    added.ObjectColor = color;
                    added.Name = name;
                    added.SetUserString(OwnerKey, Owner + pair.Key);
                    _objects[pair.Key] = doc.Objects.Add(geometry.Duplicate(), added);
                }
            }
            finally
            {
                s_quiet = false;
                if (!modified)
                    doc.Modified = false;
            }
            doc.Views.Redraw();
            Instances.RedrawCanvas(); // the message, the button, the balloon: now, not at the next solution
        }

        private static void Delete(RhinoDoc doc, Guid id)
        {
            var existing = doc.Objects.FindId(id);
            if (existing != null && !existing.IsDeleted)
                DeleteObject(doc, existing);
        }

        /// <summary>Deleted also when hidden, locked or on a layer that's off or locked -- else it
        /// would be saved into the model and left behind.</summary>
        private static void DeleteObject(RhinoDoc doc, RhinoObject existing)
        {
            if (!doc.Objects.Delete(existing, true, true))
                RhinoApp.WriteLine(GhNames.Of("Edit") + ": couldn't delete its edit object {0}.", existing.Id);
        }

        /// <summary>This component's edit objects it doesn't follow (any more): brought back by
        /// Rhino's Undo, or left by an earlier delete -- found by their user string, deleted.</summary>
        private void SweepOrphans(RhinoDoc doc)
        {
            var mine = new HashSet<Guid>(_objects.Values);
            foreach (var found in doc.Objects.FindByUserString(OwnerKey, Owner + "*", true) ?? Array.Empty<RhinoObject>())
            {
                if (!mine.Contains(found.Id))
                    DeleteObject(doc, found);
            }
        }

        /// <summary>Every V Edit's edit objects in <paramref name="doc"/>, followed or not (before
        /// a save: never part of the model).</summary>
        private static void SweepAll(RhinoDoc doc)
        {
            foreach (var found in doc.Objects.FindByUserString(OwnerKey, "*", true) ?? Array.Empty<RhinoObject>())
                DeleteObject(doc, found);
        }

        /// <summary>Every edit object out of the model.</summary>
        private void RemoveAll() => RemoveAll(RhinoDoc.ActiveDoc);

        private void RemoveAll(RhinoDoc doc)
        {
            if (doc == null || _objects.Count == 0)
                return;
            var modified = doc.Modified;
            s_quiet = true;
            try
            {
                foreach (var id in _objects.Values)
                    Delete(doc, id);
                SweepOrphans(doc);
            }
            finally
            {
                s_quiet = false;
                if (!modified)
                    doc.Modified = false;
            }
            _objects.Clear();
            doc.Views.Redraw();
        }

        /// <summary>An edit object changed in Rhino: its geometry replaces the item, and the
        /// definition recomputes from here. Back to the input's geometry (Undo): no edit.</summary>
        private void TakeEdit(string key, GeometryBase changed)
        {
            if (changed == null)
                return;
            var geometry = changed.Duplicate();
            var incoming = _shown.TryGetValue(key, out var shown) ? shown.Incoming : null;
            // Grasshopper's Undo takes it back, and closing asks to save the definition.
            RecordUndoEvent(GhNames.Of("Edit") + ": edit in Rhino");
            if (incoming != null && GeometryBase.GeometryEquals(geometry, incoming))
            {
                _edits.Remove(key);
                _editedFrom.Remove(key);
            }
            else
            {
                _edits[key] = geometry;
                if (incoming != null)
                    _editedFrom[key] = incoming.ObjectType.ToString();
            }
            var document = OnPingDocument();
            document?.Modified();
            document?.ScheduleSolution(1, d => ExpireSolution(false));
        }

        // --- Rhino and Grasshopper events (one set for all V Edits) --------------------------------

        private static void StartEvents()
        {
            if (s_eventsOn)
                return;
            s_eventsOn = true;
            RhinoDoc.ReplaceRhinoObject += (s, e) =>
            {
                if (s_quiet)
                    return;
                ForAll(c =>
                {
                    var key = c._objects.FirstOrDefault(p => p.Value == e.ObjectId).Key;
                    if (key != null)
                        c.TakeEdit(key, e.NewRhinoObject?.Geometry);
                });
            };
            RhinoDoc.BeginSaveDocument += (s, e) =>
            {
                if (e.ExportSelected || e.Document == null)
                    return; // exporting selected objects: what the user picked, edit objects too
                ForAll(c => c.RemoveAll(e.Document)); // never saved into the model
                var modified = e.Document.Modified;
                s_quiet = true;
                try
                {
                    SweepAll(e.Document); // also any left behind (Undo, a crash)
                }
                finally
                {
                    s_quiet = false;
                    if (!modified)
                        e.Document.Modified = false;
                }
            };
            RhinoDoc.EndSaveDocument += (s, e) =>
            {
                if (e.ExportSelected)
                    return;
                var modified = e.Document?.Modified ?? false;
                ForAll(c => c.Show());
                if (e.Document != null && !modified)
                    e.Document.Modified = false; // the model still counts as saved
            };
            // A new or opened model: the edit objects show there.
            RhinoDoc.EndOpenDocument += (s, e) => ForAll(c => c.QueueShow());
            RhinoDoc.NewDocument += (s, e) => ForAll(c => c.QueueShow());
            // The Reset sel. button follows Rhino's selection: the canvas repainted (at its next
            // paint, not at once: a script selecting thousands of objects fires this for each)
            // -- only while some V Edit shows objects
            RhinoDoc.SelectObjects += (s, e) => OnRhinoSelection();
            RhinoDoc.DeselectObjects += (s, e) => OnRhinoSelection();
            RhinoDoc.DeselectAllObjects += (s, e) => OnRhinoSelection();
            s_conduit = new DotConduit { Enabled = true };
        }

        /// <summary>Grasshopper's window and canvas, once there: minimized, hidden, or another
        /// definition on the canvas -- the edit objects go (and come back).</summary>
        private static void FollowGrasshopperWindow()
        {
            var editor = Instances.DocumentEditor;
            if (editor != null && !ReferenceEquals(editor, s_followedEditor))
            {
                s_followedEditor = editor;
                editor.Resize += (s, e) => ForAll(c => c.QueueShow());
                editor.VisibleChanged += (s, e) => ForAll(c => c.QueueShow());
            }
            var canvas = Instances.ActiveCanvas;
            if (canvas != null && !ReferenceEquals(canvas, s_followedCanvas))
            {
                s_followedCanvas = canvas;
                canvas.DocumentChanged += (s, e) => ForAll(c => c.QueueShow());
            }
        }

        private static void OnRhinoSelection()
        {
            var shown = false;
            ForAll(c => shown |= c.HasEdits || c._objects.Count > 0);
            if (shown)
                Instances.InvalidateCanvas();
        }

        private static void ForAll(Action<EditInRhinoComponent> action)
        {
            List<EditInRhinoComponent> all;
            lock (s_all)
            {
                s_all.RemoveAll(r => !r.TryGetTarget(out _));
                all = s_all.Select(r => r.TryGetTarget(out var c) ? c : null).Where(c => c != null).ToList();
            }
            foreach (var component in all)
                action(component);
        }

        /// <summary>The name dots of every V Edit's edit objects ("Name dots in Rhino").</summary>
        private sealed class DotConduit : DisplayConduit
        {
            protected override void DrawForeground(DrawEventArgs e)
            {
                ForAll(c =>
                {
                    if (!c._nameDots || c._objects.Count == 0)
                        return;
                    foreach (var pair in c._objects)
                    {
                        var rhinoObject = e.RhinoDoc?.Objects.FindId(pair.Value);
                        if (rhinoObject == null || rhinoObject.IsDeleted || !c._shown.TryGetValue(pair.Key, out var shown))
                            continue;
                        var hasEdit = c._edits.ContainsKey(pair.Key);
                        NameDots.Draw(e, NameDots.LabelPoint(rhinoObject.Geometry), c.Label(shown) + (hasEdit ? " edited" : ""), hasEdit ? EditedColor : ShownColor);
                    }
                });
            }
        }

        // --- the component's life -------------------------------------------------------------------

        public override void ClearData()
        {
            base.ClearData();
            if (Locked)
                RemoveAll(); // disabled: the edit objects go
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            RemoveAll();
            base.RemovedFromDocument(document);
        }

        public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
        {
            base.DocumentContextChanged(document, context);
            if (context == GH_DocumentContext.Close || context == GH_DocumentContext.Unloaded || context == GH_DocumentContext.Lock)
                RemoveAll();
            else if (context == GH_DocumentContext.Loaded || context == GH_DocumentContext.Unlock)
                QueueShow();
        }

        // --- reset, menu -------------------------------------------------------------------------------

        /// <summary>The keys of the edit objects selected in Rhino.</summary>
        private List<string> SelectedKeys()
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return new List<string>();
            return _objects.Where(p => doc.Objects.FindId(p.Value) is RhinoObject o && !o.IsDeleted && o.IsSelected(false) > 0).Select(p => p.Key).ToList();
        }

        /// <summary>Whether an edited edit object is selected in Rhino (the Reset sel. button).</summary>
        internal bool HasSelectedEdit => SelectedKeys().Any(_edits.ContainsKey);

        /// <summary>The edits of the edit objects selected in Rhino dropped (Reset sel.).</summary>
        internal void ResetSelected()
        {
            var keys = SelectedKeys().Where(_edits.ContainsKey).ToList();
            if (keys.Count == 0)
                return;
            RecordUndoEvent("Reset selected edits");
            foreach (var key in keys)
            {
                _edits.Remove(key);
                _editedFrom.Remove(key);
            }
            ExpireSolution(true);
        }

        /// <summary>Whether there are edits (the Reset all button).</summary>
        internal bool HasEdits => _edits.Count > 0;

        internal void ResetAll()
        {
            if (_edits.Count == 0)
                return;
            RecordUndoEvent("Reset all edits");
            _edits.Clear();
            _editedFrom.Clear();
            ExpireSolution(true);
        }

        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Edit objects in Rhino", (s, e) =>
            {
                RecordUndoEvent("Edit objects in Rhino");
                _showObjects = !_showObjects;
                QueueShow();
            }, true, _showObjects);
            Menu_AppendItem(menu, "Name dots in Rhino", (s, e) =>
            {
                RecordUndoEvent("Name dots in Rhino"); // saved with the definition
                _nameDots = !_nameDots;
                RhinoDoc.ActiveDoc?.Views.Redraw();
            }, true, _nameDots);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Reset selected edits", (s, e) => ResetSelected(), HasSelectedEdit);
            Menu_AppendItem(menu, "Reset all edits (" + _edits.Count + ")", (s, e) => ResetAll(), _edits.Count > 0);
            Menu_AppendItem(menu, "Select edit objects in Rhino", (s, e) => SelectInRhino(), _objects.Count > 0);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "About " + GhNames.Of("Edit") + "\u2026", (s, e) => ShowAbout());
        }

        /// <summary>Version details: this .gha's, and the plug-in's if it's loaded.</summary>
        private static void ShowAbout()
        {
            var gha = typeof(EditInRhinoComponent).Assembly;
            var lines = new List<string>
            {
                GhNames.Of("Edit") + " -- miniVivisection's Grasshopper component",
                "",
                "Version: " + MiniVivisection.VersionInfo.Version,
                "Build: " + MiniVivisection.VersionInfo.Build,
                "Built: " + MiniVivisection.VersionInfo.BuiltLocal +
                    (MiniVivisection.VersionInfo.BuiltUtc is DateTime utc ? " (" + utc.ToString("yyyy-MM-dd HH:mm") + " UTC)" : ""),
                "File: " + gha.Location,
            };
            var plugIn = Rhino.PlugIns.PlugIn.Find(new Guid("c198ec17-3078-46a0-8f9b-94a40e9ba298"));
            lines.Add("");
            lines.Add("miniVivisection plug-in: " + (plugIn != null ? plugIn.Version + " (" + plugIn.Assembly.Location + ")" : "not loaded"));
            lines.Add("Rhino: " + RhinoApp.Version);
            lines.Add("");
            lines.Add("Edit geometry of a definition in Rhino and feed the changes back. Help: V Help in the Script Editor (the manual's feature list, and section 9).");
            Rhino.UI.Dialogs.ShowMessage(GhNames.Text(string.Join(System.Environment.NewLine, lines)), "About " + GhNames.Of("Edit"));
        }

        private void SelectInRhino()
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;
            doc.Objects.UnselectAll();
            var box = BoundingBox.Empty;
            foreach (var id in _objects.Values)
            {
                if (doc.Objects.FindId(id) is RhinoObject o && !o.IsDeleted)
                {
                    o.Select(true);
                    box.Union(o.Geometry.GetBoundingBox(true));
                }
            }
            if (box.IsValid)
            {
                box.Inflate(Math.Max(box.Diagonal.Length * 0.25, 1.0));
                doc.Views.ActiveView?.ActiveViewport.ZoomBoundingBox(box);
            }
            doc.Views.Redraw();
        }

        public override void CreateAttributes() => m_attributes = new EditInRhinoAttributes(this);

        // --- saved with the definition ------------------------------------------------------------------

        public override bool Write(GH_IWriter writer)
        {
            writer.SetBoolean("MV.ShowObjects", _showObjects);
            writer.SetBoolean("MV.NameDots", _nameDots);
            var edits = _edits.ToList();
            writer.SetInt32("MV.EditCount", edits.Count);
            for (var k = 0; k < edits.Count; k++)
            {
                writer.SetString("MV.EditKey", k, edits[k].Key);
                writer.SetByteArray("MV.EditGeometry", k, GH_Convert.CommonObjectToByteArray(edits[k].Value));
                if (_editedFrom.TryGetValue(edits[k].Key, out var from))
                    writer.SetString("MV.EditFrom", k, from);
            }
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            // The component itself first: whatever happens to the edits, the definition opens with it.
            var read = base.Read(reader);
            _edits.Clear();
            _editedFrom.Clear();
            var skipped = 0;
            try
            {
                if (reader.ItemExists("MV.ShowObjects"))
                    _showObjects = reader.GetBoolean("MV.ShowObjects");
                if (reader.ItemExists("MV.NameDots"))
                    _nameDots = reader.GetBoolean("MV.NameDots");
                var count = reader.ItemExists("MV.EditCount") ? reader.GetInt32("MV.EditCount") : 0;
                for (var k = 0; k < count; k++)
                {
                    try
                    {
                        if (!reader.ItemExists("MV.EditGeometry", k) || !reader.ItemExists("MV.EditKey", k))
                        {
                            skipped++;
                            continue;
                        }
                        var geometry = GH_Convert.ByteArrayToCommonObject<GeometryBase>(reader.GetByteArray("MV.EditGeometry", k));
                        if (geometry == null)
                        {
                            skipped++;
                            continue;
                        }
                        var key = reader.GetString("MV.EditKey", k);
                        _edits[key] = geometry;
                        if (reader.ItemExists("MV.EditFrom", k))
                            _editedFrom[key] = reader.GetString("MV.EditFrom", k);
                    }
                    catch (Exception)
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception)
            {
                skipped++;
            }
            if (skipped > 0)
                RhinoApp.WriteLine(GhNames.Of("Edit") + " '{0}': {1} saved edit(s) couldn't be read and are left out.", NickName, skipped);
            return read;
        }
    }
}
