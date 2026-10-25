using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Eto.Forms;
using Rhino.Runtime.Code.Editing;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// "Watch in Rhino", "Bake to Rhino" and "Edit in Rhino" in the Script Editor's
    /// context menus, while its debugger is paused:
    /// - the code view (right click): for the selection, else the name under the
    ///   cursor (<c>crv</c>, <c>crv.PointAtStart</c>, <c>curves[0]</c>);
    /// - the Variables and Watch tabs: for the selected row (nested rows as their
    ///   path, <c>curves[0]</c>).
    /// The menus are internal editor UI (RhinoCode 8.36, by reflection):
    /// <c>RCE.Codes.Editors._editorMenu</c> and each view's <c>m_contextMenu</c>.
    /// </summary>
    internal static class EditorMenus
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly ConditionalWeakTable<object, object> s_done = new ConditionalWeakTable<object, object>();
        private static bool s_reported;

        public static void TryAdd(object editor, ScriptEditorHook hook)
        {
            if (s_done.TryGetValue(editor, out _))
                return;
            try
            {
                var codes = editor.GetType().GetProperty("Codes", Any)?.GetValue(editor);
                var console = editor.GetType().GetProperty("Console", Any)?.GetValue(editor);
                if (codes == null || console == null)
                    return; // not built yet: the next scan
                s_done.Add(editor, new object());

                var editors = codes.GetType().GetProperty("Editors", Any).GetValue(codes);
                var codeMenu = (ContextMenu)FindField(editors.GetType(), "_editorMenu").GetValue(editors);
                BreakpointUi.AddCodeMenuItems(codeMenu, editor); // first: its Opening handler enables them before ShowEnabledOnly
                AddItems(codeMenu, editor, hook, () => ExpressionAtCursor(editors), offerWatch: true);
                codeMenu.Opening += (sender, e) => HoverPreview.Held = true;
                codeMenu.Closed += (sender, e) => HoverPreview.Held = false;

                foreach (var trayName in new[] { "Variables", "VariableWatch" })
                {
                    var tray = console.GetType().GetProperty(trayName, Any).GetValue(console);
                    var view = tray.GetType().GetProperty("DebugView", Any).GetValue(tray);
                    var menu = (ContextMenu)FindField(view.GetType(), "m_contextMenu").GetValue(view);
                    AddItems(menu, editor, hook, () => Task.FromResult(SelectedExpression(view)), offerWatch: true);
                    if (trayName == "VariableWatch")
                        FixEditExpression(view);
                }
                Log.Info(LogArea.Menus, "added Watch / Bake / Edit in Rhino to the Script Editor's context menus.");
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                if (!s_reported)
                {
                    s_reported = true;
                    UserLog.Warning("Can't add the context menu items: {0}", ex.Message);
                }
            }
        }

        private static void AddItems(ContextMenu menu, object editor, ScriptEditorHook hook, Func<Task<string>> expressionOf, bool offerWatch)
        {
            var items = new List<MenuItem>();
            ButtonMenuItem watchAs = null;
            if (offerWatch)
            {
                items.Add(Item("Watch in Rhino", editor, expressionOf, (controls, expression) => hook.AddRhinoWatch(controls, expression)));
                watchAs = new ButtonMenuItem { Text = "Watch in Rhino as" };
                items.Add(watchAs);
            }
            items.Add(Item("Bake to Rhino", editor, expressionOf, (controls, expression) => WatchActions.Bake(hook, controls, expression)));
            items.Add(Item("Edit in Rhino", editor, expressionOf, (controls, expression) => hook.Edits.Start(controls, expression)));
            menu.Items.Add(new SeparatorMenuItem());
            foreach (var item in items)
                menu.Items.Add(item);
            menu.Opening += (sender, e) =>
            {
                var paused = PausedControls(editor) != null && MiniVivisectionSettings.PluginOn;
                foreach (var item in items)
                    item.Enabled = paused;
                if (watchAs != null)
                    FillWatchAs(watchAs, editor, hook, expressionOf, paused);
                ShowEnabledOnly(menu);
            };
        }

        /// <summary>"Watch in Rhino as": the interpretations of the expression there, read
        /// from the paused script -- after the code view has said what's selected.</summary>
        private static async void FillWatchAs(ButtonMenuItem menu, object editor, ScriptEditorHook hook, Func<Task<string>> expressionOf, bool paused)
        {
            menu.Items.Clear();
            if (!paused)
                return;
            menu.Items.Add(new ButtonMenuItem { Text = "…", Enabled = false });
            try
            {
                var expression = (await expressionOf())?.Trim();
                var choices = hook.InterpretationsOf(editor, expression);
                menu.Items.Clear();
                if (choices == null || choices.Count == 0)
                {
                    menu.Items.Add(new ButtonMenuItem { Text = "(nothing to choose)", Enabled = false });
                    return;
                }
                foreach (var choice in choices)
                {
                    var id = choice.Id;
                    var item = new ButtonMenuItem { Text = choice.Label };
                    item.Click += (s, e) =>
                    {
                        var controls = PausedControls(editor);
                        if (controls != null)
                            hook.AddRhinoWatch(controls, expression, id);
                    };
                    menu.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Menus, "Watch in Rhino as: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Only the menu's enabled entries are shown -- while paused, most of the
        /// code view's are disabled (the script is read-only) -- with no separator first,
        /// last or twice in a row.</summary>
        private static void ShowEnabledOnly(ContextMenu menu)
        {
            foreach (var item in menu.Items)
            {
                if (!(item is SeparatorMenuItem))
                    item.Visible = item.Enabled;
            }
            MenuItem lastShown = null;
            foreach (var item in menu.Items)
            {
                if (item is SeparatorMenuItem)
                    item.Visible = lastShown != null && !(lastShown is SeparatorMenuItem);
                if (item.Visible)
                    lastShown = item;
            }
            if (lastShown is SeparatorMenuItem)
                lastShown.Visible = false;
        }

        private static MenuItem Item(string text, object editor, Func<Task<string>> expressionOf, Action<IDebugControls, string> action)
        {
            var item = new ButtonMenuItem { Text = text };
            item.Click += async (sender, e) =>
            {
                try
                {
                    var controls = PausedControls(editor);
                    if (controls == null)
                    {
                        UserLog.Warning("{0}: the script isn't paused.", text);
                        return;
                    }
                    var expression = (await expressionOf())?.Trim();
                    if (string.IsNullOrEmpty(expression))
                    {
                        UserLog.Warning("{0}: no variable or expression there -- select one or put the cursor on its name.", text);
                        return;
                    }
                    action(controls, expression);
                }
                catch (Exception ex)
                {
                    while (ex is TargetInvocationException && ex.InnerException != null)
                        ex = ex.InnerException;
                    UserLog.Error("{0} failed: {1} ({2} in {3}.{4})", text, ScriptEditorHook.FirstLine(ex), ex.GetType().Name,
                        ex.TargetSite?.DeclaringType?.Name, ex.TargetSite?.Name);
                }
            };
            return item;
        }

        private static IDebugControls PausedControls(object editor)
        {
            var controls = ScriptEditorHook.DebugControlsOf(editor);
            return controls != null && controls.State == DebugControlsState.Paused ? controls : null;
        }

        /// <summary>The selection in the code view, else the name under the cursor:
        /// letters, digits, <c>_</c>, <c>.</c> and <c>[...]</c> around it.</summary>
        private static async Task<string> ExpressionAtCursor(object editors)
        {
            if (!(editors.GetType().GetProperty("ActiveEditContext", Any)?.GetValue(editors) is ICodeEditContext context))
                return null;
            var selected = await context.GetSelected();
            if (!string.IsNullOrWhiteSpace(selected))
                return selected;
            var position = await context.GetPosition();
            if (!context.Code.Text.TryGetLine(position, out var line) || string.IsNullOrEmpty(line))
                return null;
            bool IsPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '[' || c == ']';
            var column = Math.Max(0, Math.Min(line.Length, position.ColumnNumber - 1));
            var start = column;
            while (start > 0 && IsPart(line[start - 1]))
                start--;
            var end = column;
            while (end < line.Length && IsPart(line[end]))
                end++;
            return line.Substring(start, end - start).Trim('.');
        }

        /// <summary>The selected row of a Variables or Watch view as an expression: its
        /// name, under its parents' (<c>curves</c> + <c>[0]</c> = <c>curves[0]</c>,
        /// <c>node</c> + <c>x</c> = <c>node.x</c>).</summary>
        private static string SelectedExpression(object view)
        {
            // The views are Eto TreeGridViews; right-clicking a row selects it (ExpressionView.OnMouseDown).
            var item = (view as TreeGridView)?.SelectedItem;
            var parts = new List<string>();
            while (item != null)
            {
                var name = EditorViews.ItemName(item);
                if (name == null)
                    break; // the root, or a <globals> / <non-public> group
                parts.Insert(0, name);
                item = item.Parent;
            }
            if (parts.Count == 0)
                return null;
            var expression = parts[0];
            foreach (var part in parts.Skip(1))
                expression += part.StartsWith("[") ? part : "." + part;
            return expression;
        }

        /// <summary>The Watch tab's own "Edit Expression" starts editing the expression cell
        /// while the menu still has the keyboard focus, so the cell's text box never gets
        /// it. This starts the edit once more, a moment after the menu is gone. The stale
        /// edit is cancelled first: Eto's BeginEdit would commit it, the commit reloads
        /// the view's rows (VariableWatchView.OnCellEdited), and the new edit then finds
        /// no cell to edit. F2 never has a stale edit, which is why it works.</summary>
        private static void FixEditExpression(object view)
        {
            if (!(view is TreeGridView tree) || !(FindField(view.GetType(), "_editExpressionItem").GetValue(view) is MenuItem item))
                return;
            var root = FindField(view.GetType(), "m_root");
            item.Click += (sender, e) =>
            {
                var timer = new UITimer { Interval = 0.15 };
                timer.Elapsed += (s, a) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    if (root.GetValue(view) is TreeGridItem rootItem && tree.SelectedItem is ITreeGridItem selected)
                    {
                        // BeginEdit counts the shown rows, children of expanded watches too.
                        var row = tree.SelectedRows.DefaultIfEmpty(rootItem.Children.IndexOf(selected)).First();
                        if (row >= 0)
                        {
                            tree.CancelEdit();
                            tree.BeginEdit(row, 1);
                        }
                    }
                };
                timer.Start();
            };
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }
    }
}
