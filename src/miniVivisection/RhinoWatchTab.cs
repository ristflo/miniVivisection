using System;
using System.Collections.Generic;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// The Script Editor's "V Watch" tab: the expressions drawn in Rhino while the
    /// debugger is paused (RhinoWatchList), as a tree like the editor's Variables tab --
    /// each watch with its value and type, expandable into the value's members (RhinoCode's
    /// own variables, so Rhino objects show their data the same way), a Show checkbox,
    /// and what the last pause found (drawn, its type, an error). Expressions are any
    /// Python the paused frame can evaluate: <c>crv</c>, <c>curves[0]</c>,
    /// <c>[c.PointAtEnd for c in curves]</c>.
    ///
    /// Grayed out: what the last pause couldn't evaluate, and everything while not
    /// paused. Highlighted: watches whose objects being edited are selected in Rhino.
    /// Del removes the selected watches; Auto edit makes every watched geometry editable.
    /// </summary>
    internal sealed class RhinoWatchTab : Panel
    {
        /// <summary>A row: a watch, or a member of a watch's value.</summary>
        private sealed class Node : TreeGridItem
        {
            public RhinoWatch Watch;      // a watch's row; null for a member
            public ExecVariable Variable; // its value's variable, for the members
            public string Path = "";      // the row's expression: the watch, or watch.member / watch[i]
            public bool Loaded;           // members read

            public string Name
            {
                get => Watch?.Expression ?? MemberName;
                set
                {
                    if (Watch != null)
                        Watch.Expression = value;
                }
            }

            public string MemberName = "";

            /// <summary>A thread's row (In every thread): its value's text when it has no
            /// variable (or why it has no value), and its thread.</summary>
            public string Text;
            public string Thread;

            /// <summary>The thread a row's eye shows and hides: a thread's row, or the paused
            /// thread's row (In every thread); else null.</summary>
            public string EyeThread;

            /// <summary>Members not read yet still show the expander: they're read when
            /// the row is expanded.</summary>
            public bool CanExpand;

            public override bool Expandable => CanExpand || Count > 0;
            public string Value => Watch?.Value ?? (Variable is null ? Text ?? "" : Shorten(Variable.GetValueRepr()));
            public string Type => Watch?.Type ?? Variable?.Id?.TypeIdentifier ?? "";
            public string Status => Watch?.Status ?? "";
        }

        private readonly object _editor;
        private readonly ScriptEditorHook _hook;
        private readonly RhinoWatchList _list;
        private readonly TreeGridView _tree;
        private readonly TextBox _add;
        private readonly StateToggle _autoEdit;
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
        private bool _selectingFromRhino;

        /// <param name="narrow">For the left panel: Show, Expression and Value only, the
        /// buttons in two columns.</param>
        public RhinoWatchTab(object editor, ScriptEditorHook hook, bool narrow = false)
        {
            _editor = editor;
            _hook = hook;
            _list = hook.WatchListFor(editor);

            var configuring = false;
            var showColumn = new GridColumn
            {
                HeaderText = "Show",
                Width = 44,
                DataCell = new CustomCell
                {
                    GetIdentifier = args => (args.Item as Node)?.Watch != null ? "watch" : (args.Item as Node)?.EyeThread != null ? "thread" : "member",
                    CreateCell = args =>
                    {
                        if (args.Item is Node threadRow && threadRow.Watch == null && threadRow.EyeThread != null)
                        {
                            // a thread's value (In every thread): drawn or not, by thread
                            var threadEye = new StateToggle(DrawnIcons.EyeOpen, DrawnIcons.EyeClosed, "This thread's value drawn in Rhino", null, showState: false);
                            threadEye.Toggled += (s, e) =>
                            {
                                if (!configuring && threadEye.Tag is Node row && WatchOf(row) is RhinoWatch owner)
                                    owner.SetThreadHidden(row.EyeThread, !threadEye.Checked);
                            };
                            return threadEye;
                        }
                        if ((args.Item as Node)?.Watch == null)
                            return new Panel();
                        // An eye, open and blue or closed and gray: plainer than a check box in the dark editor.
                        var eye = new StateToggle(DrawnIcons.EyeOpen, DrawnIcons.EyeClosed, "Drawn in Rhino", null, showState: false);
                        eye.Toggled += (s, e) =>
                        {
                            if (!configuring && eye.Tag is RhinoWatch watch)
                                watch.Visible = eye.Checked;
                        };
                        return eye;
                    },
                    ConfigureCell = (args, control) =>
                    {
                        if (control is StateToggle threadEye && args.Item is Node row && row.Watch == null && row.EyeThread != null)
                        {
                            configuring = true;
                            threadEye.Tag = row;
                            threadEye.Checked = !(WatchOf(row)?.IsThreadHidden(row.EyeThread) ?? false);
                            configuring = false;
                        }
                        else if (control is StateToggle eye && args.Item is Node node && node.Watch != null)
                        {
                            configuring = true;
                            eye.Tag = node.Watch;
                            eye.Checked = node.Watch.Visible;
                            configuring = false;
                        }
                    },
                },
            };
            var expressionColumn = new GridColumn
            {
                HeaderText = "Expression",
                DataCell = new TextBoxCell(nameof(Node.Name)),
                Editable = true,
                Width = narrow ? 120 : 200,
            };
            var valueColumn = new GridColumn { HeaderText = "Value", DataCell = new TextBoxCell(nameof(Node.Value)), Width = narrow ? 160 : 260, Expand = narrow };
            var typeColumn = new GridColumn { HeaderText = "Type", DataCell = new TextBoxCell(nameof(Node.Type)), Width = 110, Visible = !narrow };
            var statusColumn = new GridColumn { HeaderText = "In Rhino", DataCell = new TextBoxCell(nameof(Node.Status)), Expand = true, Visible = !narrow };
            _tree = new TreeGridView
            {
                AllowMultipleSelection = true,
                ShowHeader = true,
                Columns = { showColumn, expressionColumn, valueColumn, typeColumn, statusColumn },
            };
            _tree.CellFormatting += (sender, e) =>
            {
                if (!(e.Item is Node node) || e.Column == showColumn)
                    return;
                var watch = WatchOf(node);
                if (_list.IsHighlighted(node.Path))
                    e.BackgroundColor = Color.FromArgb(255, 214, 0, 90); // edited objects selected in Rhino
                if (watch != null && (!watch.Available || !watch.Visible || watch.IsThreadHidden(node.EyeThread)))
                {
                    e.ForegroundColor = Colors.Gray;
                }
                else if (e.Column == expressionColumn && node.Watch != null && node.Watch.Expression.Length > 0)
                {
                    var color = WatchGeometry.ColorFor(node.Watch.Expression);
                    e.ForegroundColor = Color.FromArgb(color.R, color.G, color.B, color.A);
                }
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
            _tree.KeyDown += (sender, e) =>
            {
                if (e.KeyData == Keys.Delete && !_tree.IsEditing)
                {
                    e.Handled = true;
                    RemoveSelected();
                }
            };

            _list.StatusesChanged += Rebuild;
            _tree.SelectedItemChanged += (sender, e) =>
            {
                if (!SelectionSync.Busy && !_selectingFromRhino)
                    _hook.Selection.Highlight(_editor, Selected().SelectMany(DrawnPaths));
            };
            _list.SelectRequested += (expression, index) =>
            {
                var rows = AllNodes((_tree.DataStore as TreeGridItemCollection) ?? new TreeGridItemCollection()).ToList();
                var row = rows.FirstOrDefault(n => n.Path == expression + "[" + index + "]" && (n.Parent as Node)?.Expanded == true)
                    ?? rows.FirstOrDefault(n => n.Path == expression + " @" && n.Watch == null) // In every thread: the paused thread's row
                    ?? rows.FirstOrDefault(n => n.Watch != null && n.Watch.Expression == expression)
                    ?? rows.FirstOrDefault(n => n.Thread != null && n.Path == expression); // a thread's drawing (In every thread)
                if (row?.Parent is Node parent && !parent.Expanded)
                {
                    parent.Expanded = true;
                    _expanded.Add(parent.Path);
                    _tree.ReloadData();
                }
                if (row != null && !ReferenceEquals(_tree.SelectedItem, row) && !_tree.IsEditing)
                {
                    _selectingFromRhino = true; // from Rhino: no echo back into Rhino's selection
                    try
                    {
                        _tree.SelectedItem = row;
                    }
                    finally
                    {
                        _selectingFromRhino = false;
                    }
                }
            };
            _list.HighlightChanged += () =>
            {
                if (!_tree.IsEditing)
                    _tree.ReloadData();
            };
            _list.Items.CollectionChanged += (s, e) => Rebuild();

            _add = new TextBox { PlaceholderText = "expression to watch, e.g. crv or curves[0]" };
            _add.KeyDown += (sender, e) =>
            {
                if (e.KeyData == Keys.Enter)
                {
                    e.Handled = true;
                    AddFromBox();
                }
            };
            var add = ToolButton(DrawnIcons.Add, "Add the expression typed on the left (Enter does too)");
            add.Click += (sender, e) => AddFromBox();
            var remove = ToolButton(DrawnIcons.Remove, "Remove the selected watches (Del)");
            remove.Click += (sender, e) => RemoveSelected();
            var bake = ToolButton(DrawnIcons.Bake, "Bake: copies of the selected values into the document (layer Script Editor Watch::Baked)");
            bake.Click += (sender, e) => ForSelectedWhilePaused("Bake", (controls, expression) => WatchActions.Bake(_hook, controls, expression));
            var edit = ToolButton(DrawnIcons.Edit, "Edit in Rhino: the selected values as Rhino objects; changes go back into the script");
            edit.Click += (sender, e) => EditSelected();
            var clear = ToolButton(DrawnIcons.Clear, "Remove all watches");
            clear.Click += (sender, e) => _list.Remove(_list.Items);
            _autoEdit = new StateToggle(DrawnIcons.AutoEdit, null, "Auto edit",
                "At every pause, every shown watch's geometry becomes Rhino objects you can edit; changes go back into the script.", showState: true)
            {
                Checked = _list.AutoEdit,
                ButtonLook = true, // like the toolbar's other buttons
            };
            _autoEdit.Toggled += (sender, e) => MiniVivisectionSettings.AutoEdit = _autoEdit.Checked; // applied by SettingEffects
            // One setting for every Script Editor and both of its V Watch tabs (console
            // and left panel), also changed on the Options page: every toggle follows it.
            Load += (sender, e) =>
            {
                MiniVivisectionSettings.Changed += OnSettingChanged;
                _autoEdit.Checked = _list.AutoEdit; // changed while hidden
            };
            UnLoad += (sender, e) => MiniVivisectionSettings.Changed -= OnSettingChanged;

            var interpretItem = new ButtonMenuItem { Text = "Interpret as" };
            var removeItem = new ButtonMenuItem((s, e) => RemoveSelected()) { Text = "Remove" };
            var watchItem = new ButtonMenuItem((s, e) => WatchSelectedMembers()) { Text = "Watch in Rhino" };
            var eachThreadItem = new CheckMenuItem { Text = "In every thread", ToolTip = "Also evaluated in each other thread of the script (read only), drawn once per thread. Automatic until chosen: on while the script runs more than one thread." };
            eachThreadItem.Click += (s, e) =>
            {
                foreach (var watch in Selected().Select(n => n.Watch).Where(w => w != null))
                    watch.EachThread = eachThreadItem.Checked;
            };
            var menu = new ContextMenu(
                interpretItem,
                eachThreadItem,
                removeItem,
                watchItem,
                new ButtonMenuItem((s, e) => ForSelectedWhilePaused("Bake", (controls, expression) => WatchActions.Bake(_hook, controls, expression))) { Text = "Bake to Rhino" },
                new ButtonMenuItem((s, e) => EditSelected()) { Text = "Edit in Rhino" });
            menu.Opening += (sender, e) =>
            {
                var nodes = Selected();
                removeItem.Visible = nodes.Any(n => n.Watch != null);
                eachThreadItem.Visible = nodes.Any(n => n.Watch != null);
                eachThreadItem.Checked = nodes.Any(n => n.Watch != null && n.Watch.EachThread != false); // automatic shows as on
                watchItem.Visible = nodes.Any(n => n.Watch == null);
                FillInterpretations(interpretItem, nodes.FirstOrDefault(n => n.Watch != null)?.Watch);
            };
            _tree.ContextMenu = menu;

            EditorTheme.Apply(editor, this, _add, _tree);
            Content = new TableLayout
            {
                Padding = new Padding(4, 2),
                Spacing = new Size(4, 4),
                Rows =
                {
                    // One row: the expression to add, then the actions as icons (tooltips name them).
                    new TableRow(new TableLayout(new TableRow(new TableCell(_add, true), add, remove, bake, edit, _autoEdit, clear)) { Spacing = new Size(2, 0) }),
                    new TableRow(_tree) { ScaleHeight = true },
                },
            };
            Rebuild();
        }

        public void FocusInput() => _add.Focus();

        private void OnSettingChanged(string key)
        {
            if (key == "RhinoWatchesAutoEdit")
                Application.Instance.AsyncInvoke(() => _autoEdit.Checked = _list.AutoEdit);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                MiniVivisectionSettings.Changed -= OnSettingChanged;
            base.Dispose(disposing);
        }

        private static Button ToolButton(Action<Graphics> icon, string tip) =>
            new Button { Image = DrawnIcons.Toolbar(icon), Width = 26, ToolTip = tip, ImagePosition = ButtonImagePosition.Overlay };

        private void AddFromBox()
        {
            _list.Add(_add.Text);
            _add.Text = "";
        }

        /// <summary>The rows anew from the list -- after a pause, or a change of the list --
        /// with the expanded ones expanded again (their members read anew) and the
        /// selection kept. Not while a cell is being edited.</summary>
        private void Rebuild()
        {
            if (_tree.IsEditing)
                return;
            var selected = new HashSet<string>(Selected().Select(n => n.Path), StringComparer.Ordinal);
            var rows = new TreeGridItemCollection();
            foreach (var watch in _list.Items)
            {
                var node = new Node { Watch = watch, Variable = watch.Variable, Path = watch.Expression, CanExpand = watch.Variable?.CanExpand == true };
                if (watch.ThreadValues.Count > 0)
                    AddThreadRows(node, watch);
                if (_expanded.Contains(node.Path))
                    Reexpand(node);
                rows.Add(node);
            }
            _tree.DataStore = rows;
            var reselect = AllNodes(rows).FirstOrDefault(n => selected.Contains(n.Path));
            if (reselect != null)
                _tree.SelectedItem = reselect;
        }

        /// <summary>In every thread: the watch's rows are its threads -- the paused one
        /// (its own value, its members), then each other thread's value, named as drawn
        /// ("pt @Worker-2") so selecting a row highlights that drawing and clicking the
        /// drawing selects the row.</summary>
        private static void AddThreadRows(Node node, RhinoWatch watch)
        {
            node.Children.Add(new Node
            {
                Variable = watch.Variable,
                MemberName = watch.PausedThread == null ? "(paused thread)" : "@" + watch.PausedThread + " (paused)",
                EyeThread = watch.PausedThread,
                Path = watch.Expression + " @",
                Text = watch.Variable is null ? watch.Status : null,
                CanExpand = watch.Variable?.CanExpand == true,
            });
            foreach (var value in watch.ThreadValues)
            {
                node.Children.Add(new Node
                {
                    Variable = value.Variable,
                    MemberName = "@" + value.Thread,
                    Thread = value.Thread,
                    EyeThread = value.Thread,
                    Path = value.Path,
                    Text = value.Error,
                    CanExpand = value.Variable?.CanExpand == true,
                });
            }
            node.Loaded = true;
            node.CanExpand = true;
        }

        /// <summary>The drawings a row stands for: a watch in every thread is all of them
        /// (itself and "expression @thread"), its paused thread's row the watch itself.</summary>
        private static IEnumerable<string> DrawnPaths(Node node)
        {
            if (node.Watch != null && node.Watch.ThreadValues.Count > 0)
                return new[] { node.Path }.Concat(node.Watch.ThreadValues.Select(t => t.Path));
            return new[] { OtherThreadOf(node) != null ? node.Path : PausedPath(node) }; // "pt @Worker-2[0]": that drawing's item
        }

        private void Reexpand(Node node)
        {
            if (!LoadMembers(node))
                return;
            node.Expanded = true;
            foreach (var child in node.Children.OfType<Node>().Where(c => _expanded.Contains(c.Path)))
                Reexpand(child);
        }

        /// <summary>The members of a row's value, read once -- RhinoCode's own expansion,
        /// on the paused thread (Python's members need its GIL). False when there are none
        /// or the script isn't paused.</summary>
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
                Log.Warning(LogArea.Watch, "can't show the members of {0}: {1}", node.Path, ScriptEditorHook.FirstLine(ex));
                return false;
            }
            node.Children.Clear();
            foreach (var member in members)
            {
                var name = member.Id?.Identifier ?? "";
                node.Children.Add(new Node
                {
                    Variable = member,
                    MemberName = name,
                    Path = name.StartsWith("[") ? node.Path + name : node.Path + "." + name,
                    CanExpand = member.CanExpand,
                });
            }
            node.Loaded = true;
            return node.Children.Count > 0;
        }

        private static IEnumerable<Node> AllNodes(IEnumerable<ITreeGridItem> items)
        {
            foreach (var node in items.OfType<Node>())
            {
                yield return node;
                foreach (var child in AllNodes(node.Children))
                    yield return child;
            }
        }

        /// <summary>The watch a row belongs to.</summary>
        private static RhinoWatch WatchOf(Node node)
        {
            ITreeGridItem item = node;
            while (item is Node current)
            {
                if (current.Watch != null)
                    return current.Watch;
                item = current.Parent;
            }
            return null;
        }

        private List<Node> Selected() => _tree.SelectedItems.OfType<Node>().ToList();

        /// <summary>Interpret As for <paramref name="watch"/>: Auto, then the value's
        /// interpretations (read from the paused script), the chosen one checked.</summary>
        private void FillInterpretations(ButtonMenuItem menu, RhinoWatch watch)
        {
            menu.Items.Clear();
            menu.Visible = watch != null;
            if (watch == null)
                return;
            var choices = _hook.InterpretationsOf(_editor, watch.Expression);
            void Add(string id, string label)
            {
                var item = new CheckMenuItem { Text = label, Checked = watch.Interpretation == id };
                item.Click += (s, e) => watch.Interpretation = id;
                menu.Items.Add(item);
            }
            Add("auto", "Auto" + (choices != null && choices.Count > 0 ? " (" + choices[0].Label.ToLowerInvariant() + ")" : ""));
            if (choices == null)
            {
                menu.Items.Add(new ButtonMenuItem { Text = "(pause to see the choices)", Enabled = false });
                return;
            }
            foreach (var choice in choices)
                Add(choice.Id, choice.Label);
        }

        private void RemoveSelected() => _list.Remove(Selected().Where(n => n.Watch != null).Select(n => n.Watch));

        /// <summary>Members picked in the tree become watches of their own.</summary>
        private void WatchSelectedMembers()
        {
            foreach (var node in Selected().Where(n => n.Watch == null))
            {
                if (OtherThreadOf(node) != null)
                    UserLog.Info("Watch: {0} is another thread's value -- its watch is drawn in every thread already.", node.Path);
                else
                    _list.Add(PausedPath(node));
            }
        }

        /// <summary>The other thread a row shows (In every thread: its row, or a member
        /// under it), else null. Those values are read only and not expressions of the
        /// paused frame.</summary>
        private static string OtherThreadOf(Node node)
        {
            for (var n = node; n != null; n = n.Parent as Node)
            {
                if (n.Thread != null)
                    return n.Thread;
            }
            return null;
        }

        /// <summary>A row's expression in the paused frame: the paused thread's row (In every
        /// thread) is the watch itself, its members are the watch's members.</summary>
        private static string PausedPath(Node node)
        {
            var at = node.Path.IndexOf(" @", StringComparison.Ordinal);
            return at < 0 ? node.Path : node.Path.Remove(at, 2);
        }

        private static string Shorten(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 200 ? text : text.Substring(0, 200) + "...";
        }

        /// <summary>Edit in Rhino: a watch as it's drawn (its Interpret As), members as they are.</summary>
        private void EditSelected()
        {
            var controls = ScriptEditorHook.DebugControlsOf(_editor);
            if (controls == null || controls.State != DebugControlsState.Paused)
            {
                UserLog.Warning("Edit in Rhino: the script isn't paused.");
                return;
            }
            foreach (var node in Selected().Where(n => n.Path.Length > 0))
            {
                if (OtherThreadOf(node) is string thread)
                    UserLog.Warning("Edit in Rhino: {0} is thread {1}'s value -- other threads are read only; edit the paused thread's value.", node.Path, thread);
                else
                    _hook.Edits.Start(controls, PausedPath(node), (node.Watch ?? WatchOf(node))?.Interpretation ?? "auto");
            }
        }

        private void ForSelectedWhilePaused(string what, Action<IDebugControls, string> action)
        {
            var controls = ScriptEditorHook.DebugControlsOf(_editor);
            if (controls == null || controls.State != DebugControlsState.Paused)
            {
                UserLog.Warning("{0}: the script isn't paused.", what);
                return;
            }
            foreach (var node in Selected().Where(n => n.Path.Length > 0))
            {
                if (node.Thread != null && what == "Bake")
                {
                    // another thread's value: its drawing, as drawn
                    var baked = WatchActions.Bake(Rhino.RhinoDoc.ActiveDoc, _hook.Conduit.Values.Where(v => v.Name == node.Path).ToList());
                    if (baked == 0)
                        UserLog.Warning("Bake: {0} has nothing drawn.", node.Path);
                }
                else if (OtherThreadOf(node) is string thread)
                {
                    UserLog.Warning("{0}: {1} is a member of thread {2}'s value -- select the thread's row.", what, node.Path, thread);
                }
                else
                {
                    action(controls, PausedPath(node));
                }
            }
        }
    }
}
