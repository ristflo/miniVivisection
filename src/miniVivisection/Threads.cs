using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>One frame of a thread's stack (innermost first).</summary>
    internal sealed class ThreadFrame
    {
        public ThreadFrame(string file, int line, string function, bool own)
        {
            File = file;
            Line = line;
            Function = function;
            Own = own;
        }

        public string File { get; }

        public int Line { get; }

        public string Function { get; }

        /// <summary>The script's code, not Python's library.</summary>
        public bool Own { get; }

        public string Where => Function + " -- " + Path.GetFileName(File) + ":" + Line;
    }

    /// <summary>A Python thread of the run, at the moment of a snapshot.</summary>
    internal sealed class ThreadRow
    {
        public ThreadRow(long ident, string label, bool isMain, bool isDaemon, string state, List<ThreadFrame> frames)
        {
            Ident = ident;
            Label = label;
            IsMain = isMain;
            IsDaemon = isDaemon;
            State = state;
            Frames = frames;
        }

        public long Ident { get; }

        /// <summary>"main" (Rhino's UI thread), "script" (an async script's thread), or the thread's name.</summary>
        public string Label { get; }

        public bool IsMain { get; }

        public bool IsDaemon { get; }

        /// <summary>paused here, stopped by the debugger, waiting in join(), running, ...</summary>
        public string State { get; }

        public List<ThreadFrame> Frames { get; }

        /// <summary>Its innermost frame of the script, else its innermost frame.</summary>
        public ThreadFrame Location => Frames.FirstOrDefault(f => f.Own) ?? Frames.FirstOrDefault();

        public bool IsWaiting => State.StartsWith("waiting", StringComparison.Ordinal);
    }

    /// <summary>
    /// The threads of a debugged Python script (Python 3 only):
    /// <list type="bullet">
    /// <item>A snapshot of every thread at each pause, taken on the paused thread (the V Threads view).</item>
    /// <item>"Where is everyone?": the same while the script runs (hangs, deadlocks), Python's lock taken from a background thread with a timeout.</item>
    /// <item>Break now: the next traced line pauses, in any thread or a chosen one (BreakpointConditions' ShouldPause postfix).</item>
    /// <item>The freeze guard: a script that isn't <c># async:true</c> runs on Rhino's UI thread; a worker
    /// thread pausing would freeze Rhino (the UI thread stops at its next traced line, the debugger's
    /// lock), so that pause is skipped and explained.</item>
    /// </list>
    /// RhinoCode traces every <c>threading.Thread</c> (trace_begin.py: <c>threading.settrace</c>);
    /// while one thread is paused, the others stop at their next traced line (DebugControls.Trace
    /// holds one lock).
    /// </summary>
    internal static class Threads
    {
        /// <summary>The threads at one moment.</summary>
        internal sealed class Snapshot
        {
            public DateTime Taken { get; set; } = DateTime.Now;

            public bool WhilePaused { get; set; }

            /// <summary>The paused thread's label, or null.</summary>
            public string PausedLabel { get; set; }

            public List<ThreadRow> Rows { get; set; } = new List<ThreadRow>();

            /// <summary>Why there are no rows, or a finding (a possible deadlock).</summary>
            public string Note { get; set; }
        }

        private static readonly ConditionalWeakTable<IDebugControls, Snapshot> s_latest = new ConditionalWeakTable<IDebugControls, Snapshot>();
        private static readonly ConditionalWeakTable<IDebugControls, StrongBox<int>> s_counts = new ConditionalWeakTable<IDebugControls, StrongBox<int>>();
        private static readonly ConditionalWeakTable<IDebugControls, string> s_pausedLabels = new ConditionalWeakTable<IDebugControls, string>();
        private static readonly object s_breakLock = new object();
        private static IDebugControls s_breakControls;
        private static long s_breakIdent; // 0: any thread
        private static Uri s_breakScript; // only there (Options: "Break now stops in the script only"); null: anywhere

        /// <summary>The script file a debug run runs (its editor's code), for Break now in the
        /// script only; set by ScriptEditorHook.</summary>
        public static Func<IDebugControls, Uri> ScriptOf { get; set; }
        private static readonly HashSet<string> s_skippedLines = new HashSet<string>(StringComparer.Ordinal);
        private static long s_lastBeat;
        private static Eto.Forms.UITimer s_heartbeat;

        /// <summary>A new snapshot for these debug controls (any thread).</summary>
        public static event Action<IDebugControls> Changed;

        public static Snapshot Latest(IDebugControls controls) =>
            controls != null && s_latest.TryGetValue(controls, out var snapshot) ? snapshot : null;

        private static void Store(IDebugControls controls, Snapshot snapshot)
        {
            s_latest.AddOrUpdate(controls, snapshot);
            try
            {
                Changed?.Invoke(controls);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Pause, "threads: {0}", Log.Describe(ex));
            }
        }

        /// <summary>A run started: no break request, no skipped lines from the last run.</summary>
        public static void ResetRun()
        {
            lock (s_breakLock)
            {
                s_breakControls = null;
                s_breakIdent = 0;
            }
            lock (s_skippedLines)
                s_skippedLines.Clear();
            s_counts.Clear(); // the debugger object stays from run to run: no counts or names of the last run
            s_pausedLabels.Clear();
        }

        /// <summary>A pause that isn't Python 3's (IronPython 2, C#): no thread count or
        /// name -- not the last Python 3 pause's either.</summary>
        public static void ForgetPause(IDebugControls controls)
        {
            if (controls == null)
                return;
            s_counts.Remove(controls);
            s_pausedLabels.Remove(controls);
        }

        // --- at a pause ---------------------------------------------------------------------

        /// <summary>At a Python 3 pause, on the paused thread: the paused thread's label and
        /// how many threads run code, for the pause's log line; the snapshot for the V Threads
        /// view. Null when Python can't say.</summary>
        public static (string Label, int Count)? AtPause(IDebugControls controls)
        {
            try
            {
                var (label, _, count) = PythonBridge.ThreadInfo();
                s_counts.AddOrUpdate(controls, new StrongBox<int>(count));
                s_pausedLabels.AddOrUpdate(controls, label);
                if (MiniVivisectionSettings.PluginOn && (MiniVivisectionSettings.TabThreads || MiniVivisectionSettings.PageThreads))
                    Store(controls, new Snapshot { WhilePaused = true, PausedLabel = label, Rows = PythonBridge.Threads(pausedHere: true) });
                return (label, count);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Pause, "threads at the pause: {0}", Log.Describe(ex));
                return null;
            }
        }

        /// <summary>The paused thread's label at the last pause ("main", "script", "Worker-2"), or null.</summary>
        public static string PausedLabel(IDebugControls controls) =>
            controls != null && s_pausedLabels.TryGetValue(controls, out var label) ? label : null;

        /// <summary>How many Python threads ran code at the last pause (1 when unknown):
        /// watches are drawn in every thread automatically when more than one.</summary>
        public static int Count(IDebugControls controls) =>
            controls != null && s_counts.TryGetValue(controls, out var count) ? count.Value : 1;

        /// <summary>" in thread Worker-2 (3 threads)" for the pause's log line, when the
        /// script runs more than one thread or isn't on the main thread; else "".</summary>
        public static string Describe((string Label, int Count)? info)
        {
            if (info == null)
                return "";
            var (label, count) = info.Value;
            if (count <= 1 && (label == "main" || label == "script"))
                return "";
            return string.Format(" in thread {0} ({1} threads)", label, count);
        }

        /// <summary>The script continued or ended: the last snapshot no longer holds.</summary>
        public static void NotPaused(IDebugControls controls, string note)
        {
            var last = Latest(controls);
            if (last == null || !last.WhilePaused)
                return;
            Store(controls, new Snapshot { Note = note });
        }

        // --- "Where is everyone?" -----------------------------------------------------------

        /// <summary>A snapshot now: on the paused thread while paused, else from a
        /// background thread that waits at most <paramref name="timeout"/> for Python's lock.</summary>
        public static async Task<Snapshot> TakeAsync(IDebugControls controls, ScriptEditorHook hook, TimeSpan timeout)
        {
            if (!PythonBridge.IsRunning)
                return Keep(controls, new Snapshot { Note = "Python isn't loaded yet." });
            if (controls != null && controls.State == DebugControlsState.Paused)
            {
                if (!ScriptEditorHook.IsPythonPause(controls))
                    return Keep(controls, new Snapshot { Note = "The paused script isn't Python 3." });
                var done = new TaskCompletionSource<Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                var reached = hook.RunOnPausedThread(controls, () =>
                {
                    try
                    {
                        var (label, _, _) = PythonBridge.ThreadInfo();
                        done.TrySetResult(new Snapshot { WhilePaused = true, PausedLabel = label, Rows = PythonBridge.Threads(pausedHere: true) });
                    }
                    catch (Exception ex)
                    {
                        done.TrySetResult(new Snapshot { Note = ScriptEditorHook.FirstLine(ex) });
                    }
                });
                if (!reached)
                    return Keep(controls, new Snapshot { Note = WatchActions.NotReachable });
                var finished = await Task.WhenAny(done.Task, Task.Delay(timeout)).ConfigureAwait(false);
                return Keep(controls, finished == done.Task ? done.Task.Result : new Snapshot { Note = "The paused thread didn't answer in time." });
            }
            var running = Task.Run(() =>
            {
                try
                {
                    return new Snapshot { Rows = PythonBridge.Threads(pausedHere: false) };
                }
                catch (Exception ex)
                {
                    return new Snapshot { Note = ScriptEditorHook.FirstLine(ex) };
                }
            });
            var first = await Task.WhenAny(running, Task.Delay(timeout)).ConfigureAwait(false);
            if (first != running)
                return Keep(controls, new Snapshot { Note = "Python is busy: a thread holds Python's lock (a long call into native code). Try again." });
            var snapshot = running.Result;
            if (snapshot.Rows.Count == 0 && snapshot.Note == null)
                snapshot.Note = "No Python thread is running code.";
            else if (snapshot.Rows.Count > 0 && snapshot.Rows.All(r => r.IsWaiting))
                snapshot.Note = "Every thread is waiting -- a deadlock? " +
                    string.Join("; ", snapshot.Rows.Select(r => r.Label + " at " + (r.Location?.Where ?? "?") + " (" + r.State + ")"));
            return Keep(controls, snapshot);
        }

        private static Snapshot Keep(IDebugControls controls, Snapshot snapshot)
        {
            if (controls != null)
                Store(controls, snapshot);
            return snapshot;
        }

        // --- Break now -------------------------------------------------------------------

        /// <summary>Pause <paramref name="controls"/>' script at its next traced line, in
        /// the thread <paramref name="ident"/> (Python's thread ident), or any thread (0).
        /// Needs a debug run: a plain run isn't traced.</summary>
        public static void RequestBreak(IDebugControls controls, long ident)
        {
            Uri script = null;
            if (MiniVivisectionSettings.BreakInScript)
            {
                try
                {
                    script = ScriptOf?.Invoke(controls);
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Pause, "Break now, the script's file: {0}", Log.Describe(ex));
                }
            }
            lock (s_breakLock)
            {
                s_breakControls = controls;
                s_breakIdent = ident;
                s_breakScript = script;
            }
            if (script != null)
                Log.Debug(LogArea.Pause, "Break now: at the next line of {0}", script);
            // Continue without an active breakpoint switches tracing off (DebugControls.CanTrace:
            // the tracer then returns at once, for speed) -- no line would come to break at.
            // On again: the script is traced from its next line.
            if (controls is DebugControls debug && !debug.CanTrace && debug.State == DebugControlsState.Debugging)
            {
                try
                {
                    s_canTrace.SetValue(debug, true);
                    Log.Debug(LogArea.Pause, "Break now: tracing switched on again");
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.Pause, "Break now can't switch tracing on: {0}", Log.Describe(ex));
                }
            }
        }

        private static readonly System.Reflection.PropertyInfo s_canTrace = typeof(DebugControls).GetProperty(nameof(DebugControls.CanTrace));

        public static bool BreakRequested
        {
            get
            {
                lock (s_breakLock)
                    return s_breakControls != null;
            }
        }

        /// <summary>For NextStep.ShouldPause (on the traced thread): whether this line is
        /// where a requested break happens; the request is taken then.</summary>
        internal static bool TakeBreak(IDebugControls controls, ExecFrame frame)
        {
            lock (s_breakLock)
            {
                if (s_breakControls == null || !ReferenceEquals(s_breakControls, controls))
                    return false;
                if (s_breakIdent != 0 && !(frame.ThreadId is ExecThreadIdNative native && native.NativeThreadId == s_breakIdent))
                    return false;
                if (s_breakScript != null && !SameFile(frame.Reference?.Uri, s_breakScript))
                    return false; // in a module: on until the script's own next line
                s_breakControls = null;
                s_breakIdent = 0;
                s_breakScript = null;
                return true;
            }
        }

        private static bool SameFile(Uri a, Uri b) =>
            a != null && (a == b || (a.IsFile && b.IsFile && string.Equals(a.LocalPath, b.LocalPath, StringComparison.OrdinalIgnoreCase)));

        // --- the freeze guard -------------------------------------------------------------

        /// <summary>The UI heartbeat, for the freeze guard (UI thread, once).</summary>
        public static void StartHeartbeat()
        {
            if (s_heartbeat != null)
                return;
            Interlocked.Exchange(ref s_lastBeat, Stopwatch.GetTimestamp());
            s_heartbeat = new Eto.Forms.UITimer { Interval = 0.2 };
            s_heartbeat.Elapsed += (s, e) => Interlocked.Exchange(ref s_lastBeat, Stopwatch.GetTimestamp());
            s_heartbeat.Start();
        }

        /// <summary>Whether the UI thread's heartbeat is older than <paramref name="age"/> (no
        /// heartbeat yet: false). Doesn't wait.</summary>
        /// <summary>Whether a Grasshopper script component runs now (GrasshopperRuns sets it).</summary>
        public static Func<bool> InGrasshopperRun { get; set; } = () => false;

        public static bool UiSilentFor(TimeSpan age) =>
            s_heartbeat != null && (Stopwatch.GetTimestamp() - Interlocked.Read(ref s_lastBeat)) / (double)Stopwatch.Frequency > age.TotalSeconds;

        /// <summary>Whether Rhino's UI thread is busy or blocked (not answering): it runs the
        /// script itself, or another thread's pause blocks it. From a thread other than the
        /// UI thread. Unknown (no heartbeat yet): false.</summary>
        public static bool UiBlocked(TimeSpan wait)
        {
            if (s_heartbeat == null)
                return false;
            var age = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - Interlocked.Read(ref s_lastBeat)) / (double)Stopwatch.Frequency);
            if (age > TimeSpan.FromSeconds(1))
                return true;
            var answered = new ManualResetEventSlim(); // not disposed: the UI may answer after the wait
            RhinoApp.InvokeOnUiThread(new Action(() => answered.Set()));
            return !answered.Wait(wait);
        }

        /// <summary>A worker thread's pause skipped by the freeze guard: said once per line
        /// and run, in V Log and the script's Terminal. On the paused thread.</summary>
        public static void ReportSkipped(Uri uri, int line, bool python)
        {
            var key = (uri?.ToString() ?? "") + "|" + line;
            lock (s_skippedLines)
            {
                if (!s_skippedLines.Add(key))
                    return;
            }
            string label = null;
            if (python)
            {
                try
                {
                    label = PythonBridge.ThreadInfo().Label;
                }
                catch (Exception)
                {
                    // no name then
                }
            }
            var message = InGrasshopperRun()
                ? string.Format(
                    "miniVivisection: the breakpoint at line {0} wasn't paused in thread {1}: a Grasshopper component runs on Rhino's UI thread, " +
                    "which would freeze while another thread is paused. To debug its threads, add '# async:true' to the script (the line after '#! python 3'): " +
                    "miniVivisection then runs its debug runs on a thread of their own.",
                    line, label ?? "(a worker)")
                : string.Format(
                    "miniVivisection: the breakpoint at line {0} wasn't paused in thread {1}: this script runs on Rhino's UI thread, which would freeze " +
                    "while another thread is paused. To debug its threads, add '# async:true' to the script (the line after '#! python 3').",
                    line, label ?? "(a worker)");
            UserLog.Warning("{0}", message.Substring("miniVivisection: ".Length));
            if (python)
            {
                // Not from this thread: the Terminal's stream waits for the UI thread, which is
                // busy in the script (a join) -- printing here froze Rhino. On the UI thread, when
                // it's free again.
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    try
                    {
                        PythonBridge.Print(message);
                    }
                    catch (Exception)
                    {
                        // the log has it
                    }
                }));
            }
        }
    }
}
