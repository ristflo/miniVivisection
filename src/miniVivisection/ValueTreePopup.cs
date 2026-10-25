using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// A value's members as a tree, in a small window at the mouse -- the tooltip's "Tree"
    /// link, as VS Code's debug hover and Visual Studio's DataTips. The rows are
    /// RhinoCode's own variables (as in the Variables and V Watch tabs), the members
    /// read when a row is expanded, on the paused thread. The window stays open while
    /// stepping: at every pause it reads the value again (scoped as at its line) and
    /// keeps the expanded rows; while the script runs it's grayed; it closes when the
    /// run ends, or with Esc. Options: "Value trees".
    /// </summary>
    internal sealed class ValueTreePopup : Form
    {
        private sealed class Node : TreeGridItem
        {
            public ExecVariable Variable;
            public string Name = "";
            public string Path = "";
            public bool Loaded;
            public bool CanExpand;

            public override bool Expandable => CanExpand || Count > 0;
            public string Value => Shorten(Variable?.GetValueRepr());
            public string Type => Variable?.Id?.TypeIdentifier ?? "";
        }

        private readonly object _editor;
        private readonly ScriptEditorHook _hook;
        private readonly string _expression;
        private readonly int _line;
        private readonly TreeGridView _tree;
        private readonly Label _status;
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
        private IDebugControls _controls;

        private ValueTreePopup(object editor, ScriptEditorHook hook, string expression, int line)
        {
            _editor = editor;
            _hook = hook;
            _expression = expression;
            _line = line;
            Title = expression + (line > 0 ? "  -- line " + line : "");
            WindowStyle = WindowStyle.Utility;
            ShowInTaskbar = false;
            Resizable = true;
            Minimizable = false;
            Maximizable = false;
            Size = new Size(480, 320);
            _tree = new TreeGridView
            {
                ShowHeader = true,
                Columns =
                {
                    new GridColumn { HeaderText = "Name", DataCell = new TextBoxCell(nameof(Node.Name)), Width = 150 },
                    new GridColumn { HeaderText = "Value", DataCell = new TextBoxCell(nameof(Node.Value)), Width = 200 },
                    new GridColumn { HeaderText = "Type", DataCell = new TextBoxCell(nameof(Node.Type)), Expand = true },
                },
            };
            _tree.Expanding += (sender, e) =>
            {
                if (e.Item is Node node && !LoadMembers(node))
                    e.Cancel = true;
            };
            _tree.Expanded += (sender, e) =>
            {
                if (e.Item is Node node)
                    _expanded.Add(node.Path);
            };
            _tree.Collapsed += (sender, e) =>
            {
                if (e.Item is Node node)
                    _expanded.Remove(node.Path);
            };
            _status = new Label { TextColor = Colors.Gray, Font = SystemFonts.Default(8) };
            Content = new TableLayout
            {
                Padding = new Padding(4),
                Spacing = new Size(2, 2),
                Rows = { new TableRow(_tree) { ScaleHeight = true }, _status },
            };
            KeyDown += (sender, e) =>
            {
                if (e.Key == Keys.Escape)
                {
                    e.Handled = true;
                    Close();
                }
            };
            _hook.PausedForTools += OnPaused;
            _hook.EndedForTools += OnEnded;
            Closed += (sender, e) =>
            {
                _hook.PausedForTools -= OnPaused;
                _hook.EndedForTools -= OnEnded;
                if (_controls != null)
                    _controls.Continued -= OnContinued;
            };
            _expanded.Add(expression); // the root opens
        }

        private static readonly List<ValueTreePopup> s_open = new List<ValueTreePopup>();

        /// <summary>"Value trees" turned off: the open ones close.</summary>
        public static void CloseAll()
        {
            foreach (var popup in s_open.ToList())
                popup.Close();
        }

        /// <summary>The tooltip's Tree link (UI thread, while paused).</summary>
        public static void Open(object editor, ScriptEditorHook hook, string expression, int line)
        {
            if (!MiniVivisectionSettings.TooltipTree)
                return;
            var popup = new ValueTreePopup(editor, hook, expression, line) { Owner = editor as Window }; // above the editor only
            var mouse = Mouse.Position;
            popup.Location = new Point((int)mouse.X + 12, (int)mouse.Y + 12);
            s_open.Add(popup);
            popup.Closed += (s, e) => s_open.Remove(popup);
            popup.Show();
            popup.Reload();
        }

        /// <summary>The value read again, its expanded rows too (UI thread).</summary>
        private void Reload()
        {
            var controls = ScriptEditorHook.DebugControlsOf(_editor);
            if (controls == null || controls.State != DebugControlsState.Paused)
            {
                _status.Text = "Not paused.";
                return;
            }
            if (!ReferenceEquals(controls, _controls))
            {
                if (_controls != null)
                    _controls.Continued -= OnContinued;
                _controls = controls;
                _controls.Continued += OnContinued;
            }
            var access = new ScriptEditorAccess(_editor);
            var python = PythonBridge.IsRunning && BreakpointUi.IsPython(access.Code?.Uri, access.Language);
            var filename = HoverPreview.FileFor(access, controls);
            string error = null;
            if (!_hook.TryOnPausedThread(controls, () =>
            {
                try
                {
                    var frame = controls.Results?.CurrentThread?.CurrentFrame;
                    object value;
                    if (python && _line > 0 && filename != "")
                        value = PythonBridge.EvalAt(_expression, _line, filename);
                    else if (!ScriptEditorHook.TryEvaluate(controls, _expression, out value, out error))
                        return null;
                    return ScriptEditorHook.VariableOf(frame, _expression, value, onPausedThread: true);
                }
                catch (Exception ex)
                {
                    error = ScriptEditorHook.FirstLine(ex);
                    return null;
                }
            }, out var root) || root is null)
            {
                _tree.DataStore = new TreeGridItemCollection();
                _status.Text = error ?? "Can't read it here.";
                _tree.Enabled = true;
                return;
            }
            var node = new Node { Variable = root, Name = _expression, Path = _expression, CanExpand = root.CanExpand };
            if (_expanded.Contains(node.Path))
                Reexpand(node);
            _tree.DataStore = new TreeGridItemCollection { node };
            _tree.Enabled = true;
            _status.Text = "Paused" + (frameLine(controls) is int line ? " at line " + line : "") + ". Updated at every pause; Esc closes.";
        }

        private static int? frameLine(IDebugControls controls) => controls.Results?.CurrentThread?.CurrentFrame?.Reference?.Position.LineNumber;

        private void Reexpand(Node node)
        {
            if (!LoadMembers(node))
                return;
            node.Expanded = true;
            foreach (var child in node.Children.OfType<Node>().Where(c => _expanded.Contains(c.Path)))
                Reexpand(child);
        }

        private bool LoadMembers(Node node)
        {
            if (node.Loaded)
                return node.Children.Count > 0;
            var variable = node.Variable;
            var controls = ScriptEditorHook.DebugControlsOf(_editor);
            if (variable is null || !variable.CanExpand || controls == null || controls.State != DebugControlsState.Paused)
                return false;
            List<ExecVariable> members;
            try
            {
                if (!_hook.TryOnPausedThread(controls, () => variable.Expand().ToList(), out members) || members == null)
                    return false;
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Hover, "can't show the members of {0}: {1}", node.Path, ScriptEditorHook.FirstLine(ex));
                return false;
            }
            node.Children.Clear();
            foreach (var member in members)
            {
                var name = member.Id?.Identifier ?? "";
                node.Children.Add(new Node
                {
                    Variable = member,
                    Name = name,
                    Path = name.StartsWith("[") ? node.Path + name : node.Path + "." + name,
                    CanExpand = member.CanExpand,
                });
            }
            node.Loaded = true;
            return node.Children.Count > 0;
        }

        private void OnPaused(IDebugControls controls)
        {
            if (ReferenceEquals(ScriptEditorHook.DebugControlsOf(_editor), controls))
                RhinoApp.InvokeOnUiThread(new Action(Reload));
        }

        private void OnContinued(IDebugControls controls)
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                _tree.Enabled = false;
                _status.Text = "Running -- the values of the last pause.";
            }));
        }

        private void OnEnded(IDebugControls controls)
        {
            if (ReferenceEquals(controls, _controls))
                RhinoApp.InvokeOnUiThread(new Action(Close));
        }

        private static string Shorten(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 200 ? text : text.Substring(0, 199) + "…";
        }
    }
}
