using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Rhino;
using Rhino.Runtime.Code.Execution;

namespace MiniVivisection
{
    /// <summary>A watch's value in another thread of the script ("In every thread"): its
    /// row under the watch, named and drawn as "expression @thread".</summary>
    internal sealed class ThreadValue
    {
        public ThreadValue(string thread, string path, ExecVariable variable, string error)
        {
            Thread = thread;
            Path = path;
            Variable = variable;
            Error = error;
        }

        public string Thread { get; }

        /// <summary>"pt @Worker-2": the drawing's name too (selection both ways).</summary>
        public string Path { get; }

        /// <summary>RhinoCode's variable for the value (members expand), or null.</summary>
        public ExecVariable Variable { get; }

        /// <summary>Why the thread has no value (not defined there yet), or null.</summary>
        public string Error { get; }
    }

    /// <summary>One expression of the "V Watch" tab.</summary>
    internal sealed class RhinoWatch : INotifyPropertyChanged
    {
        private string _expression;
        private string _interpretation = "auto";
        private string _status = "";
        private bool _visible = true;
        private bool? _eachThread;
        private bool _available;

        public RhinoWatch(string expression, bool visible = true)
        {
            _expression = expression;
            _visible = visible;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Expression
        {
            get => _expression;
            set => Set(ref _expression, (value ?? "").Trim());
        }

        /// <summary>drawn, drawn (n), the value's type, or why it has none.</summary>
        public string Status
        {
            get => _status;
            set => Set(ref _status, value ?? "");
        }

        /// <summary>The last pause could evaluate it; false while not paused (the tab
        /// shows it grayed out).</summary>
        public bool Available
        {
            get => _available;
            set => Set(ref _available, value);
        }

        /// <summary>Interpret As: "auto" (the first candidate) or a chosen interpretation
        /// (minivivisection_visualize: points, polyline, curve, ...).</summary>
        public string Interpretation
        {
            get => _interpretation;
            set => Set(ref _interpretation, string.IsNullOrEmpty(value) ? "auto" : value);
        }

        /// <summary>The value at the last pause, as the Variables tab shows it.</summary>
        public string Value { get; private set; } = "";

        /// <summary>The value's type at the last pause.</summary>
        public string Type { get; private set; } = "";

        /// <summary>RhinoCode's variable for the value at the last pause -- its members
        /// expand in the tab -- or null.</summary>
        public ExecVariable Variable { get; private set; }

        private readonly HashSet<string> _hiddenThreads = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Threads (by name: "main", "script", "Worker-2") whose value of this watch
        /// isn't drawn (In every thread: the eye on a thread's row).</summary>
        public IReadOnlyCollection<string> HiddenThreads => _hiddenThreads;

        public bool IsThreadHidden(string thread) => thread != null && _hiddenThreads.Contains(thread);

        public void SetThreadHidden(string thread, bool hidden)
        {
            if (thread == null || (hidden ? !_hiddenThreads.Add(thread) : !_hiddenThreads.Remove(thread)))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HiddenThreads)));
        }

        /// <summary>The paused thread's name at the last pause, when the watch was in every thread.</summary>
        public string PausedThread { get; private set; }

        internal void SetPausedThread(string thread) => PausedThread = thread;

        /// <summary>Its values in the other threads at the last pause (In every thread), else empty.</summary>
        public IReadOnlyList<ThreadValue> ThreadValues { get; private set; } = new ThreadValue[0];

        internal void SetThreadValues(IReadOnlyList<ThreadValue> values) => ThreadValues = values ?? new ThreadValue[0];

        internal void SetDetails(ExecVariable variable)
        {
            Variable = variable;
            Value = variable is null ? "" : Shorten(variable.GetValueRepr());
            Type = variable is null ? "" : variable.Id?.TypeIdentifier ?? "";
        }

        private static string Shorten(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 200 ? text : text.Substring(0, 200) + "...";
        }

        /// <summary>In every thread: also evaluated in each other thread's frame of the
        /// script (read only), drawn once per thread (Threads, V Watch). Null: automatic --
        /// on while the script runs more than one thread (async scripts with workers).</summary>
        public bool? EachThread
        {
            get => _eachThread;
            set => Set(ref _eachThread, value);
        }

        /// <summary>Drawn in Rhino (the Show column).</summary>
        public bool Visible
        {
            get => _visible;
            set => Set(ref _visible, value);
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// The expressions drawn in Rhino while a Script Editor's debugger is paused -- the
    /// "V Watch" tab's list, separate from the editor's own Watch list. Items live on
    /// the UI thread; the paused thread reads <see cref="Snapshot"/>. Kept in the
    /// plug-in's settings, so it survives Rhino sessions.
    /// </summary>
    internal sealed class RhinoWatchList
    {
        private const string SettingsKey = "RhinoWatches";
        private HashSet<string> _highlighted = new HashSet<string>(StringComparer.Ordinal);
        private (string Expression, bool Visible, string Interpretation, bool? EachThread, string[] HiddenThreads)[] _snapshot =
            new (string, bool, string, bool?, string[])[0];

        public RhinoWatchList()
        {
            Items.CollectionChanged += (s, e) =>
            {
                foreach (RhinoWatch item in e.NewItems ?? new RhinoWatch[0])
                    item.PropertyChanged += OnItemChanged;
                foreach (RhinoWatch item in e.OldItems ?? new RhinoWatch[0])
                    item.PropertyChanged -= OnItemChanged;
                Commit();
            };
            var settings = MiniVivisectionPlugin.Instance?.Settings;
            if (settings != null && settings.TryGetStringList(SettingsKey, out var saved))
            {
                foreach (var entry in saved.Where(e => !string.IsNullOrWhiteSpace(e)))
                {
                    var watch = Parse(entry);
                    if (watch != null)
                        Items.Add(watch);
                }
            }
        }

        /// <summary>The expressions or their Show flags changed (UI thread).</summary>
        public event Action Changed;

        /// <summary>A pause's statuses arrived (UI thread).</summary>
        public event Action StatusesChanged;

        /// <summary>A watch's drawing was clicked in Rhino: the tab selects its row -- the
        /// item's (index) when it's shown (UI thread).</summary>
        public event Action<string, int> SelectRequested;

        public void RequestSelect(string expression, int index) => SelectRequested?.Invoke(expression, index);

        /// <summary>Objects being edited were selected or deselected in Rhino (UI thread).</summary>
        public event Action HighlightChanged;

        /// <summary>Auto edit: every shown watch's geometry is editable in Rhino at every
        /// pause, written back on change. Kept in the plug-in's settings.</summary>
        public bool AutoEdit
        {
            get => MiniVivisectionSettings.AutoEdit;
            set => MiniVivisectionSettings.AutoEdit = value;
        }

        /// <summary>Whether <paramref name="expression"/> (a watch, or a member's path) belongs
        /// to an object selected in Rhino.</summary>
        public bool IsHighlighted(string expression) => expression != null && _highlighted.Contains(expression);

        /// <summary>The expressions of the edited objects selected in Rhino (UI thread).</summary>
        public void SetHighlighted(HashSet<string> expressions)
        {
            if (_highlighted.SetEquals(expressions))
                return;
            _highlighted = expressions;
            HighlightChanged?.Invoke();
        }

        /// <summary>The list, for the tab's grid (UI thread only).</summary>
        public ObservableCollection<RhinoWatch> Items { get; } = new ObservableCollection<RhinoWatch>();

        /// <summary>The list for the paused thread.</summary>
        public (string Expression, bool Visible, string Interpretation, bool? EachThread, string[] HiddenThreads)[] Snapshot => _snapshot;

        /// <summary>Adds an expression -- or shows it again when it is listed (any thread).</summary>
        public void Add(string expression, string interpretation = null)
        {
            expression = (expression ?? "").Trim();
            if (expression.Length == 0)
                return;
            OnUiThread(() =>
            {
                var existing = Items.FirstOrDefault(i => i.Expression == expression);
                if (existing == null)
                {
                    existing = new RhinoWatch(expression);
                    if (interpretation != null)
                        existing.Interpretation = interpretation;
                    Items.Add(existing);
                    return;
                }
                existing.Visible = true;
                if (interpretation != null)
                    existing.Interpretation = interpretation;
            });
        }

        public void Remove(IEnumerable<RhinoWatch> watches)
        {
            foreach (var watch in watches.ToList())
                Items.Remove(watch);
        }

        /// <summary>The statuses from a pause, which expressions it could evaluate, and
        /// their values' details (any thread).</summary>
        public void SetStatuses(IReadOnlyDictionary<string, string> statuses, ICollection<string> evaluated, IReadOnlyDictionary<string, ExecVariable> details,
            IReadOnlyDictionary<string, List<ThreadValue>> threads = null, string pausedThread = null)
        {
            OnUiThread(() =>
            {
                foreach (var item in Items)
                {
                    item.SetThreadValues(threads != null && threads.TryGetValue(item.Expression, out var inThreads) ? inThreads : null);
                    item.SetPausedThread(threads != null && threads.ContainsKey(item.Expression) ? pausedThread : null);
                    item.Status = statuses.TryGetValue(item.Expression, out var status) ? status : (item.Visible ? "" : "hidden");
                    item.Available = !item.Visible || evaluated.Contains(item.Expression);
                    item.SetDetails(details.TryGetValue(item.Expression, out var variable) ? variable : null);
                }
                StatusesChanged?.Invoke();
            });
        }

        /// <summary>The script continued or ended: nothing can be evaluated (any thread).</summary>
        public void SetNotPaused(string status)
        {
            OnUiThread(() =>
            {
                foreach (var item in Items)
                {
                    item.SetThreadValues(null);
                    item.Available = false;
                    item.Status = item.Visible ? status : "hidden";
                }
                StatusesChanged?.Invoke();
            });
        }

        private void OnItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RhinoWatch.Expression) || e.PropertyName == nameof(RhinoWatch.Visible) ||
                e.PropertyName == nameof(RhinoWatch.Interpretation) || e.PropertyName == nameof(RhinoWatch.EachThread) ||
                e.PropertyName == nameof(RhinoWatch.HiddenThreads))
                Commit();
        }

        private void Commit()
        {
            _snapshot = Items.Where(i => !string.IsNullOrWhiteSpace(i.Expression)).Select(i => (i.Expression, i.Visible, i.Interpretation, i.EachThread, i.HiddenThreads.ToArray())).ToArray();
            MiniVivisectionPlugin.Instance?.Settings.SetStringList(SettingsKey,
                Items.Where(i => !string.IsNullOrWhiteSpace(i.Expression)).Select(Format).ToArray());
            Changed?.Invoke();
        }

        // Kept as JSON ({"e": expression, "v": shown, "i": interpretation, "th": in every
        // thread, when chosen}); earlier versions kept the expression, "!" in front when
        // hidden, and "t" (always written, so only its "on" was a choice).
        private static string Format(RhinoWatch watch)
        {
            var fields = new Dictionary<string, object> { ["e"] = watch.Expression, ["v"] = watch.Visible, ["i"] = watch.Interpretation };
            if (watch.EachThread.HasValue)
                fields["th"] = watch.EachThread.Value;
            if (watch.HiddenThreads.Count > 0)
                fields["hide"] = watch.HiddenThreads.OrderBy(t => t, StringComparer.Ordinal).ToArray();
            return System.Text.Json.JsonSerializer.Serialize(fields);
        }

        private static RhinoWatch Parse(string entry)
        {
            if (entry.StartsWith("{"))
            {
                try
                {
                    using (var json = System.Text.Json.JsonDocument.Parse(entry))
                    {
                        var root = json.RootElement;
                        var watch = new RhinoWatch(root.GetProperty("e").GetString(), !root.TryGetProperty("v", out var v) || v.GetBoolean());
                        if (root.TryGetProperty("i", out var i))
                            watch.Interpretation = i.GetString();
                        if (root.TryGetProperty("th", out var th))
                            watch.EachThread = th.GetBoolean();
                        else if (root.TryGetProperty("t", out var t) && t.GetBoolean())
                            watch.EachThread = true;
                        if (root.TryGetProperty("hide", out var hide) && hide.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var thread in hide.EnumerateArray())
                                watch.SetThreadHidden(thread.GetString(), true);
                        }
                        return watch;
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return entry.StartsWith("!") ? new RhinoWatch(entry.Substring(1), visible: false) : new RhinoWatch(entry);
        }

        private static void OnUiThread(Action action)
        {
            if (RhinoApp.InvokeRequired)
                RhinoApp.InvokeOnUiThread(action);
            else
                action();
        }
    }
}
