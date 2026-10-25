using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Eto.Forms;
using Rhino;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// RunPythonScript without the debugger, and the Script Editor afterwards. With the option
    /// "RunPythonScript command" = Stop and record, a Python 3 script runs as Rhino runs it --
    /// plainly, at full speed, no editor -- and:
    /// - Ctrl+Alt+B (Break now's hotkey) stops it: from a thread of its own, with Python's
    ///   lock (the script hands it over between bytecodes), it reads where the script is --
    ///   the thread with the script file on its stack: file, line, function, the stack, the
    ///   frame's values -- and raises KeyboardInterrupt there, which ends the run;
    /// - an error that ends the run is recorded too (RhinoCode's ExecuteException: its line,
    ///   or the script's last line in the traceback).
    /// What happened goes to the command line and V Log, and to a file (last_stop.json, next
    /// to the logs). The next time a Script Editor window shows -- opened, or brought to the
    /// front -- within two hours, it opens the script, sets a breakpoint at the line, goes
    /// there, and shows an overlay in the code view: what happened, why it ended there, and
    /// to run it again with Debug to stop at that line and look. Once per record.
    /// RunPythonScript's own IronPython engine (scripts without <c>#! python 3</c>) isn't
    /// covered. Hooks: ScriptRunCommand (RunPythonScriptCommand.RunPython3, and
    /// RhinoCode.RunScriptAsyncMaybe for the run's Task), BreakHotkey, ScriptEditorHook.
    /// </summary>
    internal static partial class StoppedRuns
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly TimeSpan ShowWithin = TimeSpan.FromHours(2);
        private static readonly object s_lock = new object();
        private static Run s_run;

        [ThreadStatic]
        private static Stack<Run> t_starting; // RunPython3 runs (nested: a script running RunPythonScript): the innermost's Task comes first

        /// <summary>A RunPythonScript run is going on that Break now can stop.</summary>
        public static bool Active
        {
            get
            {
                lock (s_lock)
                    return s_run != null;
            }
        }

        // --- the run -------------------------------------------------------------------------

        /// <summary>RunPython3 starts <paramref name="uri"/> plainly (the UI thread): followed from now on.</summary>
        public static Run Begin(Uri uri, string title)
        {
            // On the UI thread: a script that isn't async runs on it, so that's its thread (an
            // async one's isn't known: Capture sets 0).
            var run = new Run { Path = uri != null && uri.IsFile ? uri.LocalPath : null, Title = title, Ident = PythonBridge.CurrentThreadIdent() };
            lock (s_lock)
                s_run = run;
            (t_starting ?? (t_starting = new Stack<Run>())).Push(run);
            BreakHotkey.RunStarted();
            return run;
        }

        /// <summary>RhinoCode.RunScriptAsyncMaybe's Task, while RunPython3 runs (its postfix):
        /// done now for a script on the UI thread, later for an async one.</summary>
        public static void Capture(Task task)
        {
            if (t_starting == null || t_starting.Count == 0 || task == null)
                return;
            var run = t_starting.Pop();
            if (task.IsCompleted)
            {
                Finish(run, task);
            }
            else
            {
                run.Ident = 0; // async: on a thread of its own, found by its file
                task.ContinueWith(t => RhinoApp.InvokeOnUiThread(new Action(() => Finish(run, t))), TaskScheduler.Default);
            }
        }

        /// <summary>RunPython3 is done: a run whose Task wasn't seen (it failed before) ends here.</summary>
        public static void AfterRunPython3(Run run)
        {
            if (run == null || t_starting == null || !t_starting.Contains(run))
                return; // its Task was seen (Capture)
            // Its Task wasn't seen (it failed before): off the stack, and over.
            var kept = t_starting.Where(r => r != run).Reverse().ToList();
            t_starting.Clear();
            foreach (var r in kept)
                t_starting.Push(r);
            Finish(run, null);
        }

        /// <summary>Break now's hotkey during such a run (its own thread): where the script is,
        /// and stop it there.</summary>
        public static void Stop()
        {
            Run run;
            var attempt = new StopAttempt();
            lock (s_lock)
            {
                run = s_run;
                if (run == null || run.Stopping)
                    return;
                run.Stopping = true;
            }
            Task.Run(async () =>
            {
                // Python's lock may come only after a long native call: by then this press may
                // have been given up (the user told so, and may press again), or the run be over.
                bool StillWanted()
                {
                    lock (s_lock)
                        return !attempt.Abandoned && ReferenceEquals(s_run, run);
                }
                void Found(Where where)
                {
                    lock (s_lock)
                        run.Stopped = where; // before the interrupt: the run's end finds it
                }
                var stopping = Task.Run(() => PythonBridge.StopScript(run.Path ?? run.Title ?? "", true, run.Ident, StillWanted, Found));
                var first = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
                string problem = null;
                if (first != stopping)
                {
                    attempt.Abandoned = true;
                    problem = "Python is busy in a long call into native code -- press it again in a moment.";
                }
                else if (stopping.IsFaulted)
                {
                    problem = ScriptEditorHook.FirstLine(stopping.Exception?.InnerException ?? stopping.Exception);
                }
                else if (stopping.Result == null)
                {
                    problem = "no thread is running the script's code.";
                }
                if (problem != null)
                {
                    lock (s_lock)
                    {
                        if (first == stopping)
                            run.Stopped = null;
                    }
                    run.Stopping = false;
                    RhinoApp.InvokeOnUiThread(new Action(() => RhinoApp.WriteLine("miniVivisection: couldn't stop {0}: {1}", run.Title, problem)));
                    UserLog.Warning("Break now: couldn't stop {0}: {1}", run.Title, problem);
                    return;
                }
                Log.Info(LogArea.Runs, "RunPythonScript: {0} stopped at line {1} ({2})", run.Title, stopping.Result.Line, stopping.Result.Function);
            });
        }

        private static void Finish(Run run, Task task)
        {
            lock (s_lock)
            {
                if (s_run == run)
                    s_run = null;
            }
            BreakHotkey.RunEnded();
            try
            {
                var error = task?.Exception?.InnerException ?? task?.Exception;
                Where stopped;
                lock (s_lock)
                    stopped = run.Stopped;
                Record record;
                if (stopped != null)
                    record = FromStop(run, stopped);
                else if (error != null && !(error is OperationCanceledException))
                    record = FromError(run, error);
                else
                    return; // finished: nothing to show
                Save(record);
                var where = record.Function != null && record.Function != "<module>" ? " (in " + record.Function + ")" : "";
                var what = record.Kind == "stopped"
                    ? string.Format("{0} stopped at line {1}{2}.", record.Title, record.Line, where)
                    : string.Format("{0} ended with an error at line {1}{2}: {3}", record.Title, record.Line, where, record.Error);
                RhinoApp.WriteLine("miniVivisection: " + what + " Open the Script Editor: it shows where and why.");
                UserLog.Info("RunPythonScript: " + what);
                // An editor on screen now: there at once.
                foreach (var window in s_watched.Select(p => p.Key).Where(w => w.Visible && w.WindowState != WindowState.Minimized && !ScriptRunCommand.IsKeptInvisible(w)).ToList())
                    OfferIn(window);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Runs, "recording where the script ended: {0}", Log.Describe(ex));
            }
        }

        // --- the Script Editor -----------------------------------------------------------------

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> s_watched =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Window, object>();

        /// <summary>A Script Editor window seen (ScriptEditorHook): a waiting record shows in
        /// it once it's set up, or when it comes to the front later.</summary>
        public static void Watch(Window window)
        {
            if (window == null || s_watched.TryGetValue(window, out _))
                return;
            s_watched.Add(window, null);
            window.GotFocus += (s, e) =>
            {
                if (!ScriptRunCommand.IsKeptInvisible(window))
                    OfferIn(window);
            };
            var timer = new UITimer { Interval = 1.5 }; // its code view and tabs first
            timer.Elapsed += (s, e) =>
            {
                timer.Stop();
                if (window.Visible && window.WindowState != WindowState.Minimized && !ScriptRunCommand.IsKeptInvisible(window))
                    OfferIn(window);
            };
            timer.Start();
        }

        /// <summary>A waiting record (recent, not shown yet, its file there): shown in <paramref name="editor"/>.</summary>
        public static void OfferIn(object editor)
        {
            try
            {
                if (!MiniVivisectionSettings.PluginOn || IsEmbedded(editor))
                    return; // a Grasshopper component's editor holds that component's script
                var record = Load();
                if (record == null || record.Shown || DateTime.Now - record.Time > ShowWithin || record.Line <= 0 ||
                    string.IsNullOrEmpty(record.File) || !File.Exists(ToPath(record.File)))
                    return;
                record.File = ToPath(record.File);
                record.Shown = true;
                Save(record); // once
                Show(editor, record);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Runs, "showing where the script ended: {0}", Log.Describe(ex));
            }
        }

        private static readonly List<(IDebugControls Controls, DebugBreakpoint Breakpoint, Uri Script)> s_autoBreakpoints =
            new List<(IDebugControls, DebugBreakpoint, Uri)>();

        /// <summary>The breakpoints set here, on <paramref name="controls"/> -- every one, for
        /// null -- removed: they were for one run (its debugger disconnected at its end).</summary>
        public static void RemoveAutoBreakpoints(IDebugControls controls)
        {
            List<(IDebugControls Controls, DebugBreakpoint Breakpoint, Uri Script)> done;
            lock (s_autoBreakpoints)
            {
                done = s_autoBreakpoints.Where(a => controls == null || ReferenceEquals(a.Controls, controls)).ToList();
                foreach (var item in done)
                    s_autoBreakpoints.Remove(item);
            }
            List<(IDebugControls Controls, Uri Script, int Line)> conditions;
            lock (s_autoConditions)
            {
                conditions = s_autoConditions.Where(a => controls == null || ReferenceEquals(a.Controls, controls)).ToList();
                foreach (var item in conditions)
                    s_autoConditions.Remove(item);
            }
            foreach (var item in conditions)
                BreakpointSpecs.EndOneShot(item.Script, item.Line); // the line's own spec back
            lock (s_disconnectHandlers)
            {
                foreach (var (debugger, handler) in s_disconnectHandlers.Where(h => controls == null || ReferenceEquals(h.Debugger, controls)).ToList())
                {
                    debugger.Disconnected -= handler;
                    s_disconnectHandlers.Remove((debugger, handler));
                }
            }
            foreach (var item in done)
            {
                try
                {
                    item.Controls.Breakpoints.Remove(item.Breakpoint);
                    Log.Info(LogArea.Runs, "the breakpoint set after the stop is removed ({0}, line {1})", item.Script, item.Breakpoint is CodeReferenceBreakpoint r ? r.Reference.Position.LineNumber : 0);
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Runs, "removing the breakpoint set after the stop: {0}", Log.Describe(ex));
                }
            }
        }

        private static bool IsEmbedded(object editor)
        {
            var options = editor.GetType().GetProperty("LaunchOptions", Any)?.GetValue(editor);
            return options?.GetType().GetProperty("Embedded", Any)?.GetValue(options) as bool? == true;
        }

        private static void Show(object editor, Record record)
        {
            // The script: its tab if open, else opened (the editor's own AddCode makes it active).
            var state = editor.GetType().GetProperty("ActiveState", Any)?.GetValue(editor)
                ?? throw new MissingMemberException(editor.GetType().FullName, "ActiveState");
            var code = (state.GetType().GetProperty("Codes", Any)?.GetValue(state) as System.Collections.IEnumerable)?
                .Cast<object>()
                .Select(c => c.GetType().GetProperty("Code", Any)?.GetValue(c) as Code)
                .FirstOrDefault(c => c?.Uri != null && c.Uri.IsFile && SamePath(c.Uri.LocalPath, record.File))
                ?? RhinoCode.CreateCode(new Uri(record.File));
            var addCode = state.GetType().GetMethods(Any).First(m => m.Name == "AddCode" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(Code));
            var options = Activator.CreateInstance(addCode.GetParameters()[1].ParameterType, true);
            addCode.Invoke(state, new[] { code, options });
            // No breakpoint is set: the overlay says how to stop there again.
            UserLog.Info("{0}: {1} -- shown in the Script Editor.", record.Title,
                record.Kind == "stopped" ? "stopped by Break now at line " + record.Line : "error at line " + record.Line + ": " + record.Error);
            if (!string.IsNullOrEmpty(record.Traceback))
                Log.Info(LogArea.Runs, "the error's traceback:\n{0}", record.Traceback);
            ShowOverlay(editor, code, record);
        }

        /// <summary>A breakpoint at <paramref name="line"/> of <paramref name="code"/> for the
        /// next debug run only -- removed when its editor's debugger next disconnects (the run's
        /// end), and before another of these is set; with <paramref name="condition"/>, a
        /// conditional one (BreakpointSpecs, removed with it).</summary>
        internal static void AddOneShotBreakpoint(Code code, int line, string condition)
        {
            RemoveAutoBreakpoints(null);
            var controls = code?.DebugControls;
            if (controls == null || line <= 0)
                return;
            if (!controls.Breakpoints.WhereReferences(code).Any(b => b.Reference.Position.LineNumber == line))
            {
                var breakpoint = new CodeReferenceBreakpoint(code, line);
                controls.Breakpoints.Add(breakpoint);
                lock (s_autoBreakpoints)
                    s_autoBreakpoints.Add((controls, breakpoint, code.Uri));
            }
            if (condition != null)
            {
                BreakpointSpecs.SetOneShot(code.Uri, line, condition); // the user's own spec there is set aside, not lost
                lock (s_autoConditions)
                    s_autoConditions.Add((controls, code.Uri, line));
            }
            // One run: the next debug run of this editor's debugger ends -> gone.
            if (controls is DebugControls debugger)
            {
                DebugControlsEventHandler ended = null;
                ended = c =>
                {
                    debugger.Disconnected -= ended;
                    RhinoApp.InvokeOnUiThread(new Action(() => RemoveAutoBreakpoints(controls)));
                };
                debugger.Disconnected += ended;
                lock (s_disconnectHandlers)
                    s_disconnectHandlers.Add((debugger, ended)); // one per debugger: replaced by the next one-shot
            }
        }

        private static readonly List<(IDebugControls Controls, Uri Script, int Line)> s_autoConditions = new List<(IDebugControls, Uri, int)>();
        private static readonly List<(DebugControls Debugger, DebugControlsEventHandler Handler)> s_disconnectHandlers = new List<(DebugControls, DebugControlsEventHandler)>();

        /// <summary>The explanation overlay in <paramref name="editor"/>'s code view, once it
        /// shows <paramref name="code"/> (it loads asynchronously; up to 10 s).</summary>
        internal static void ShowOverlay(object editor, Code code, Record record)
        {
            var tries = 0;
            var timer = new UITimer { Interval = 0.4 };
            timer.Elapsed += async (s, e) =>
            {
                if (++tries > 25)
                {
                    timer.Stop();
                    Log.Info(LogArea.Runs, "stop overlay: the code view didn't show {0} within 10 s", record.File);
                    return;
                }
                var access = new ScriptEditorAccess(editor);
                if (!(access.Monaco is WebView web) || access.Code?.Uri != code.Uri)
                    return;
                timer.Stop();
                try
                {
                    // A function body (its returns): run as one, as the other code-view scripts.
                    var result = await HoverPreview.Script(web, OverlayScript(record));
                    if (result == "no Monaco editor found" && tries <= 25)
                    {
                        // A tab just opened in an editor already open: its code view is there,
                        // the Monaco editor in it not yet -- again at the next tick
                        timer.Start();
                        return;
                    }
                    Log.Info(LogArea.Runs, "stop overlay: {0}", result ?? "(no answer)");
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.Runs, "stop overlay: {0}", Log.Describe(ex));
                }
            };
            timer.Start();
        }

    }
}
