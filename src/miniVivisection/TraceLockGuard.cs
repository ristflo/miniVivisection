using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Rhino;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// RhinoCode's debugger pauses inside one lock that every debugger shares
    /// (<c>DebugControls.s_traceLock</c>, static): <c>Trace</c> holds it while a script waits
    /// at a pause, and <c>Attach</c>/<c>Detach</c> take it too. A script paused on a thread
    /// of its own (<c># async:true</c>) therefore blocks any other debugged code on Rhino's
    /// UI thread -- another editor's Debug, a Grasshopper script component solving in the
    /// debugger -- at its start or its first line, and with the UI thread blocked the paused
    /// script can't be continued: Rhino freezes. Here those three wait for the lock on the UI
    /// thread with Rhino's message loop going (as FreePause does at a pause): the other script
    /// can be continued or stopped, and this one goes on once it has. Patched with Harmony
    /// (prefix takes the lock, finalizer releases it; the original re-enters it).
    /// </summary>
    internal static class TraceLockGuard
    {
        private static object s_lock;
        private static int s_uiThread;
        private static Harmony s_harmony;
        private static string s_error;
        private static int s_waits;

        // How long an ordinary hold (another thread tracing one line) may take before it counts as a pause.
        private const int ShortWait = 200;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_error ?? "not installed";

        /// <summary>How often the UI thread had to wait for another thread's pause (the checks).</summary>
        internal static int Waits => s_waits;

        /// <summary>Patches once; on the UI thread (it records which thread that is).</summary>
        public static void TryInstall()
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                var type = typeof(DebugControls);
                s_lock = type.GetField("s_traceLock", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                    ?? throw new MissingFieldException(type.FullName, "s_traceLock");
                var attach = type.GetMethod(nameof(DebugControls.Attach), new[] { typeof(Guid) })
                    ?? throw new MissingMethodException(type.FullName, nameof(DebugControls.Attach));
                var detach = type.GetMethod(nameof(DebugControls.Detach), Type.EmptyTypes)
                    ?? throw new MissingMethodException(type.FullName, nameof(DebugControls.Detach));
                var trace = type.GetMethod(nameof(DebugControls.Trace), new[] { typeof(ExecFrame) })
                    ?? throw new MissingMethodException(type.FullName, nameof(DebugControls.Trace));
                s_uiThread = Environment.CurrentManagedThreadId;
                var harmony = new Harmony("minivivisection.tracelockguard");
                var prefix = new HarmonyMethod(typeof(TraceLockGuard), nameof(EnterPrefix));
                var finalizer = new HarmonyMethod(typeof(TraceLockGuard), nameof(ExitFinalizer));
                foreach (var method in new[] { attach, detach, trace })
                    harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                s_harmony = harmony;
                Log.Debug(LogArea.Pause, "the debugger's shared lock is taken on the UI thread with Rhino's message loop going");
            }
            catch (Exception ex)
            {
                s_error = ex.Message;
                UserLog.Warning("Can't guard the debugger's shared lock -- a debug run on Rhino's UI thread while another script is paused on its own thread freezes Rhino: {0}", ex.Message);
            }
        }

        /// <summary>For the checks: the thread that counts as the UI thread.</summary>
        internal static void SetUiThreadForChecks(int managedThreadId) => s_uiThread = managedThreadId;

        /// <summary>Waits until the lock is free, keeping the UI going; the checks pass a
        /// stand-in for Rhino's message loop.</summary>
        internal static Action Pump { get; set; } = () => RhinoApp.Wait();

        private static void EnterPrefix(object __instance, MethodBase __originalMethod, out bool __state)
        {
            __state = false;
            var gate = s_lock;
            if (gate == null || Environment.CurrentManagedThreadId != s_uiThread || Monitor.IsEntered(gate))
                return; // another thread, or nested in a pause on the UI thread: as RhinoCode does
            // Attach when attached, Detach when detached: the original returns before the lock
            if (__instance is DebugControls controls && __originalMethod != null)
            {
                var state = controls.TracerState.ToString();
                if ((__originalMethod.Name == nameof(DebugControls.Attach) && state == "Attached") || (__originalMethod.Name == nameof(DebugControls.Detach) && state == "Detached"))
                    return;
            }
            // Another thread tracing a line holds the lock for a moment -- the script's own worker
            // threads do all the time: waited for plainly, as RhinoCode would.
            if (Monitor.TryEnter(gate, ShortWait))
            {
                __state = true;
                return;
            }
            // Held longer: a pause on another thread. Rhino's message loop goes on meanwhile.
            Interlocked.Increment(ref s_waits);
            var since = DateTime.UtcNow;
            var told = false;
            try
            {
                while (!Monitor.TryEnter(gate, 10))
                {
                    Pump();
                    if (!told && DateTime.UtcNow - since > TimeSpan.FromSeconds(1))
                    {
                        told = true;
                        UserLog.Warning("Another script is paused on a thread of its own: this debug run waits until it continues -- Continue or Stop it in its Script Editor.");
                    }
                }
            }
            catch (Exception ex)
            {
                // Pumping failed (Rhino closing): wait plainly, as RhinoCode would.
                Log.Warning(LogArea.Pause, "waiting for the debugger's lock: {0}", Log.Describe(ex));
                Monitor.Enter(gate);
            }
            __state = true;
            Log.Info(LogArea.Pause, "the other script's pause is over: this debug run goes on");
        }

        private static Exception ExitFinalizer(Exception __exception, bool __state)
        {
            if (__state)
                Monitor.Exit(s_lock);
            return __exception;
        }
    }
}
