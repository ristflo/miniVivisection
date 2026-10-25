using System;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// The "V Threads" tab and page: the script's Python threads (Threads) -- one row per
    /// thread with its state and where it is, expanding into its stack. Refreshed at every
    /// pause; while the script runs, "Where is everyone?" takes a snapshot (hangs,
    /// deadlocks). "Break now" pauses the script at its next line, in any thread or the
    /// selected one. Double-click a row: that line in the code (the script open in the
    /// editor). Options: "Tab (bottom): V Threads", "Page (left): V Threads".
    /// </summary>
    internal sealed class ThreadsView : Panel
    {
        private readonly object _editor;
        private readonly ScriptEditorHook _hook;
        private readonly Label _status = new Label { Wrap = WrapMode.Word };
        private readonly TreeGridView _tree;
        private readonly Button _where = new Button { Text = "Where is everyone?", ToolTip = "Where each thread of the script is now (also while it runs: hangs, deadlocks)." };
        private readonly Button _break = new Button { Text = "Break now", ToolTip = "Pause the script at its next line, in whichever thread gets there first (while debugging)." };
        private readonly Button _breakThread = new Button { Text = "Break in thread", ToolTip = "Pause the script at the selected thread's next line (while debugging)." };
        private readonly Button _debug = new Button { Text = "Debug", ToolTip = "Run the script in the debugger (the editor's Run > Debug): Break now works then, no breakpoint needed." };
        private readonly UITimer _timer = new UITimer { Interval = 0.5 };
        private string _shownEditorState;
        private const string NeedsDebug = "Break now needs a debug run -- a plain Run isn't in the debugger. Click Debug (or set a breakpoint and press F5).";
        private DebugControlsState? _shownState;

        public ThreadsView(object editor, ScriptEditorHook hook, bool narrow = false)
        {
            _editor = editor;
            _hook = hook;
            _tree = new TreeGridView
            {
                ShowHeader = true,
                AllowMultipleSelection = false,
                Columns =
                {
                    new GridColumn { HeaderText = "Thread", DataCell = new TextBoxCell(0), Width = narrow ? 110 : 160 },
                    new GridColumn { HeaderText = "State", DataCell = new TextBoxCell(1), Width = narrow ? 120 : 170 },
                    new GridColumn { HeaderText = "Where", DataCell = new TextBoxCell(2), Expand = true },
                },
            };
            _tree.CellDoubleClick += (sender, e) => Reveal(e.Item as TreeGridItem);
            _tree.Activated += (sender, e) => Reveal(e.Item as TreeGridItem);
            _tree.SelectedItemChanged += (sender, e) => UpdateButtons();
            _where.Click += async (sender, e) =>
            {
                _where.Enabled = false;
                try
                {
                    await Threads.TakeAsync(DebugControls(), _hook, TimeSpan.FromSeconds(3));
                }
                finally
                {
                    _where.Enabled = true;
                }
            };
            _break.Click += (sender, e) => Break(0);
            _breakThread.Click += (sender, e) =>
            {
                if (SelectedRow() is ThreadRow row)
                    Break(row.Ident);
            };
            _timer.Elapsed += (sender, e) => UpdateState();
            _debug.Click += (sender, e) => StartDebugging();

            Load += (sender, e) =>
            {
                Threads.Changed += OnChanged;
                _timer.Start();
                Show(Threads.Latest(DebugControls()));
                UpdateState();
            };
            UnLoad += (sender, e) =>
            {
                Threads.Changed -= OnChanged;
                _timer.Stop();
            };

            EditorTheme.Apply(editor, this, _tree, _status);
            var buttons = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 4, Items = { _debug, _where, _break, _breakThread } };
            Content = new TableLayout
            {
                Padding = new Padding(4, 2),
                Spacing = new Size(4, 4),
                Rows =
                {
                    new TableRow(narrow ? (Control)new Scrollable { Content = buttons, Border = BorderType.None, ExpandContentHeight = false } : buttons),
                    new TableRow(_status),
                    new TableRow(_tree) { ScaleHeight = true },
                },
            };
            Show(null);
        }

        private IDebugControls DebugControls() => ScriptEditorHook.DebugControlsOf(_editor);

        private void OnChanged(IDebugControls controls)
        {
            Application.Instance.AsyncInvoke(() =>
            {
                if (ReferenceEquals(controls, DebugControls()))
                    Show(Threads.Latest(controls));
            });
        }

        private void Break(long ident)
        {
            var controls = DebugControls();
            if (controls == null || controls.State == DebugControlsState.Stopped || controls.State == DebugControlsState.Disconnected)
            {
                _status.Text = "Break now works while the script runs in the debugger (F5 with a breakpoint, or Debug).";
                return;
            }
            if (controls.State == DebugControlsState.Paused)
            {
                _status.Text = "Already paused.";
                return;
            }
            Threads.RequestBreak(controls, ident);
            _status.Text = ident == 0
                ? "Pausing at the script's next line (a thread waiting in a lock or join pauses when it gets going again)..."
                : "Pausing at that thread's next line...";
            UserLog.Info("Break now{0}.", ident == 0 ? "" : " in the selected thread");
        }

        /// <summary>Running, paused or not running: the buttons and the line above the list follow.</summary>
        /// <summary>The editor's state: PlayingCode (a plain Run), DebuggingCode, Idling, ...</summary>
        private string EditorState()
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var state = _editor.GetType().GetProperty("ActiveState", any)?.GetValue(_editor);
            return state?.GetType().GetProperty("CurrentState", any)?.GetValue(state)?.ToString();
        }

        /// <summary>The editor's own Debug command (Run > Debug, editor.debugCode): the
        /// active script in the debugger, with or without breakpoints.</summary>
        private void StartDebugging()
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            try
            {
                var commands = _editor.GetType().GetProperty("ActiveCommands", any)?.GetValue(_editor);
                var find = commands?.GetType().GetMethod("Find", any, null, new[] { typeof(string) }, null);
                if (!(find?.Invoke(commands, new object[] { "editor.debugCode" }) is Command debug))
                {
                    _status.Text = "The editor's Debug command isn't there: use Run > Debug.";
                    return;
                }
                if (!debug.Enabled)
                {
                    _status.Text = "The editor can't debug now (a run is going on, or no script is open).";
                    return;
                }
                debug.Execute();
                UserLog.Info("Debug: the script runs in the debugger -- Break now and Where is everyone? work while it runs.");
            }
            catch (Exception ex)
            {
                _status.Text = "Can't start the debugger: " + ScriptEditorHook.FirstLine(ex);
            }
        }

        private void UpdateState()
        {
            var state = DebugControls()?.State;
            var editorState = EditorState();
            if (editorState != _shownEditorState)
            {
                _shownEditorState = editorState;
                UpdateButtons();
                if (editorState == "PlayingCode" || editorState == "PlayingForeverCode")
                    _status.Text = "Running without the debugger (Run). " + NeedsDebug + " Where is everyone? works.";
            }
            if (state == _shownState)
                return;
            _shownState = state;
            UpdateButtons();
            if (state != DebugControlsState.Paused && Threads.Latest(DebugControls()) is Threads.Snapshot last && !last.WhilePaused && last.Rows.Count == 0)
                Show(last);
        }

        private void UpdateButtons()
        {
            var state = DebugControls()?.State;
            var editorState = _shownEditorState ?? EditorState();
            var debugging = editorState == "DebuggingCode";
            var running = debugging && state == DebugControlsState.Debugging;
            _break.Enabled = running && !Threads.BreakRequested;
            _breakThread.Enabled = running && SelectedRow() != null;
            _debug.Enabled = editorState == "EditingCode" || editorState == "Idling";
            var why = debugging ? (state == DebugControlsState.Paused ? "The script is paused." : null)
                : editorState == "PlayingCode" || editorState == "PlayingForeverCode" ? NeedsDebug
                : "The script isn't running in the debugger: click Debug (or set a breakpoint and press F5).";
            _break.ToolTip = "Pause the script at its next line, in whichever thread gets there first." + (why == null ? "" : "\n" + why);
            _breakThread.ToolTip = "Pause the script at the selected thread's next line." + (why == null ? "" : "\n" + why);
        }

        private ThreadRow SelectedRow()
        {
            var item = _tree.SelectedItem as TreeGridItem;
            while (item != null && !(item.Tag is ThreadRow))
                item = item.Parent as TreeGridItem;
            return item?.Tag as ThreadRow;
        }

        private void Show(Threads.Snapshot snapshot)
        {
            var items = new TreeGridItemCollection();
            if (snapshot == null)
            {
                _status.Text = "The script's threads show here at a pause (Python 3). While it runs: \"Where is everyone?\".";
            }
            else
            {
                var when = snapshot.Taken.ToString("HH:mm:ss");
                if (snapshot.Rows.Count > 0)
                    _status.Text = (snapshot.WhilePaused
                        ? string.Format("Paused in thread {0} -- {1} thread(s), {2}.", snapshot.PausedLabel, snapshot.Rows.Count, when)
                        : string.Format("Running -- {0} thread(s) at {1}.", snapshot.Rows.Count, when)) + (snapshot.Note == null ? "" : " " + snapshot.Note);
                else
                    _status.Text = snapshot.Note ?? "No threads.";
                foreach (var row in snapshot.Rows)
                {
                    var label = row.Label + (row.IsDaemon ? " (daemon)" : "");
                    var item = new TreeGridItem(label, row.State, row.Location?.Where ?? "") { Tag = row, Expanded = false };
                    foreach (var frame in row.Frames)
                        item.Children.Add(new TreeGridItem("", frame.Own ? "" : "library", frame.Where) { Tag = frame });
                    items.Add(item);
                }
            }
            _tree.DataStore = items;
            UpdateButtons();
        }

        /// <summary>The row's line in the code, when it's in the script open in the editor.</summary>
        private void Reveal(TreeGridItem item)
        {
            var frame = item?.Tag as ThreadFrame ?? (item?.Tag as ThreadRow)?.Location;
            if (frame == null)
                return;
            var access = new ScriptEditorAccess(_editor);
            var path = access.ScriptPath;
            if (path == null || !string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(frame.File), StringComparison.OrdinalIgnoreCase))
            {
                _status.Text = string.Format("Line {0} of {1}: open that file in the editor to see it.", frame.Line, frame.File);
                return;
            }
            var monaco = access.Monaco;
            if (monaco != null)
                _ = monaco.SetAndRevealCursorPositionAsync(frame.Line, 1);
        }
    }
}
