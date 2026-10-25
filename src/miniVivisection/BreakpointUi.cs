using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>Which part of a breakpoint's spec is edited.</summary>
    internal enum SpecField
    {
        Condition,
        HitCount,
        LogMessage,
    }

    /// <summary>
    /// The breakpoints' conditions in the Script Editor, per editor window:
    /// - in the code view, after the breakpoint's line ("◆ if i > 3"): Monaco decorations,
    ///   which move with edits -- their lines are read back twice a second and become the
    ///   specs' lines (BreakpointSpecs.SetLines). A spec whose breakpoint is gone (for two
    ///   reads) is dropped; when the editor replaced the text and the decorations are
    ///   lost, the specs follow their breakpoints by order;
    /// - in the Breakpoints tab: a column with the spec, its hits and its last error, a
    ///   context menu and a double click to edit it;
    /// - in the code view's context menu: "Breakpoint Condition…", "Hit Count…",
    ///   "Log Message…", "Remove Condition" for the clicked line.
    /// </summary>
    internal sealed class BreakpointMarkers
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Function bodies (see HoverPreview.Script).
        private const string InstallScript = @"
if (window.__swvBp) return 'ok';
" + CodeViewScripts.FindEditor + @"if (!document.getElementById('swv-bp-style')) {
  var style = document.createElement('style');
  style.id = 'swv-bp-style';
  style.textContent = '.swv-bp-after{color:#c586c0;font-style:italic;opacity:.85}.swv-bp-error{color:#f14c4c;font-style:italic}';
  document.head.appendChild(style);
}
var state = { ids: [], model: editor.getModel(), mouseLine: 0 };
state.set = function (list) {
  var model = editor.getModel();
  if (model !== state.model) { state.ids = []; state.model = model; }
  var decorations = list.map(function (m) {
    var line = Math.min(Math.max(1, m.line), model.getLineCount());
    var column = model.getLineMaxColumn(line);
    var options = { description: 'swv-breakpoint-condition', stickiness: 1, showIfCollapsed: true }; // an empty range shows its text only so
    if (m.text) { // empty: hidden, but still moving with its line
      options.hoverMessage = { value: m.hover };
      options.after = { content: '   ◆ ' + m.text, inlineClassName: m.error ? 'swv-bp-error' : 'swv-bp-after' };
    }
    return { range: { startLineNumber: line, startColumn: Math.max(1, column - 1), endLineNumber: line, endColumn: column }, options: options };
  });
  state.ids = editor.deltaDecorations(state.ids, decorations);
  return 'ok';
};
state.lines = function () {
  var model = editor.getModel();
  if (model !== state.model) return 'model';
  return JSON.stringify(state.ids.map(function (id) { var r = model.getDecorationRange(id); return r ? r.startLineNumber : 0; }));
};
editor.onMouseDown(function (e) {
  state.mouseLine = (e.event && e.event.rightButton && e.target && e.target.position) ? e.target.position.lineNumber : 0;
});
window.__swvBp = state;
return 'ok';";

        private const string ReadLinesScript = "return window.__swvBp ? window.__swvBp.lines() : 'none';";
        private const string MouseLineScript = "return window.__swvBp ? String(window.__swvBp.mouseLine) : '0';";

        private static readonly ConditionalWeakTable<object, BreakpointMarkers> s_markers = new ConditionalWeakTable<object, BreakpointMarkers>();
        private static bool s_reportedInstall;

        private readonly ScriptEditorAccess _access;
        private readonly UITimer _timer;
        private WebView _installedIn;
        private WebView _pushedWeb;
        private string _pushedKey;
        private string _pushedState;
        private List<int> _pushedLines = new List<int>();
        private List<int> _lastBreakpoints = new List<int>();
        private readonly Dictionary<int, DateTime> _missingSince = new Dictionary<int, DateTime>();
        private static readonly TimeSpan DropAfter = TimeSpan.FromSeconds(3);

        /// <summary>The lines of all the code view's breakpoints, active or not, sorted.</summary>
        internal static async Task<List<int>> AllBreakpointLines(Ed.Eto.IEd ed)
        {
            var lines = new List<int>();
            foreach (var (line, _) in await ed.GetBreakpointsAsync())
                lines.Add(line);
            lines.Sort();
            return lines;
        }
        private TreeGridView _breakpointsView;
        private GridColumn _conditionColumn;
        private bool _busy;
        private int _failedInstalls;

        private BreakpointMarkers(object editor)
        {
            _access = new ScriptEditorAccess(editor);
            _timer = new UITimer { Interval = 0.5 };
            _timer.Elapsed += OnTick;
            _timer.Start();
            Action<string> specsChanged = key => Application.Instance.AsyncInvoke(() => _pushedState = null);
            BreakpointSpecs.Changed += specsChanged;
            EditorWindows.WhenClosed(editor, () =>
            {
                _timer.Stop();
                BreakpointSpecs.Changed -= specsChanged;
            });
        }

        public static void TryAdd(object editor, ScriptEditorHook hook)
        {
            if (s_markers.TryGetValue(editor, out _))
                return;
            var markers = new BreakpointMarkers(editor);
            s_markers.Add(editor, markers);
            markers.AddToBreakpointsTab(editor);
        }

        /// <summary>The breakpoint line the code view's context menu was opened on (the
        /// right-clicked line, else the cursor's).</summary>
        public static async Task<int> MenuLine(object editor)
        {
            var access = new ScriptEditorAccess(editor);
            if (access.Monaco is WebView web && int.TryParse(await HoverPreview.Script(web, MouseLineScript), out var line) && line > 0)
                return line;
            return (await access.CursorAsync()).Line;
        }

        private async void OnTick(object sender, EventArgs e)
        {
            if (_busy)
                return;
            _busy = true;
            try
            {
                await Update();
            }
            catch (Exception ex)
            {
                Log.Trace(LogArea.Breakpoints, "markers: {0}", Log.Describe(ex)); // the view is reloading; next tick
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task Update()
        {
            var code = _access.Code;
            var ed = _access.Monaco;
            var web = ed as WebView;
            var uri = code?.Uri;
            if (uri == null || web == null)
                return;
            var key = BreakpointSpecs.Key(uri);
            ScriptKinds.Learn(uri, _access.Language);
            if (!ReferenceEquals(web, _installedIn))
            {
                var installed = await HoverPreview.Script(web, InstallScript);
                if (installed != "ok")
                {
                    // The code view may still be loading: tried again at every tick, reported
                    // once it has failed for 20 s.
                    if (++_failedInstalls == 40 && !s_reportedInstall)
                    {
                        s_reportedInstall = true;
                        Log.Warning(LogArea.Breakpoints, "breakpoint markers unavailable: {0}", installed ?? "no answer from the code view");
                    }
                    return;
                }
                if (_installedIn == null)
                    Log.Info(LogArea.Breakpoints, "breakpoint markers ready in the code view");
                _installedIn = web;
                _failedInstalls = 0;
            }

            // 1. Lines moved by edits: the decorations moved with them.
            if (ReferenceEquals(web, _pushedWeb) && _pushedKey == key && _pushedLines.Count > 0)
            {
                var read = await HoverPreview.Script(web, ReadLinesScript);
                if (read != null && read.StartsWith("[", StringComparison.Ordinal))
                {
                    var lines = JsonSerializer.Deserialize<List<int>>(read);
                    if (lines.Count == _pushedLines.Count && !lines.SequenceEqual(_pushedLines) && lines.All(l => l > 0))
                    {
                        _pushedLines = lines;
                        if (BreakpointSpecs.SetLines(uri, lines))
                            Log.Debug(LogArea.Breakpoints, "{0}: conditions moved to lines {1}", key, string.Join(", ", lines));
                    }
                }
                else if (read == "model" || read == "none")
                    _pushedState = null;
            }

            // 2. Specs follow their breakpoints, and go with them. All breakpoints, disabled
            //    ones too: disabling a breakpoint keeps its condition.
            var breakpoints = await AllBreakpointLines(ed);
            var specs = BreakpointSpecs.Of(uri);
            var gone = specs.Where(s => !breakpoints.Contains(s.Line)).ToList();
            if (gone.Count > 0 && breakpoints.Count == _lastBreakpoints.Count && breakpoints.Count > 0)
            {
                // The same number of breakpoints at other lines: the text was replaced; by order.
                foreach (var spec in gone.ToList())
                {
                    var index = _lastBreakpoints.IndexOf(spec.Line);
                    if (index >= 0 && !specs.Any(s => s.Line == breakpoints[index]))
                    {
                        var moved = spec.Copy();
                        BreakpointSpecs.Remove(uri, spec.Line);
                        BreakpointSpecs.Set(uri, breakpoints[index], s =>
                        {
                            s.Condition = moved.Condition;
                            s.HitCondition = moved.HitCondition;
                            s.LogMessage = moved.LogMessage;
                        });
                        gone.Remove(spec);
                    }
                }
            }
            // Dropped only when missing for a while: the editor takes a moment to put the
            // breakpoints back after it replaced the text.
            var now = DateTime.UtcNow;
            foreach (var spec in gone)
            {
                if (!_missingSince.TryGetValue(spec.Line, out var since))
                    _missingSince[spec.Line] = now;
                else if (now - since >= DropAfter)
                {
                    _missingSince.Remove(spec.Line);
                    BreakpointSpecs.Remove(uri, spec.Line);
                    UserLog.Info("Breakpoint at line {0} removed: its condition is dropped ({1}).", spec.Line, spec.Summary);
                }
            }
            foreach (var line in _missingSince.Keys.Where(l => !gone.Any(s => s.Line == l)).ToList())
                _missingSince.Remove(line);
            _lastBreakpoints = breakpoints;

            // 3. The markers and the Breakpoints tab, when something changed.
            specs = BreakpointSpecs.Of(uri);
            var on = MiniVivisectionSettings.ConditionalBreakpoints; // with the Debug switch: hidden while either is off
            if (_conditionColumn != null && _conditionColumn.Visible != on)
                _conditionColumn.Visible = on;
            var markers = specs.Select(s => new Dictionary<string, object>
            {
                ["line"] = s.Line,
                ["text"] = on ? MarkerText(s) : "",
                ["error"] = s.LastError != null,
                ["hover"] = HoverText(s),
            }).ToList();
            var state = JsonSerializer.Serialize(markers);
            if (state == _pushedState && ReferenceEquals(web, _pushedWeb) && key == _pushedKey)
                return;
            var json = JsonSerializer.Serialize(state); // a JS string literal of the JSON
            if (await HoverPreview.Script(web, "return window.__swvBp ? window.__swvBp.set(JSON.parse(" + json + ")) : 'none';") != "ok")
            {
                _installedIn = null;
                return;
            }
            _pushedWeb = web;
            _pushedKey = key;
            _pushedState = state;
            _pushedLines = specs.Select(s => s.Line).ToList();
            RefreshBreakpointsTab();
        }

        private static string MarkerText(BreakpointSpec spec)
        {
            var text = spec.Summary;
            if (spec.Hits > 0)
                text += " · " + spec.Hits + (spec.Hits == 1 ? " hit" : " hits");
            if (spec.LastError != null)
                text += " · error";
            return text;
        }

        private static string HoverText(BreakpointSpec spec)
        {
            var lines = new List<string>();
            if (spec.Condition.Length > 0)
                lines.Add("Pauses when: " + MarkdownText.Escape(spec.Condition));
            if (spec.HitCondition.Length > 0)
                lines.Add("Hit count: " + MarkdownText.Escape(spec.HitCondition));
            if (spec.LogMessage.Length > 0)
                lines.Add("Logs instead of pausing: " + MarkdownText.Escape(spec.LogMessage));
            if (spec.LastError != null)
                lines.Add("Last error (it paused): " + MarkdownText.Escape(spec.LastError));
            lines.Add("Right-click the line to change it.");
            return string.Join("\n\n", lines);
        }

        // --- the Breakpoints tab -----------------------------------------------------------

        private void AddToBreakpointsTab(object editor)
        {
            try
            {
                var console = editor.GetType().GetProperty("Console", Any)?.GetValue(editor);
                var tray = console?.GetType().GetProperty("Breakpoints", Any)?.GetValue(console);
                if (!(tray?.GetType().GetProperty("DebugView", Any)?.GetValue(tray) is TreeGridView view))
                    return;
                _breakpointsView = view;
                var column = new GridColumn
                {
                    HeaderText = "Condition",
                    Editable = false,
                    DataCell = new TextBoxCell
                    {
                        Binding = Binding.Delegate<object, string>(item =>
                        {
                            var spec = SpecOf(item);
                            return spec == null ? "" : MarkerText(spec) + (spec.LastError != null ? ": " + spec.LastError : "");
                        }),
                    },
                };
                view.Columns.Add(column);
                _conditionColumn = column;
                view.CellFormatting += (sender, e) =>
                {
                    if (e.Column == column)
                        e.ForegroundColor = SpecOf(e.Item)?.LastError != null ? Color.FromArgb(241, 76, 76) : Color.FromArgb(197, 134, 192);
                };
                view.CellDoubleClick += (sender, e) =>
                {
                    if (e.GridColumn == column && LineOf(e.Item) is int line)
                        BreakpointUi.Edit(editor, line, SpecField.Condition);
                };
                view.MouseDown += (sender, e) =>
                {
                    if (e.Buttons == MouseButtons.Alternate && view.GetCellAt(e.Location).Item is ITreeGridItem item)
                        view.SelectedItem = item;
                };
                var menu = new ContextMenu();
                foreach (var (text, field) in new[] { ("Breakpoint Condition…", SpecField.Condition), ("Hit Count…", SpecField.HitCount), ("Log Message…", SpecField.LogMessage) })
                {
                    var item = new ButtonMenuItem { Text = text };
                    item.Click += (sender, e) =>
                    {
                        if (LineOf(view.SelectedItem) is int line)
                            BreakpointUi.Edit(editor, line, field);
                    };
                    menu.Items.Add(item);
                }
                var remove = new ButtonMenuItem { Text = "Remove Condition" };
                remove.Click += (sender, e) =>
                {
                    if (LineOf(view.SelectedItem) is int line && _access.Code?.Uri is Uri uri)
                        BreakpointSpecs.Remove(uri, line);
                };
                menu.Items.Add(remove);
                menu.Opening += (sender, e) =>
                {
                    var any = LineOf(view.SelectedItem) != null;
                    foreach (var item in menu.Items)
                        item.Enabled = any;
                    remove.Enabled = SpecOf(view.SelectedItem) != null;
                };
                view.ContextMenu = menu;
                Log.Debug(LogArea.Breakpoints, "the Breakpoints tab shows the conditions");
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "can't add the conditions to the Breakpoints tab: {0}", Log.Describe(ex));
            }
        }

        private void RefreshBreakpointsTab()
        {
            try
            {
                _breakpointsView?.ReloadData();
            }
            catch (Exception ex)
            {
                Log.Trace(LogArea.Breakpoints, "Breakpoints tab: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The line of a Breakpoints tab row (RhinoCode's BreakpointItem.Breakpoint).</summary>
        private static int? LineOf(object item) =>
            item?.GetType().GetProperty("Breakpoint", Any)?.GetValue(item) is CodeReferenceBreakpoint breakpoint
                ? breakpoint.Reference.Position.LineNumber
                : (int?)null;

        private BreakpointSpec SpecOf(object item) =>
            LineOf(item) is int line && _access.Code?.Uri is Uri uri ? BreakpointSpecs.Find(uri, line) : null;
    }

    /// <summary>Editing a breakpoint's spec: the code view's menu items and the popup.</summary>
    internal static class BreakpointUi
    {
        /// <summary>The code view's items, before the plug-in's other ones: their Opening
        /// handler runs before EditorMenus shows only the enabled entries.</summary>
        public static void AddCodeMenuItems(ContextMenu menu, object editor)
        {
            var items = new List<MenuItem>();
            foreach (var (text, field) in new[] { ("Breakpoint Condition…", SpecField.Condition), ("Hit Count…", SpecField.HitCount), ("Log Message…", SpecField.LogMessage) })
            {
                var item = new ButtonMenuItem { Text = text };
                item.Click += async (sender, e) => Edit(editor, await BreakpointMarkers.MenuLine(editor), field);
                items.Add(item);
            }
            var remove = new ButtonMenuItem { Text = "Remove Condition" };
            remove.Click += async (sender, e) =>
            {
                if (new ScriptEditorAccess(editor).Code?.Uri is Uri uri)
                    BreakpointSpecs.Remove(uri, await BreakpointMarkers.MenuLine(editor));
            };
            menu.Items.Add(new SeparatorMenuItem());
            foreach (var item in items)
                menu.Items.Add(item);
            menu.Items.Add(remove);
            menu.Opening += async (sender, e) =>
            {
                var access = new ScriptEditorAccess(editor);
                var available = MiniVivisectionSettings.ConditionalBreakpoints && access.Monaco != null && access.Code?.Uri != null;
                foreach (var item in items)
                    item.Enabled = available;
                remove.Enabled = false;
                if (!available || !BreakpointSpecs.HasAny(access.Code.Uri))
                    return;
                try
                {
                    var line = await BreakpointMarkers.MenuLine(editor);
                    if (BreakpointSpecs.Find(access.Code.Uri, line) != null)
                        remove.Enabled = remove.Visible = true;
                }
                catch (Exception ex)
                {
                    Log.Trace(LogArea.Menus, "Remove Condition: {0}", Log.Describe(ex));
                }
            };
        }

        /// <summary>The popup for one field of the breakpoint at <paramref name="line"/>, at
        /// the mouse; Enter applies (making the breakpoint when there is none), Esc cancels.</summary>
        public static async void Edit(object editor, int line, SpecField field)
        {
            try
            {
                var access = new ScriptEditorAccess(editor);
                var uri = access.Code?.Uri;
                var ed = access.Monaco;
                if (uri == null || ed == null || line <= 0)
                    return;
                var python = IsPython(uri, access.Language);
                var spec = BreakpointSpecs.Find(uri, line);
                var current = spec == null ? "" : field == SpecField.Condition ? spec.Condition : field == SpecField.HitCount ? spec.HitCondition : spec.LogMessage;
                var text = Prompt(editor, line, field, python, current, spec?.LastError);
                if (text == null)
                    return;
                if (text.Trim().Length > 0)
                {
                    var lines = await BreakpointMarkers.AllBreakpointLines(ed);
                    if (!lines.Contains(line))
                        await ed.SetBreakpointAsync(line);
                }
                BreakpointSpecs.Set(uri, line, s =>
                {
                    if (field == SpecField.Condition)
                        s.Condition = text;
                    else if (field == SpecField.HitCount)
                        s.HitCondition = text;
                    else
                        s.LogMessage = text;
                });
                if (!MiniVivisectionSettings.ConditionalBreakpoints)
                    UserLog.Warning("Conditional breakpoints are off (V Settings > Debugger > Conditional breakpoints\u2026): line {0} pauses every time.", line);
            }
            catch (Exception ex)
            {
                UserLog.Error("Can't change the breakpoint at line {0}: {1}", line, ScriptEditorHook.FirstLine(ex));
            }
        }

        private static readonly Dictionary<SpecField, string> Titles = new Dictionary<SpecField, string>
        {
            [SpecField.Condition] = "Breakpoint condition",
            [SpecField.HitCount] = "Hit count",
            [SpecField.LogMessage] = "Log message",
        };

        // The dialog has no buttons.
        private const string DialogKeys = " -- Enter: OK, Esc: cancel, empty: remove";

        private static string Hint(SpecField field, bool python)
        {
            switch (field)
            {
                case SpecField.Condition:
                    return python
                        ? "Pauses only when this is true, e.g.  i > 3 and crv.IsClosed  (Python, at the line)" + DialogKeys
                        : "Pauses only when this is true, e.g.  i > 3 and crv.IsClosed  (C# and IronPython: names, members, calls, operators; and/or/not or &&/||/!)" + DialogKeys;
                case SpecField.HitCount:
                    return "Pauses at the n-th hit:  5   >= 5   % 10 (every 10th)  -- after the condition" + DialogKeys;
                default:
                    return "Printed to the Terminal instead of pausing; {expressions} are filled in, e.g.  i = {i}" + DialogKeys;
            }
        }

        /// <summary>A small modal box at the mouse; null when cancelled.</summary>
        private static string Prompt(object editor, int line, SpecField field, bool python, string current, string lastError)
        {
            var box = new TextBox { Text = current, Width = 420 };
            var hint = new Label { Text = Hint(field, python), TextColor = Colors.Gray, Font = SystemFonts.Default(8) };
            var error = new Label { Text = lastError == null ? "" : "Last run: " + lastError, TextColor = Color.FromArgb(220, 50, 50), Font = SystemFonts.Default(8) };
            var dialog = new Dialog<string>
            {
                Title = Titles[field] + " -- line " + line,
                WindowStyle = WindowStyle.Utility,
                Resizable = false,
                Minimizable = false,
                Maximizable = false,
                ShowInTaskbar = false,
                Padding = new Padding(8, 6),
                Content = new StackLayout { Spacing = 3, Items = { box, hint, error } },
            };
            box.KeyDown += (sender, e) =>
            {
                if (e.Key == Keys.Escape)
                {
                    e.Handled = true;
                    dialog.Close(null);
                }
                else if (e.Key == Keys.Enter)
                {
                    e.Handled = true;
                    var problem = Validate(field, python, box.Text);
                    if (problem == null)
                        dialog.Close(box.Text.Trim());
                    else
                        error.Text = problem;
                }
            };
            dialog.Shown += (sender, e) =>
            {
                box.Focus();
                box.SelectAll();
            };
            var mouse = Mouse.Position;
            dialog.Location = new Point((int)mouse.X - 20, (int)mouse.Y + 12);
            var owner = (editor as Control)?.ParentWindow ?? editor as Window;
            return owner != null ? dialog.ShowModal(owner) : dialog.ShowModal();
        }

        /// <summary>Null when <paramref name="text"/> can be used, else what's wrong.</summary>
        internal static string Validate(SpecField field, bool python, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0)
                return null; // clears it
            switch (field)
            {
                case SpecField.Condition:
                    return ExpressionError(text, python);
                case SpecField.HitCount:
                    return BreakpointConditions.HitSyntaxError(text);
                default:
                    foreach (Match part in Regex.Matches(text, @"\{\{|\}\}|\{([^{}]+)\}"))
                    {
                        if (part.Groups[1].Success && ExpressionError(part.Groups[1].Value, python) is string problem)
                            return "{" + part.Groups[1].Value + "}: " + problem;
                    }
                    return null;
            }
        }

        /// <summary>Syntax check of a condition or a {part}. Python: only where Python can be
        /// reached without waiting for a paused script's GIL (ScriptEditorHook.TryPython);
        /// when it can't, unchecked here -- an error shows at the first hit.</summary>
        private static string ExpressionError(string expression, bool python)
        {
            if (!python)
                return CSharpConditions.SyntaxError(expression);
            var hook = MiniVivisectionPlugin.Instance?.Hook;
            if (hook == null)
                return null;
            return hook.TryPython(() => PythonBridge.SyntaxError(expression), out var problem) ? problem : null;
        }

        /// <summary>Whether the script runs in Rhino's Python 3 (CPython), which the plug-in's
        /// Python features reach -- not IronPython 2 (ScriptKinds), not C#.</summary>
        internal static bool IsPython(Uri uri, string language) => ScriptKinds.IsPython3(uri, language);
    }
}
