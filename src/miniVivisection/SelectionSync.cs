using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Rhino;
using Rhino.Runtime.Code.Execution.Debugging;
using Rhino.UI;

namespace MiniVivisection
{
    /// <summary>
    /// Selection in both directions between the tabs and Rhino:
    /// - rows selected in the V Watch tab, or in the editor's Variables and Watch
    ///   tabs, highlight what's drawn for them (Rhino's selection color) and select their
    ///   objects being edited;
    /// - a click on drawn geometry (DrawnPicker; Options: plain click, Alt+click, off)
    ///   selects its watch -- the row, a list item's row when it's shown -- and its
    ///   variable in the editor's tabs;
    /// - objects being edited selected in Rhino highlight their watch (ScriptEditorHook).
    /// The plug-in's own selections don't echo back (<see cref="Busy"/>).
    /// </summary>
    internal sealed class SelectionSync
    {
        private static readonly Regex s_item = new Regex(@"^(.*)\[(\d+)\]$", RegexOptions.Compiled);
        private readonly ScriptEditorHook _hook;

        public SelectionSync(ScriptEditorHook hook)
        {
            _hook = hook;
        }

        /// <summary>The plug-in is selecting: selection events are its own (UI thread).</summary>
        public static bool Busy { get; private set; }

        /// <summary>Rows selected in a tab (their paths: <c>crv</c>, <c>curves[2]</c>,
        /// <c>node.x</c>): what's drawn for them is highlighted, their objects being
        /// edited are selected. UI thread.</summary>
        public void Highlight(object editor, IEnumerable<string> paths, bool selectObjects = true)
        {
            if (Busy || !MiniVivisectionSettings.SelectFromTabs)
                return;
            var controls = ScriptEditorHook.DebugControlsOf(editor);
            if (controls == null)
                return;
            var drawn = new HashSet<string>(_hook.Conduit.Values.Select(v => v.Name), StringComparer.Ordinal);
            var selected = new HashSet<(string Name, int Index)>();
            foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)))
            {
                if (drawn.Contains(path) || _hook.Edits.IsEditing(controls, path))
                {
                    selected.Add((path, -1));
                    continue;
                }
                var match = s_item.Match(path);
                if (match.Success && (drawn.Contains(match.Groups[1].Value) || _hook.Edits.IsEditing(controls, match.Groups[1].Value)))
                    selected.Add((match.Groups[1].Value, int.Parse(match.Groups[2].Value)));
            }
            if (!_hook.Conduit.SetSelected(selected))
                return;
            Log.Debug(LogArea.Selection, "from a tab: {0}", string.Join(", ", selected.Select(s => s.Index < 0 ? s.Name : s.Name + "[" + s.Index + "]")));
            Busy = true;
            try
            {
                var doc = RhinoDoc.ActiveDoc;
                var ids = selectObjects ? _hook.Edits.ObjectsOf(controls, selected) : new List<Guid>();
                if (doc != null && ids.Count > 0)
                {
                    doc.Objects.UnselectAll();
                    doc.Objects.Select(ids);
                }
                doc?.Views.Redraw();
            }
            finally
            {
                Busy = false;
            }
        }

        /// <summary>A click on drawn geometry: its watch selected in the tabs.</summary>
        public void Picked(IDebugControls controls, string name, int index)
        {
            var editor = _hook.EditorOf(controls);
            if (editor == null)
                return;
            Log.Debug(LogArea.Selection, "picked {0}[{1}]", name, index);
            Busy = true;
            try
            {
                _hook.Conduit.SetSelected(new HashSet<(string, int)> { (name, index) });
                _hook.WatchListFor(editor).RequestSelect(name, index);
                EditorViews.Select(editor, new List<string> { name + "[" + index + "]", name });
                RhinoDoc.ActiveDoc?.Views.Redraw();
            }
            finally
            {
                Busy = false;
            }
        }
    }

    /// <summary>
    /// Clicks on what the plug-in draws (it isn't a document object, so Rhino can't pick
    /// it): Rhino's own pick test against the drawn items (WatchConduit.HitTest). Plain
    /// clicks (default) leave Rhino's own selection alone -- a click on a document object
    /// still selects it; Alt+click is taken by the plug-in. Not while a command runs.
    /// </summary>
    internal sealed class DrawnPicker : MouseCallback
    {
        private readonly ScriptEditorHook _hook;

        public DrawnPicker(ScriptEditorHook hook)
        {
            _hook = hook;
        }

        protected override void OnMouseDown(MouseCallbackEventArgs e)
        {
            try
            {
                var mode = MiniVivisectionSettings.PickDrawn;
                if (mode == "Off" || e.MouseButton != MouseButton.Left || _hook.Conduit.Count == 0)
                    return;
                var alt = (Eto.Forms.Keyboard.Modifiers & Eto.Forms.Keys.Alt) != 0;
                if (mode == "Alt+click" ? !alt : alt || e.CtrlKeyDown || e.ShiftKeyDown)
                    return;
                if (Rhino.Commands.Command.InCommand())
                    return;
                var hit = _hook.Conduit.HitTest(e.View, e.ViewportPoint);
                if (hit == null)
                    return;
                _hook.Selection.Picked(hit.Value.Controls, hit.Value.Value.Name, hit.Value.Index);
                if (mode == "Alt+click")
                    e.Cancel = true;
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Selection, "picking: {0}", Log.Describe(ex));
            }
        }
    }
}
