using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Rhino;
using Rhino.Geometry;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Finds the open Script Editors and follows their debuggers.
    ///
    /// The editor window (RhinoCodeEditor's internal <c>RCE</c>) has no extension
    /// point, but each editor state has a public <c>DebugControls</c> property of
    /// RhinoCode's public <see cref="IDebugControls"/>, shared by every script in
    /// that editor:
    /// - <see cref="IDebugControls.Expressions"/> is the Watch tab's list -- the plug-in
    ///   draws its own "V Watch" list instead (RhinoWatchList);
    /// - <see cref="IDebugControls.Paused"/> fires on the script's thread right
    ///   before the debugger's "Select Action" prompt;
    /// - <c>Results.CurrentThread.CurrentFrame</c> evaluates a watch to an
    ///   <see cref="ExecVariable"/> holding the live value.
    /// Only finding the editors needs reflection (RhinoCode 8.36; may change with
    /// any Service Release).
    /// </summary>
    internal sealed class ScriptEditorHook : IDisposable
    {
        private const string EditorAssemblyName = "RhinoCodeEditor";
        private const string EditorTypeName = "RhinoCodeEditor.Editor.RCE";

        private readonly WatchConduit _conduit;
        private readonly ConditionalWeakTable<IDebugControls, object> _hooked = new ConditionalWeakTable<IDebugControls, object>();
        private readonly ConditionalWeakTable<object, RhinoWatchList> _watchLists = new ConditionalWeakTable<object, RhinoWatchList>();
        private readonly ConditionalWeakTable<object, RunLog> _runLogs = new ConditionalWeakTable<object, RunLog>();
        private readonly List<WeakReference<IDebugControls>> _hookedList = new List<WeakReference<IDebugControls>>();
        private readonly ConditionalWeakTable<IDebugControls, StrongBox<int>> _pausedThreads =
            new ConditionalWeakTable<IDebugControls, StrongBox<int>>();
        private readonly ConditionalWeakTable<IDebugControls, object> _editors = new ConditionalWeakTable<IDebugControls, object>();
        private Type _editorType;
        private EventInfo _runStartEvent;
        private Delegate _runStartHandler;
        private EventInfo _runEndEvent;
        private Delegate _runEndHandler;
        private bool _selectionPending;
        private DateTime _nextScan;
        private bool _reportedScanError;
        private bool _reportedEditorType;
        private bool _disposed;

        public ScriptEditorHook(WatchConduit conduit)
        {
            _conduit = conduit;
            Edits = new EditSession(this, conduit);
            Selection = new SelectionSync(this);
            Threads.ScriptOf = controls => EditorOf(controls) is object editor ? new ScriptEditorAccess(editor).Code?.Uri : null;
            GrasshopperPauseGuard.AnyPaused = () => PausedControls().Count > 0;
            FreePause.Abandoned = controls =>
            {
                var closed = controls is IDebugControls debugger && _editors.TryGetValue(debugger, out var editor) &&
                             editor is Eto.Forms.Window window && _closedWindows.TryGetValue(window, out _);
                if (closed)
                    Log.Debug(LogArea.Pause, "abandoned: controls {0}, editor {1}; windows: {2}", controls.GetHashCode(), _editors.TryGetValue((IDebugControls)controls, out var e2) ? e2.GetHashCode() : 0,
                        string.Join(", ", Eto.Forms.Application.Instance.Windows.Where(w => _seenWindows.TryGetValue(w, out _)).Select(w =>
                            string.Format("{0} closed {1} visible {2} controls {3}", w.GetHashCode(), _closedWindows.TryGetValue(w, out _), w.Visible, DebugControlsOf(w)?.GetHashCode() ?? 0))));
                return closed;
            };
            FreePause.SkipAtPause = controls => controls is IDebugControls debugger && (FreezeGuardSkips(debugger) || RewiringSkips(debugger));
        }

        // Script Editor windows closed for good (not just hidden): a pause of theirs can't go on.
        private readonly ConditionalWeakTable<Eto.Forms.Window, object> _closedWindows = new ConditionalWeakTable<Eto.Forms.Window, object>();

        /// <summary>Watched values edited as Rhino objects, written back into the script.</summary>
        internal EditSession Edits { get; }

        internal WatchConduit Conduit => _conduit;

        /// <summary>A pause, on the paused thread, after the plug-in's own work (Claude's
        /// step tools wait for it).</summary>
        internal event Action<IDebugControls> PausedForTools;

        /// <summary>A debug run ended (Claude's step tools, queued edits).</summary>
        internal event Action<IDebugControls> EndedForTools;

        /// <summary>Selection between the tabs and Rhino, both ways.</summary>
        internal SelectionSync Selection { get; }

        /// <summary>The Script Editor window following <paramref name="controls"/>, or null.</summary>
        internal object EditorOf(IDebugControls controls) => _editors.TryGetValue(controls, out var editor) ? editor : null;

        /// <summary>The Script Editor showing the code view <paramref name="ed"/> (an
        /// Ed.Eto.Ed), or null.</summary>
        internal object EditorOfMonaco(object ed) => Editors().FirstOrDefault(e => ReferenceEquals(new ScriptEditorAccess(e).Monaco, ed));

        /// <summary>Script Editors this hook follows (open or not yet collected).</summary>
        public int EditorCount
        {
            get
            {
                lock (_hookedList)
                    return _hookedList.Count(r => r.TryGetTarget(out _));
            }
        }

        public void Start()
        {
            RhinoApp.Idle += OnIdle;
            // The Script Editor or RhinoCode's Rhino platform loaded: followed at once (its run
            // events, FreePause), not at Rhino's next idle moment -- a run started by a macro
            // right after opening the first editor would go unseen until then
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            FreePause.Waiting += OnFreePauseWaiting;
            RhinoDoc.SelectObjects += OnSelectionChanged;
            RhinoDoc.DeselectObjects += OnSelectionChanged;
            RhinoDoc.DeselectAllObjects += OnDeselectAll;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            RhinoApp.Idle -= OnIdle;
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            FreePause.Waiting -= OnFreePauseWaiting;
            RhinoDoc.SelectObjects -= OnSelectionChanged;
            RhinoDoc.DeselectObjects -= OnSelectionChanged;
            RhinoDoc.DeselectAllObjects -= OnDeselectAll;
            Edits.Dispose();
            try
            {
                _runStartEvent?.RemoveEventHandler(null, _runStartHandler);
                _runEndEvent?.RemoveEventHandler(null, _runEndHandler);
            }
            catch (Exception)
            {
                // Rhino is closing.
            }
            lock (_hookedList)
            {
                foreach (var reference in _hookedList)
                {
                    if (reference.TryGetTarget(out var controls) && _hooked.TryGetValue(controls, out _))
                        Unhook(controls);
                }
                _hookedList.Clear();
            }
        }

        // An editor opened while Rhino is idle is found within a second; one that
        // starts a run right away is found through RCE.GroupBeginExecute.
        private void OnIdle(object sender, EventArgs e)
        {
            if (DateTime.UtcNow < _nextScan)
                return;
            _nextScan = DateTime.UtcNow.AddSeconds(1);
            Scan();
        }

        /// <summary>Follows every open Script Editor not followed yet, and adds the tabs,
        /// pages and menu items (<paramref name="withUi"/>). At a run's start only the
        /// following: building tabs inside the editor's start of a run would delay it.</summary>
        public void Scan() => Scan(withUi: true);

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            var name = e.LoadedAssembly.GetName().Name;
            if (name != "RhinoCodeEditor" && name != "RhinoCodePlatform.Rhino3D")
                return;
            RhinoApp.InvokeOnUiThread(new Action(() => Scan(withUi: false))); // run events, FreePause; the UI at the next idle
        }

        private void Scan(bool withUi)
        {
            if (_disposed)
                return;
            try
            {
                FreePause.TryInstall();
                WatchExpressions.TryInstall(this);
                var editorType = EditorType();
                if (editorType == null)
                    return; // the Script Editor hasn't been opened in this Rhino yet
                if (!_reportedEditorType)
                {
                    _reportedEditorType = true;
                    Log.Info(LogArea.Scan, "the Script Editor is loaded; looking for its windows.");
                }
                SubscribeToRunEvents(editorType);
                foreach (var window in Eto.Forms.Application.Instance.Windows)
                {
                    if (editorType.IsInstanceOfType(window))
                    {
                        // A closed editor still listed: never its debug controls' editor again -- a
                        // new editor of the same code (a Grasshopper component's) has them now, and
                        // a pause taken for the closed one's would be stopped as abandoned.
                        if (_closedWindows.TryGetValue(window, out _))
                            continue;
                        if (!_seenWindows.TryGetValue(window, out _))
                        {
                            // each editor window once: which one got the tabs, at what size
                            _seenWindows.Add(window, null);
                            var seen = window;
                            EditorWindows.WhenClosed(seen, () => _closedWindows.AddOrUpdate(seen, null));
                            StoppedRuns.Watch(window); // where a RunPythonScript run ended, if it did
                            Log.Info(LogArea.Scan, "Script Editor window #{0}: '{1}', {2} x {3}{4}", ++_windowCount, window.Title, window.Width, window.Height,
                                withUi ? "" : " (found at a run's start: its tabs come at the next idle moment)");
                        }
                        var controls = DebugControlsOf(window);
                        if (controls != null)
                        {
                            _editors.AddOrUpdate(controls, window);
                            Hook(controls);
                        }
                        if (!withUi)
                            continue;
                        ConsoleTabs.TryAdd(window, this);
                        BrowserPages.TryAdd(window, this);
                        EditorMenus.TryAdd(window, this);
                        EditorViews.FollowSelection(window, this);
                        HoverPreview.TryAdd(window, this);
                        BreakpointMarkers.TryAdd(window, this);
                        PluginSwitch.TryAdd(window);
                        TooltipLinks.TryAdd(window, this);
                        FolderDrop.TryAdd(window);
                        KeepEditorOpen.TryInstall(window);
                        DebugRunButton.TryAdd(window);
                        EditorExamples.TryAdd(window);
                        Threads.StartHeartbeat();
                        HoverValues.RegisterOpenScripts(window); // scripts open before the plug-in was loaded
                        EditorHooks.ReportOnce();
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_reportedScanError)
                {
                    _reportedScanError = true;
                    UserLog.Error("Can't look for Script Editors: {0}", ex.Message);
                }
            }
        }

        private Type EditorType()
        {
            if (_editorType == null)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == EditorAssemblyName);
                _editorType = assembly?.GetType(EditorTypeName);
                if (assembly != null && _editorType == null && !_reportedMissingEditorType)
                {
                    // The Script Editor is loaded, but its window type has another name now (a
                    // Rhino update): without it nothing is followed -- say so, once.
                    _reportedMissingEditorType = true;
                    UserLog.Warning("The Script Editor's window ({0}) isn't there in this Rhino: miniVivisection can't follow the editor -- it needs an update.", EditorTypeName);
                }
            }
            return _editorType;
        }

        private bool _reportedMissingEditorType;

        /// <summary>The editor's current state's debug controls: <c>RCE.ActiveState</c>
        /// (internal type, public property), then its public <c>DebugControls</c>.</summary>
        internal static IDebugControls DebugControlsOf(object editor)
        {
            if (editor == null)
                return null; // a prompt without an editor (the Grasshopper window)
            var state = editor.GetType().GetProperty("ActiveState", BindingFlags.Public | BindingFlags.Instance)?.GetValue(editor);
            return state?.GetType().GetProperty("DebugControls", BindingFlags.Public | BindingFlags.Instance)?.GetValue(state) as IDebugControls;
        }

        /// <summary>RCE.GroupBeginExecute -- an internal static event with an internal
        /// delegate type (groupId, workspaceId) -- fires when an editor starts a run:
        /// scanning then catches an editor opened a moment ago before its first pause.</summary>
        private void SubscribeToRunEvents(Type editorType)
        {
            const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            if (_runStartEvent == null && editorType.GetEvent("GroupBeginExecute", anyStatic) is EventInfo runStart)
            {
                var onRunStarting = typeof(ScriptEditorHook).GetMethod(nameof(OnRunStarting), BindingFlags.NonPublic | BindingFlags.Instance);
                _runStartHandler = Delegate.CreateDelegate(runStart.EventHandlerType, this, onRunStarting);
                runStart.AddEventHandler(null, _runStartHandler);
                _runStartEvent = runStart;
            }
            if (_runEndEvent == null && editorType.GetEvent("GroupEndExecute", anyStatic) is EventInfo runEnd)
            {
                var onRunEnded = typeof(ScriptEditorHook).GetMethod(nameof(OnRunEnded), BindingFlags.NonPublic | BindingFlags.Instance);
                _runEndHandler = Delegate.CreateDelegate(runEnd.EventHandlerType, this, onRunEnded);
                runEnd.AddEventHandler(null, _runEndHandler);
                _runEndEvent = runEnd;
            }
        }

        /// <summary>RCE.GroupBeginExecute (the editor invokes it on the UI thread): follow
        /// the editor now -- its debugger is about to pause -- and add the UI afterwards.</summary>
        private void OnRunStarting(Guid groupId, Guid workspaceId)
        {
            Log.Debug(LogArea.Runs, "run starting (workspace {0})", workspaceId);
            VisualizerScripts.NewRun();
            BreakpointSpecs.ResetHits();
            Threads.ResetRun();
            BreakHotkey.RunStarted();
            Action follow = () =>
            {
                Scan(withUi: false);
                foreach (var editor in Editors())
                {
                    var access = new ScriptEditorAccess(editor);
                    ScriptKinds.Learn(access.Code?.Uri, access.Language);
                    _asyncRuns.AddOrUpdate(editor, new StrongBox<bool>(ScriptKinds.AsksForAsync(access.ScriptText) == true)); // the freeze guard
                    var title = access.ScriptTitle;
                    RunLogFor(editor).Begin(title);
                    UserLog.Info("Run started: {0}", title);
                    if (access.DebugControls is IDebugControls debugger && debugger.State == DebugControlsState.Debugging)
                        Log.Info(LogArea.Runs, "debugger: RhinoCode's tracer ({0}); pause on exceptions: {1}; stop once per line: {2}; Break now: {3}",
                            access.Language, debugger.PauseOnExceptionPolicy, MiniVivisectionSettings.OncePerLine ? "on" : "off", MiniVivisectionSettings.BreakHotkey);
                }
                Eto.Forms.Application.Instance.AsyncInvoke(() => Scan(withUi: true));
                ConsoleTabs.UpdateStatus();
            };
            if (RhinoApp.InvokeRequired)
                RhinoApp.InvokeOnUiThread(follow);
            else
                follow();
        }

        /// <summary>RCE.GroupEndExecute: the run's Terminal output goes into its run log.</summary>
        private void OnRunEnded(Guid groupId, Guid workspaceId)
        {
            Log.Debug(LogArea.Runs, "run ended (workspace {0})", workspaceId);
            BreakHotkey.RunEnded();
            Action record = () =>
            {
                foreach (var editor in Editors())
                {
                    RunLogFor(editor).End(new ScriptEditorAccess(editor).TerminalText);
                    ScriptRunCommand.Reveal(editor, paused: false); // off screen for a run without a pause: back, minimized
                }
                UserLog.Info("Run ended.");
            };
            if (RhinoApp.InvokeRequired)
                RhinoApp.InvokeOnUiThread(record);
            else
                record();
        }

        /// <summary>The followed Script Editors (open or not yet collected).</summary>
        private List<object> Editors()
        {
            var editors = new List<object>();
            lock (_hookedList)
            {
                foreach (var reference in _hookedList)
                {
                    if (reference.TryGetTarget(out var controls) && _editors.TryGetValue(controls, out var editor) && !editors.Contains(editor))
                        editors.Add(editor);
                }
            }
            return editors;
        }

        /// <summary>The followed debuggers paused right now.</summary>
        internal List<IDebugControls> PausedControls() => ControlsIn(DebugControlsState.Paused);

        /// <summary>The followed debuggers in <paramref name="state"/> right now.</summary>
        internal List<IDebugControls> ControlsIn(DebugControlsState state)
        {
            var found = new List<IDebugControls>();
            lock (_hookedList)
            {
                foreach (var reference in _hookedList)
                {
                    if (reference.TryGetTarget(out var controls) && controls.State == state)
                        found.Add(controls);
                }
            }
            return found;
        }

        /// <summary>The run log of <paramref name="editor"/> (Claude's get_run_errors).</summary>
        internal RunLog RunLogFor(object editor) => _runLogs.GetValue(editor, _ => new RunLog());

        private void Hook(IDebugControls controls)
        {
            if (_hooked.TryGetValue(controls, out _))
                return;
            _hooked.Add(controls, null);
            lock (_hookedList)
            {
                _hookedList.RemoveAll(r => !r.TryGetTarget(out _));
                _hookedList.Add(new WeakReference<IDebugControls>(controls));
            }
            controls.Paused += OnPaused;
            controls.Continued += OnContinued;
            controls.Stopped += OnEnded;
            controls.Disconnected += OnEnded;
            controls.Resetted += OnEnded;
            controls.Detached += OnDetached;
            Log.Info(LogArea.Scan, "following a Script Editor's debugger -- its V Watch tab is drawn while paused.");
        }

        private void Unhook(IDebugControls controls)
        {
            controls.Paused -= OnPaused;
            controls.Continued -= OnContinued;
            controls.Stopped -= OnEnded;
            controls.Disconnected -= OnEnded;
            controls.Resetted -= OnEnded;
            controls.Detached -= OnDetached;
            _hooked.Remove(controls);
        }

        private readonly ConditionalWeakTable<Eto.Forms.Window, object> _seenWindows = new ConditionalWeakTable<Eto.Forms.Window, object>();
        private int _windowCount;
        private readonly ConditionalWeakTable<object, StrongBox<bool>> _asyncRuns = new ConditionalWeakTable<object, StrongBox<bool>>();

        /// <summary>The freeze guard's question, at a pause off the UI thread: does the script
        /// itself run on Rhino's UI thread (no <c># async:true</c>)? Then the UI thread stops at
        /// its next traced line while this thread is paused, and Rhino freezes. From the
        /// editor's script at the run's start, else the paused file; only when neither says,
        /// whether the UI thread answers (it can be busy for other reasons: a first run sets
        /// up the editor).</summary>
        private bool RunsOnUiThread(IDebugControls controls, ExecFrame frame)
        {
            // A Grasshopper script component runs inside the solution, on the UI thread, whatever
            // its script says (trusting "# async:true" froze Rhino: GH 6) -- unless miniVivisection
            // moved this debug run to a thread of its own (GrasshopperRuns.SolvingOffUiThread).
            if (GrasshopperRuns.Solving)
                return !GrasshopperRuns.SolvingOffUiThread;
            // However the script asks to run: a UI thread not answering for 2 s can't take the
            // editor's pause (its handlers wait for it) -- a pause now would freeze Rhino.
            if (Threads.UiSilentFor(TimeSpan.FromSeconds(2)))
                return true;
            bool? isAsync = null;
            if (_editors.TryGetValue(controls, out var editor) && _asyncRuns.TryGetValue(editor, out var known))
                isAsync = known.Value;
            isAsync = isAsync ?? ScriptKinds.AsksForAsync(frame.Reference?.Uri);
            if (isAsync.HasValue)
                return !isAsync.Value;
            return Threads.UiBlocked(TimeSpan.FromSeconds(3));
        }

        /// <summary>An error in a Grasshopper script component right after its inputs were
        /// rewired (a Ctrl+drag disconnecting one): not paused at -- the editor would come up in
        /// the middle of the wiring.</summary>
        private static bool RewiringSkips(IDebugControls controls)
        {
            var frame = controls.Results?.CurrentThread?.CurrentFrame;
            return frame != null && frame.Event == ExecEvent.Exception && (!RhinoApp.InvokeRequired || GrasshopperRuns.SolvingOffUiThread) && Rewiring.SkipErrorPause();
        }

        /// <summary>The freeze guard, on the pausing thread before the Paused event (FreePause):
        /// a pause off the UI thread while the UI thread is busy (the script runs on it, not
        /// async) would freeze Rhino -- it doesn't happen, the thread runs on. Said in V Log.</summary>
        private bool FreezeGuardSkips(IDebugControls controls)
        {
            var frame = controls.Results?.CurrentThread?.CurrentFrame;
            if (!RhinoApp.InvokeRequired || !MiniVivisectionSettings.FreezeGuard || frame == null ||
                frame.Event == ExecEvent.Exception || !RunsOnUiThread(controls, frame))
                return false;
            BreakpointConditions.TakeReason(); // this pause's: not the next one's
            Threads.ReportSkipped(frame.Reference?.Uri, frame.Reference?.Position.LineNumber ?? 0, IsPythonPause(controls));
            return true;
        }

        /// <summary>On the paused thread, before the debugger's prompt: the frame is set,
        /// and this thread holds Python's GIL -- watch expressions and Python lists can be
        /// evaluated here.</summary>
        private void OnPaused(IDebugControls controls)
        {
            // Nothing may escape: this runs inside RhinoCode's pause, and an exception
            // here would end the pause before the debugger stops.
            try
            {
                _pausedThreads.AddOrUpdate(controls, new StrongBox<int>(Environment.CurrentManagedThreadId));
                var frame = controls.Results?.CurrentThread?.CurrentFrame;
                Log.Debug(LogArea.Pause, "paused: {0} line {1} ({2})", frame?.Reference?.Uri?.Segments.LastOrDefault(), frame?.Reference?.Position.LineNumber, frame?.Event);
                var reason = BreakpointConditions.TakeReason();
                var python = IsPythonPause(controls);
                if (!python)
                    Threads.ForgetPause(controls); // IronPython 2, C#: no Python 3 thread names or counts
                var thread = python && MiniVivisectionSettings.PluginOn ? Threads.AtPause(controls) : null;
                var ghRun = GrasshopperRuns.PausedRun(); // a Grasshopper script component's run, if it's one
                if (frame != null && frame.Event != ExecEvent.Exception)
                    UserLog.Info("Paused at line {0} of {1}{2}{3}{4}.", frame.Reference?.Position.LineNumber, frame.Reference?.Uri?.Segments.LastOrDefault(),
                        Threads.Describe(thread), reason == null ? "" : " (" + reason + ")", ghRun == null ? "" : " -- Grasshopper: " + ghRun);
                if (_editors.TryGetValue(controls, out var badgeIn))
                {
                    GrasshopperRuns.ShowBadge(badgeIn, MiniVivisectionSettings.PluginOn ? ghRun : null);
                    var hiddenEditor = badgeIn;
                    RhinoApp.InvokeOnUiThread(new Action(() => ScriptRunCommand.Reveal(hiddenEditor, paused: true))); // RunPythonScript's, off screen
                }
                if (frame != null && frame.Event == ExecEvent.Exception && _editors.TryGetValue(controls, out var paused))
                {
                    UserLog.Error("Stopped at an exception, line {0}: {1}: {2}", frame.Reference.Position.LineNumber, frame.Exception.Id, frame.Exception.Message);
                    RunLogFor(paused).AddException(string.Format("{0}: {1} -- line {2} of {3}\n{4}", frame.Exception.Id, frame.Exception.Message,
                        frame.Reference.Position.LineNumber, frame.Reference.Uri?.LocalPath, frame.Exception.Description).TrimEnd());
                }
                ConsoleTabs.UpdateStatus();
                if (!MiniVivisectionSettings.PluginOn)
                {
                    PausedForTools?.Invoke(controls);
                    return; // switched off (PluginSwitch): no visualizers, drawing or editing
                }
                try
                {
                    _editors.TryGetValue(controls, out var shownIn);
                    InlineValues.AtPause(controls, shownIn); // also "was" for the tooltips (ValueHistory)
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.Hover, "inline values: {0}", Log.Describe(ex));
                }
                if (IsPythonPause(controls))
                    VisualizerScripts.AtPause(); // before drawing: the visualizers apply to this pause
                Edits.Update(controls);
                if (_conduit.Enabled)
                    Show(controls, onPausedThread: true);
                if (_editors.TryGetValue(controls, out var editor) && WatchListFor(editor).AutoEdit)
                    AutoEdit(controls, WatchListFor(editor));
                PausedForTools?.Invoke(controls);
            }
            catch (Exception ex)
            {
                UserLog.Warning("Trouble at the pause (the debugger continues normally): {0} ({1} in {2}.{3})", FirstLine(ex), ex.GetType().Name,
                    ex.TargetSite?.DeclaringType?.Name, ex.TargetSite?.Name);
            }
        }

        /// <summary>Auto edit (the V Watch tab): every shown watch whose value is
        /// geometry becomes editable Rhino objects, written back on change; watches no
        /// longer listed stop being edited. On the paused thread.</summary>
        private void AutoEdit(IDebugControls controls, RhinoWatchList list)
        {
            if (!IsPythonPause(controls))
                return; // write-back needs the paused Python frame
            var hiddenHere = Threads.PausedLabel(controls);
            var listed = new HashSet<string>(list.Snapshot.Where(w => w.Visible && !(hiddenHere != null && w.HiddenThreads.Contains(hiddenHere))).Select(w => w.Expression),
                StringComparer.Ordinal);
            foreach (var watch in Edits.Watches(controls).Where(w => !listed.Contains(w)).ToList())
                RhinoApp.InvokeOnUiThread(new Action(() => Edits.Stop(controls, watch)));
            var pausedThread = Threads.PausedLabel(controls);
            foreach (var watch in list.Snapshot.Where(w => w.Visible && !(pausedThread != null && w.HiddenThreads.Contains(pausedThread))))
            {
                if (Edits.IsEditing(controls, watch.Expression) || !TryEvaluate(controls, watch.Expression, out var value, out _))
                    continue;
                if (EditSession.ModeOf(value, watch.Interpretation).StartsWith("readonly"))
                    continue; // drawn, not editable: no message at every pause
                if (WatchGeometry.Drawable(value, watch.Interpretation, true, out _, out _, out _).Count > 0)
                    Edits.Start(controls, watch.Expression, watch.Interpretation);
            }
        }

        /// <summary>The Auto edit setting changed (UI thread, SettingEffects): applied now
        /// when paused; off ends the editing of this run's objects.</summary>
        internal void ApplyAutoEdit(object editor)
        {
            var list = WatchListFor(editor);
            var on = list.AutoEdit;
            var controls = DebugControlsOf(editor);
            if (controls == null || controls.State != DebugControlsState.Paused)
                return;
            if (!on)
            {
                foreach (var watch in Edits.Watches(controls).ToList())
                    Edits.Stop(controls, watch);
                return;
            }
            if (!RunOnPausedThread(controls, () => AutoEdit(controls, list)))
                UserLog.Warning("Auto edit: {0}.", WatchActions.NotReachable);
        }

        // Nothing may escape the debugger's events: RhinoCode raises them inside its own
        // stepping and stopping.
        private void OnContinued(IDebugControls controls)
        {
            try
            {
                ConsoleTabs.UpdateStatus();
                if (_editors.TryGetValue(controls, out var editor))
                {
                    WatchListFor(editor).SetNotPaused("running");
                    InlineValues.Clear(editor);
                    GrasshopperRuns.ShowBadge(editor, null);
                    Threads.NotPaused(controls, "Running. \"Where is everyone?\" shows where each thread is now.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.Pause, "continuing: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Redraws the V Watch list after the Python tab changed the paused
        /// script (on the paused thread).</summary>
        internal void Refresh(IDebugControls controls)
        {
            if (_conduit.Enabled)
                Show(controls, onPausedThread: true);
            if (_editors.TryGetValue(controls, out var editor))
                EditorViews.Refresh(editor, controls); // the editor's Variables/Watch tabs too
        }

        /// <summary>The "V Watch" tab's list of <paramref name="editor"/>, created once.</summary>
        internal RhinoWatchList WatchListFor(object editor)
        {
            return _watchLists.GetValue(editor, key =>
            {
                var list = new RhinoWatchList();
                list.Changed += () => OnRhinoWatchesChanged(key);
                return list;
            });
        }

        /// <summary>"Watch in Rhino", watch('expr'), Claude's add_watch: onto the editor's
        /// V Watch list. False when the editor isn't known.</summary>
        internal bool AddRhinoWatch(IDebugControls controls, string expression, string interpretation = null)
        {
            if (!_editors.TryGetValue(controls, out var editor))
                return false;
            WatchListFor(editor).Add(expression, interpretation);
            return true;
        }

        /// <summary>Interpret As: the interpretations of <paramref name="expression"/>'s value
        /// in the paused frame, the default first -- (id, label); null when not paused or
        /// it can't be evaluated. UI thread; waits for the paused thread.</summary>
        internal List<(string Id, string Label)> InterpretationsOf(object editor, string expression)
        {
            var controls = DebugControlsOf(editor);
            if (controls == null || controls.State != DebugControlsState.Paused || string.IsNullOrWhiteSpace(expression) || !PythonBridge.IsRunning)
                return null;
            try
            {
                return TryOnPausedThread(controls, () => TryEvaluate(controls, expression, out var value, out _) ? PythonBridge.Candidates(value) : null, out var found)
                    ? found
                    : null;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Watch, "interpretations of {0}: {1}", expression, Log.Describe(ex));
                return null;
            }
        }

        /// <summary>The V Watch list changed (UI thread). Python can only be evaluated
        /// on the paused thread: the UI thread when the script runs there; otherwise the
        /// work goes to FreePause's wait loop, which runs on the paused thread. Without
        /// either, plain variable names are drawn now, the rest at the next pause.</summary>
        private void OnRhinoWatchesChanged(object editor)
        {
            var controls = DebugControlsOf(editor);
            if (controls == null || !_conduit.Enabled || controls.State != DebugControlsState.Paused)
                return;
            var list = WatchListFor(editor);
            Action showAndEdit = () =>
            {
                Show(controls, onPausedThread: true);
                if (list.AutoEdit)
                    AutoEdit(controls, list); // a watch added or shown during the pause: editable now
            };
            if (IsPausedThread(controls))
                showAndEdit();
            else if (!FreePause.TryRunOnPausedThread(controls, showAndEdit))
                Show(controls, onPausedThread: false);
        }

        /// <summary>The editor's Watch tab asks for an expression its paused frame doesn't
        /// list (WatchExpressions): the variable RhinoCode itself would make for the
        /// expression's value in the paused Python frame. Python runs on the paused
        /// thread only -- right here when this is it (the frame records its thread),
        /// else through FreePause's wait loop, waited for briefly. False for C#, for
        /// another frame of the call stack, or when the paused thread can't be reached.</summary>
        internal bool TryEvaluateWatch(ExecFrame frame, DebugExpression expression, out ExecVariable variable)
        {
            variable = null;
            var controls = ControlsPausedAt(frame);
            if (controls == null || !ScriptKinds.IsPython3(frame.Reference?.Uri))
                return false; // C# and IronPython 2: the editor's own lookup
            if (frame.ThreadId.Id == Environment.CurrentManagedThreadId)
            {
                variable = PythonBridge.WatchVariable(expression.ExpressionString);
                return variable is object; // not != : ExecVariable's operator calls Equals, which throws on null
            }
            if (DateTime.UtcNow < _watchBusyUntil)
                return false; // the paused thread didn't answer a moment ago: the editor's own lookup, no wait
            var queued = TryOnPausedThread(controls, () => PythonBridge.WatchVariable(expression.ExpressionString), out var found);
            if (!queued)
                _watchBusyUntil = DateTime.UtcNow.AddSeconds(3); // a Watch tab with many expressions waits once, not once each
            variable = found;
            return queued && variable is object;
        }

        private DateTime _watchBusyUntil;

        /// <summary>The followed debug controls whose current frame is <paramref name="frame"/>.</summary>
        private IDebugControls ControlsPausedAt(ExecFrame frame)
        {
            lock (_hookedList)
            {
                foreach (var reference in _hookedList)
                {
                    if (reference.TryGetTarget(out var controls) && ReferenceEquals(controls.Results?.CurrentThread?.CurrentFrame, frame))
                        return controls;
                }
            }
            return null;
        }

        /// <summary>A script on its own thread paused: the editor's Watch tab evaluated its
        /// expressions before Python could be reached (the editor updates its views from
        /// the Paused event, before the pause waits). Now FreePause takes work, so the
        /// tab is asked once more.</summary>
        private void OnFreePauseWaiting(object paused)
        {
            if (!(paused is IDebugControls controls) || !WatchExpressions.Enabled || !RhinoApp.InvokeRequired)
                return;
            if (!controls.Expressions.Any() || !_editors.TryGetValue(controls, out var editor))
                return;
            var frame = controls.Results?.CurrentThread?.CurrentFrame;
            if (frame != null)
                RhinoApp.InvokeOnUiThread(new Action(() => EditorViews.ShowWatch(editor, frame)));
        }

        /// <summary>Whether the paused frame is Python's: editing writes back through the
        /// paused Python frame only.</summary>
        internal static bool IsPythonPause(IDebugControls controls) =>
            BreakpointUi.IsPython(controls.Results?.CurrentThread?.CurrentFrame?.Reference?.Uri, null);

        /// <summary>Whether this is the thread paused for <paramref name="controls"/>: the
        /// paused frame records it (ExecFrame.ThreadId) -- right from the start of the
        /// pause, before this plug-in's own Paused handler may have run.</summary>
        internal bool IsPausedThread(IDebugControls controls)
        {
            var frame = controls.State == DebugControlsState.Paused ? controls.Results?.CurrentThread?.CurrentFrame : null;
            if (frame != null && frame.ThreadId.Id > 0)
                return frame.ThreadId.Id == Environment.CurrentManagedThreadId;
            return _pausedThreads.TryGetValue(controls, out var thread) && thread.Value == Environment.CurrentManagedThreadId;
        }

        /// <summary>Runs <paramref name="work"/> on the paused thread and waits for it
        /// (MiniVivisectionSettings.PausedThreadTimeout): right here when this is it, else through FreePause's wait loop.
        /// False when the paused thread can't be reached or doesn't answer.</summary>
        internal bool TryOnPausedThread<T>(IDebugControls controls, Func<T> work, out T result)
        {
            if (IsPausedThread(controls))
            {
                result = work();
                return true;
            }
            var found = default(T);
            var done = new ManualResetEventSlim();
            var gaveUp = 0;
            var queued = FreePause.TryRunOnPausedThread(controls, () =>
            {
                try
                {
                    if (Volatile.Read(ref gaveUp) == 0) // the caller still waits
                        found = work();
                }
                finally
                {
                    done.Set();
                }
            });
            if (!queued || !done.Wait(MiniVivisectionSettings.PausedThreadTimeout))
            {
                Volatile.Write(ref gaveUp, 1);
                result = default(T);
                return false;
            }
            result = found;
            return true;
        }

        private void OnEnded(IDebugControls controls)
        {
            Log.Debug(LogArea.Runs, "debugger ended");
            try
            {
                EndedForTools?.Invoke(controls);
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.Claude, "after the run: {0}", Log.Describe(ex));
            }
            try
            {
                Edits.End(controls);
                _conduit.Clear(controls);
                ValueHistory.Clear(controls);
                if (_editors.TryGetValue(controls, out var editor))
                {
                    InlineValues.Clear(editor);
                    WatchListFor(editor).SetNotPaused("not running");
                    Threads.NotPaused(controls, "Not running.");
                    RhinoApp.InvokeOnUiThread(new Action(() => RunLogFor(editor).End(new ScriptEditorAccess(editor).TerminalText)));
                }
                ConsoleTabs.UpdateStatus();
                Redraw();
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.Runs, "after the run: {0}", Log.Describe(ex));
            }
        }

        private void OnDetached(IExecTracer tracer)
        {
            if (tracer is IDebugControls controls)
                OnEnded(controls);
        }

        private void Show(IDebugControls controls, bool onPausedThread)
        {
            try
            {
                var report = new List<string>();
                var values = WatchedValues(controls, onPausedThread, report);
                _conduit.Set(controls, values);
                Log.Info(LogArea.Watch, "V Watch: {0}", report.Count == 0 ? "(empty)" : string.Join("; ", report));
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.Watch, "couldn't read the V Watch list: {0}", ex.Message);
                _conduit.Clear(controls);
            }
            Redraw();
        }

        /// <summary>A watch "In every thread": its value in each other thread's frame of the
        /// script (read only), drawn as "name @thread" in a color of its own; the status'
        /// addition. On the paused thread, Python 3 only.</summary>
        private string InOtherThreads(IDebugControls controls, string name, string interpretation, bool onPausedThread, List<WatchedValue> values,
            List<ThreadValue> rows, string[] hidden)
        {
            if (!onPausedThread || !IsPythonPause(controls))
                return "; other threads: Python 3, at a pause";
            List<(string Label, long Ident, bool Here, bool Ok, object Value)> found;
            try
            {
                found = PythonBridge.EvalInThreads(name);
            }
            catch (Exception ex)
            {
                return "; other threads: " + FirstLine(ex);
            }
            var drawn = new List<string>();
            var missing = new List<string>();
            var frame = controls.Results?.CurrentThread?.CurrentFrame;
            foreach (var (label, _, here, ok, value) in found.Where(f => !f.Here))
            {
                var shown = name + " @" + label;
                if (!ok)
                {
                    missing.Add(label);
                    rows.Add(new ThreadValue(label, shown, null, value as string ?? "not there"));
                    continue;
                }
                ExecVariable variable = null;
                try
                {
                    variable = frame == null ? null : VariableOf(frame, shown, value, onPausedThread: true);
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Watch, "{0}: {1}", shown, Log.Describe(ex));
                }
                rows.Add(new ThreadValue(label, shown, variable, variable is null ? Convert.ToString(value) : null));
                if (hidden.Contains(label))
                    continue; // its row stays, its drawing doesn't (the eye on the row)
                var items = WatchGeometry.Drawable(value, interpretation, true, out _, out _, out var problem);
                if (problem != null || items.Count == 0)
                    continue;
                values.Add(new WatchedValue(shown, WatchGeometry.ColorFor(shown), items));
                drawn.Add(label);
            }
            var text = drawn.Count > 0 ? "; also in " + string.Join(", ", drawn) : found.Count(f => !f.Here) == 0 ? "; no other thread running code" : "";
            if (missing.Count > 0)
                text += "; not (yet) in " + string.Join(", ", missing);
            return text;
        }

        /// <summary>What of the V Watch list's values in the paused frame can be drawn,
        /// copied: the script may change them once it continues. <paramref name="report"/>
        /// gets one entry per expression -- drawn, its value's type, or why it has none --
        /// and the tab's Status column shows the same.</summary>
        private List<WatchedValue> WatchedValues(IDebugControls controls, bool onPausedThread, List<string> report)
        {
            var values = new List<WatchedValue>();
            var frame = controls.Results?.CurrentThread?.CurrentFrame;
            if (frame == null || !_editors.TryGetValue(controls, out var editor))
            {
                report.Add("no paused frame");
                return values;
            }
            var list = WatchListFor(editor);
            var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
            var evaluated = new HashSet<string>(StringComparer.Ordinal);
            var details = new Dictionary<string, ExecVariable>(StringComparer.Ordinal);
            var threads = new Dictionary<string, List<ThreadValue>>(StringComparer.Ordinal);
            var pausedThread = Threads.PausedLabel(controls);
            foreach (var watch in list.Snapshot)
            {
                var name = watch.Expression;
                if (statuses.ContainsKey(name))
                    continue;
                if (!watch.Visible)
                {
                    statuses[name] = "hidden";
                    continue;
                }
                string status;
                var everyThread = watch.EachThread ?? Threads.Count(controls) > 1;
                if (!TryEvaluate(frame, new DebugExpression(name), onPausedThread, out var value, out var error))
                {
                    status = error;
                }
                else
                {
                    evaluated.Add(name);
                    details[name] = VariableOf(frame, name, value, onPausedThread);
                    var items = WatchGeometry.Drawable(value, watch.Interpretation, onPausedThread, out var how, out var pythonPending, out var problem);
                    if (problem != null)
                    {
                        status = "visualizer failed: " + problem;
                    }
                    else if (items.Count > 0 && everyThread && watch.HiddenThreads.Contains(pausedThread ?? ""))
                    {
                        status = "hidden in " + pausedThread;
                    }
                    else if (items.Count > 0)
                    {
                        // In every thread: the paused thread's dot says whose value it is.
                        values.Add(new WatchedValue(name, WatchGeometry.ColorFor(name), items, everyThread && pausedThread != null ? name + " @" + pausedThread + " (paused)" : null));
                        status = "drawn" + (how.Length > 0 ? " " + how : "") + (items.Count == 1 ? "" : string.Format(" ({0})", items.Count));
                    }
                    else
                    {
                        status = pythonPending ? "at the next pause" : TypeName(value) + ", nothing to draw";
                    }
                }
                if (everyThread)
                {
                    var rows = new List<ThreadValue>();
                    status += InOtherThreads(controls, watch.Expression, watch.Interpretation, onPausedThread, values, rows, watch.HiddenThreads);
                    threads[name] = rows;
                }
                statuses[name] = status;
                report.Add(name + ": " + status);
            }
            list.SetStatuses(statuses, evaluated, details, threads, pausedThread);
            return values;
        }

        /// <summary>The value's details for the V Watch tab: the variable RhinoCode
        /// makes for it -- value text, type, expandable members -- for Python on the paused
        /// thread; else the editor's own variable (plain names, C#); else null.</summary>
        internal static ExecVariable VariableOf(ExecFrame frame, string name, object value, bool onPausedThread)
        {
            if (onPausedThread && ScriptKinds.IsPython3(frame?.Reference?.Uri))
            {
                try
                {
                    var variable = PythonBridge.VariableFor(name, value);
                    if (variable is object)
                        return variable;
                }
                catch (Exception)
                {
                    // the editor's variable, below
                }
            }
            var bypass = WatchExpressions.Bypass;
            WatchExpressions.Bypass = true;
            try
            {
                return frame.Evaluate(new DebugExpression(name), out var result) ? result.Result as ExecVariable : null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                WatchExpressions.Bypass = bypass; // a caller's bypass stays
            }
        }

        /// <summary>One watch expression's current value. On the paused thread Python
        /// gives it -- the editor's own result was taken at the pause and misses what
        /// the Python tab or an edit changed since. Elsewhere, or for C#, the editor's
        /// result (plain variable names only).</summary>
        internal static bool TryEvaluate(ExecFrame frame, DebugExpression expression, bool onPausedThread, out object value, out string error)
        {
            value = null;
            error = null;
            Exception pythonError = null;
            if (onPausedThread && ScriptKinds.IsPython3(frame?.Reference?.Uri)) // not IronPython 2's frames
            {
                try
                {
                    value = PythonBridge.Eval(expression.ExpressionString);
                    return true;
                }
                catch (Exception ex)
                {
                    pythonError = ex;
                }
            }
            bool found;
            IDebugExpressionResult result = null;
            var bypass = WatchExpressions.Bypass;
            WatchExpressions.Bypass = true; // the frame's own variables, as before
            try
            {
                found = frame != null && frame.Evaluate(expression, out result);
            }
            finally
            {
                WatchExpressions.Bypass = bypass;
            }
            if (found)
            {
                value = result.Result is ExecVariable variable
                    ? (variable.TryGetValue(out object v) ? v : null)
                    : result.Result;
                return true;
            }
            error = pythonError != null ? FirstLine(pythonError) : "at the next pause";
            return false;
        }

        /// <summary><paramref name="expression"/>'s current value in the editor's paused
        /// frame (on the paused thread).</summary>
        internal static bool TryEvaluate(IDebugControls controls, string expression, out object value, out string error)
        {
            var frame = controls.Results?.CurrentThread?.CurrentFrame;
            return TryEvaluate(frame, new DebugExpression(expression), onPausedThread: true, out value, out error);
        }

        /// <summary>Python from a thread that isn't a paused one (the UI thread: the condition
        /// box, Claude's tools, loading). A script paused on another thread holds Python's
        /// GIL -- taking it here would wait until that script continues, and the UI with
        /// it. Then <paramref name="work"/> runs on that paused thread (FreePause's loop),
        /// waited for briefly; false when it can't be reached. Otherwise it runs here.</summary>
        internal bool TryPython<T>(Func<T> work, out T result)
        {
            var heldElsewhere = PausedControls().FirstOrDefault(c => !IsPausedThread(c));
            if (heldElsewhere == null)
            {
                result = work();
                return true;
            }
            return TryOnPausedThread(heldElsewhere, work, out result);
        }

        /// <summary>Runs <paramref name="job"/> on the thread paused for
        /// <paramref name="controls"/>, the one that can run the script's Python: right
        /// away when that is this thread, else through FreePause's wait loop. False when
        /// neither works (the script runs on its own thread and FreePause is off).</summary>
        internal bool RunOnPausedThread(IDebugControls controls, Action job)
        {
            if (IsPausedThread(controls))
            {
                job();
                return true;
            }
            return FreePause.TryRunOnPausedThread(controls, job);
        }

        private void OnSelectionChanged(object sender, Rhino.DocObjects.RhinoObjectSelectionEventArgs e) => QueueSelectionHighlight();

        private void OnDeselectAll(object sender, Rhino.DocObjects.RhinoDeselectAllObjectsEventArgs e) => QueueSelectionHighlight();

        // Selection events come in bursts: one update once they're done.
        private void QueueSelectionHighlight()
        {
            if (_selectionPending || SelectionSync.Busy || !MiniVivisectionSettings.SelectFromRhino || Edits.Count == 0 && !_highlighting)
                return;
            _selectionPending = true;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                _selectionPending = false;
                HighlightSelection();
            }));
        }

        private bool _highlighting;

        /// <summary>Objects being edited (Edit in Rhino, auto edit) selected in Rhino: their
        /// watches are highlighted in the V Watch tab, and their variables selected in
        /// the editor's Variables and Watch tabs.</summary>
        private void HighlightSelection()
        {
            var doc = RhinoDoc.ActiveDoc;
            var ids = doc?.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList() ?? new List<Guid>();
            var targets = Edits.TargetsOf(ids);
            _highlighting = targets.Count > 0;
            foreach (var editor in Editors())
            {
                var controls = DebugControlsOf(editor);
                var mine = targets.Where(t => t.Controls == controls).ToList();
                var expressions = new HashSet<string>(mine.SelectMany(t => new[] { t.Watch, t.Expression }), StringComparer.Ordinal);
                WatchListFor(editor).SetHighlighted(expressions);
                if (mine.Count > 0)
                {
                    EditorViews.Select(editor, mine.Select(t => t.Expression).Concat(mine.Select(t => t.Watch)).Distinct().ToList());
                    var first = mine[0];
                    var bracket = first.Expression.LastIndexOf('[');
                    var index = bracket > 0 && first.Expression.EndsWith("]") && int.TryParse(first.Expression.Substring(bracket + 1, first.Expression.Length - bracket - 2), out var i) ? i : -1;
                    WatchListFor(editor).RequestSelect(first.Watch, index);
                }
            }
        }

        internal static string TypeName(object value)
        {
            if (value == null)
                return "None";
            return PythonBridge.IsPython(value) ? "a Python value" : value.GetType().Name;
        }

        internal static string FirstLine(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            var message = (ex.Message ?? ex.GetType().Name).Trim();
            var newline = message.IndexOf('\n');
            return newline < 0 ? message : message.Substring(0, newline).Trim();
        }

        // Paused runs Rhino's message loop (a GetOption prompt), which processes
        // queued UI-thread work -- from any thread the redraw happens right away.
        private static void Redraw()
        {
            RhinoApp.InvokeOnUiThread(new Action(() => RhinoDoc.ActiveDoc?.Views.Redraw()));
        }
    }
}
