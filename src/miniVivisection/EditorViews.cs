using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Rhino;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Brings the Script Editor's Variables and Watch tabs up to date after the paused
    /// script was changed (an edit in Rhino written back, the Python tab, Claude's
    /// run_python). The editor reads a paused frame's variables once and caches them
    /// (<c>ExecFrame.m_results</c>), so its tabs keep the values from the pause. This
    /// empties that cache, reads the variables again on the paused thread -- RhinoCode's
    /// Python variable provider needs the GIL -- and then has both tabs show the frame
    /// again (<c>VariablesTray.View(code, frame)</c>, <c>VariableWatchTray.View(code, frame)</c>,
    /// internal editor UI, by reflection). Expanded rows stay expanded.
    /// </summary>
    internal static class EditorViews
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly FieldInfo s_results = typeof(ExecFrame).GetField("m_results", Any);
        private static readonly HashSet<string> s_reported = new HashSet<string>();

        /// <summary>On the paused thread.</summary>
        public static void Refresh(object editor, IDebugControls controls)
        {
            try
            {
                var frame = controls.Results?.CurrentThread?.CurrentFrame;
                if (editor == null || frame == null || s_results == null)
                    return;
                (s_results.GetValue(frame) as IDictionary)?.Clear();
                frame.Evaluate().ToList(); // read again here, where the script's Python may run
                RhinoApp.InvokeOnUiThread(new Action(() => ShowFrame(editor, frame)));
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        /// <summary>A row of a Variables or Watch view as a name: its variable's
        /// (<c>crv</c>, <c>[0]</c>, <c>x</c>), or the Watch row's expression; null for the
        /// root and for groups (globals, non-public).</summary>
        internal static string ItemName(object item)
        {
            var variable = item.GetType().GetProperty("Variable", Any, null, typeof(ExecVariable), Type.EmptyTypes, null)?.GetValue(item) as ExecVariable;
            if (variable is object) // not != : ExecVariable's operator calls Equals, which throws on null
                return variable.Id?.Identifier;
            var getExpression = item.GetType().GetMethod("GetExpression", Any, null, Type.EmptyTypes, null);
            if (getExpression?.Invoke(item, null) is DebugExpression expression && !string.IsNullOrWhiteSpace(expression.ExpressionString))
                return expression.ExpressionString;
            return null;
        }

        /// <summary>Selects, in the editor's Variables and Watch tabs, the first shown row
        /// whose path is one of <paramref name="expressions"/> (<c>curves[2]</c>: the
        /// <c>[2]</c> row under an expanded <c>curves</c>) -- objects being edited were
        /// selected in Rhino. UI thread.</summary>
        public static void Select(object editor, IList<string> expressions)
        {
            try
            {
                var console = editor.GetType().GetProperty("Console", Any)?.GetValue(editor);
                foreach (var trayName in new[] { "Variables", "VariableWatch" })
                {
                    var tray = console?.GetType().GetProperty(trayName, Any)?.GetValue(console);
                    if (!(tray?.GetType().GetProperty("DebugView", Any)?.GetValue(tray) is Eto.Forms.TreeGridView view) || view.IsEditing)
                        continue; // a row being edited keeps its edit
                    var root = FindField(view.GetType(), "m_root")?.GetValue(view) as Eto.Forms.ITreeGridItem;
                    var rows = new Dictionary<string, Eto.Forms.ITreeGridItem>(StringComparer.Ordinal);
                    Collect(root, null, rows);
                    var row = expressions.Where(rows.ContainsKey).Select(e => rows[e]).FirstOrDefault();
                    if (row != null && !ReferenceEquals(view.SelectedItem, row))
                        view.SelectedItem = row;
                }
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_following =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

        /// <summary>Rows selected in the editor's Variables and Watch tabs highlight what's
        /// drawn for them (SelectionSync). Once per editor window.</summary>
        public static void FollowSelection(object editor, ScriptEditorHook hook)
        {
            if (s_following.TryGetValue(editor, out _))
                return;
            try
            {
                var console = editor.GetType().GetProperty("Console", Any)?.GetValue(editor);
                if (console == null)
                    return;
                s_following.Add(editor, null);
                foreach (var trayName in new[] { "Variables", "VariableWatch" })
                {
                    var tray = console.GetType().GetProperty(trayName, Any)?.GetValue(console);
                    if (!(tray?.GetType().GetProperty("DebugView", Any)?.GetValue(tray) is Eto.Forms.TreeGridView view))
                        continue;
                    view.SelectedItemChanged += (sender, e) =>
                    {
                        // The drawing only: selecting Rhino objects here took the editor's focus,
                        // so clicking a Watch row's name (or Edit Watch) no longer edited it.
                        if (!SelectionSync.Busy && view.SelectedItem != null)
                            hook.Selection.Highlight(editor, new[] { PathOf(view.SelectedItem) }, selectObjects: false);
                    };
                }
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        /// <summary>A row's path: its name under its parents' (<c>curves[0]</c>, <c>node.x</c>).</summary>
        internal static string PathOf(Eto.Forms.ITreeGridItem item)
        {
            var parts = new List<string>();
            while (item != null)
            {
                var name = ItemName(item);
                if (name == null)
                    break; // the root, or a group
                parts.Insert(0, name);
                item = item.Parent;
            }
            if (parts.Count == 0)
                return null;
            var path = parts[0];
            foreach (var part in parts.Skip(1))
                path += part.StartsWith("[") ? part : "." + part;
            return path;
        }

        /// <summary>The shown rows under <paramref name="item"/> by path: children of
        /// expanded rows only -- selecting a hidden row would expand the tree.</summary>
        private static void Collect(Eto.Forms.ITreeGridItem item, string prefix, Dictionary<string, Eto.Forms.ITreeGridItem> rows)
        {
            if (!(item is Eto.Forms.TreeGridItem parent))
                return;
            foreach (var child in parent.Children)
            {
                var name = ItemName(child);
                var path = name == null ? prefix : prefix == null ? name : name.StartsWith("[") ? prefix + name : prefix + "." + name;
                if (name != null && path != null && !rows.ContainsKey(path))
                    rows[path] = child;
                if (name == null || child.Expanded)
                    Collect(child, path, rows);
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            return null;
        }

        /// <summary>Has the Watch tab evaluate its expressions again (UI thread): after a
        /// pause of a script on its own thread, once FreePause can run Python for it.</summary>
        public static void ShowWatch(object editor, ExecFrame frame) => ShowFrame(editor, frame, "VariableWatch");

        private static void ShowFrame(object editor, ExecFrame frame, params string[] trayNames)
        {
            try
            {
                var code = new ScriptEditorAccess(editor).Code;
                var console = editor.GetType().GetProperty("Console", Any)?.GetValue(editor);
                if (code == null || console == null)
                    return;
                // Each tab on its own: one failing doesn't keep the other stale.
                foreach (var trayName in trayNames.Length > 0 ? trayNames : new[] { "Variables", "VariableWatch" })
                {
                    try
                    {
                        var tray = console.GetType().GetProperty(trayName, Any)?.GetValue(console);
                        var view = tray?.GetType().GetMethod("View", Any, null, new[] { typeof(Code), typeof(ExecFrame) }, null);
                        if (view == null)
                            Report(new MissingMethodException(trayName, "View(Code, ExecFrame)"));
                        else
                            view.Invoke(tray, new object[] { code, frame });
                    }
                    catch (Exception ex)
                    {
                        Report(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }

        /// <summary>Each kind of failure once.</summary>
        private static void Report(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            var text = string.Format("{0} ({1} in {2}.{3})", ScriptEditorHook.FirstLine(ex), ex.GetType().Name,
                ex.TargetSite?.DeclaringType?.Name, ex.TargetSite?.Name);
            lock (s_reported)
            {
                if (!s_reported.Add(text))
                    return;
            }
            Log.Warning(LogArea.Watch, "can't refresh the Variables/Watch tabs: {0}", text);
        }
    }
}
