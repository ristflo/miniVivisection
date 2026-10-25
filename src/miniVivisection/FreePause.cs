using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Rhino;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Keeps Rhino usable while the Script Editor's debugger is paused.
    ///
    /// Rhino's own pause (<c>RhinoCodePlatform.Rhino3D.Rhino3DDebugControls.Pause</c>)
    /// waits in a "Select Action" command-line prompt: typed text goes to that
    /// prompt and objects can't be selected; only toolbar macros get through. The
    /// editor's Debug button runs the script outside any Rhino command, so waiting
    /// without that prompt leaves Rhino at its normal "Command:" prompt.
    ///
    /// Patched with Harmony (RhinoCode 8.36, internal names):
    /// - <c>Pause()</c> waits for the action instead -- keeping Rhino's message loop
    ///   going when the script runs on the UI thread, plainly blocking the script's
    ///   own thread when it runs on another;
    /// - <c>Proceed(action)</c>, which the editor's Continue/Step/Stop buttons call,
    ///   hands over the action instead of answering the prompt.
    /// </summary>
    internal static class FreePause
    {
        private const string PlatformAssemblyName = "RhinoCodePlatform.Rhino3D";
        private const string ControlsTypeName = "RhinoCodePlatform.Rhino3D.Rhino3DDebugControls";

        // Debug controls waiting in WaitForAction -> work for their paused thread.
        // (Nested pauses of the same controls -- through the pumped message loop -- stack up: the
        // innermost takes the work, the outer's queue is back when it ends.)
        private static readonly Dictionary<object, Stack<ConcurrentQueue<Action>>> s_waiting =
            new Dictionary<object, Stack<ConcurrentQueue<Action>>>(ReferenceEqualityComparer.Instance);
        private static FieldInfo s_action;
        private static FieldInfo s_actionLock;
        private static Harmony s_harmony;
        private static string s_error;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_error ?? "not installed";

        /// <summary>How a pause on the UI thread keeps Rhino going (MiniVivisection
        /// Pump=...): Rhino's own wait (<c>RhinoApp.Wait</c>), or an Eto/WPF message-loop
        /// iteration (<c>Dispatcher.PushFrame</c>, which translates key presses into
        /// characters for WPF text boxes but skips Rhino's own message handling).</summary>
        public enum PumpKind
        {
            Rhino,
            Eto,
        }

        /// <summary>Wait without the prompt (MiniVivisection FreePause=On/Off).
        /// Takes effect at the next pause.</summary>
        public static bool Enabled
        {
            get => MiniVivisectionSettings.FreePause;
            set => MiniVivisectionSettings.FreePause = value;
        }

        /// <summary>Switchable while paused (experimental comparison).</summary>
        public static PumpKind Pump
        {
            get => MiniVivisectionSettings.Pump == "Eto" ? PumpKind.Eto : PumpKind.Rhino;
            set => MiniVivisectionSettings.Pump = value.ToString();
        }

        public static bool IsInstalled => s_harmony != null;

        /// <summary>Whether a pause of these debug controls has no way on any more -- its
        /// editor window closed for good (ScriptEditorHook) -- so the wait ends with Stop
        /// instead of waiting for a Continue nobody can press.</summary>
        public static Func<object, bool> Abandoned { get; set; } = _ => false;

        /// <summary>The freeze guard (ScriptEditorHook): whether this pause of the debug controls
        /// must not happen -- asked on the pausing thread before the Paused event, whose handlers
        /// (the Script Editor's) wait for Rhino's UI thread: a UI thread waiting in the script
        /// (a join) never answers, and Rhino froze inside the event.</summary>
        public static Func<object, bool> SkipAtPause { get; set; } = _ => false;

        /// <summary>The paused thread waits here and takes work from now on (raised on
        /// that thread, with the debug controls).</summary>
        public static event Action<object> Waiting;

        /// <summary>Patches the debugger once RhinoCode's Rhino platform is loaded.
        /// Reports a failure once; the original pause stays in place then.</summary>
        public static void TryInstall()
        {
            if (s_harmony != null || s_error != null)
                return;
            var platform = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == PlatformAssemblyName);
            if (platform == null)
                return; // not loaded yet: the next scan tries again
            try
            {
                var controls = platform.GetType(ControlsTypeName, throwOnError: true);
                s_controlsType = controls;
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                s_action = controls.GetField("_action", flags) ?? throw new MissingFieldException(ControlsTypeName, "_action");
                s_actionLock = controls.GetField("_actionLock", flags) ?? throw new MissingFieldException(ControlsTypeName, "_actionLock");
                var pause = controls.GetMethod("Pause", flags, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(ControlsTypeName, "Pause");
                var proceed = controls.GetMethod("Proceed", flags, null, new[] { typeof(DebugAction) }, null) ?? throw new MissingMethodException(ControlsTypeName, "Proceed");

                var harmony = new Harmony("minivivisection.freepause");
                harmony.Patch(pause, prefix: new HarmonyMethod(typeof(FreePause), nameof(PausePrefix)));
                harmony.Patch(proceed, prefix: new HarmonyMethod(typeof(FreePause), nameof(ProceedPrefix)));
                // The freeze guard decides before the Paused event; a skipped pause raises neither
                // it nor Continued (both handled by the editor through the UI thread).
                var onPaused = controls.GetMethod("OnPaused", flags, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(ControlsTypeName, "OnPaused");
                var onContinued = controls.GetMethod("OnContinued", flags, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(ControlsTypeName, "OnContinued");
                // Harmony patches a method as its declaring type has it (here: DebugControls).
                onPaused = onPaused.DeclaringType.GetMethod("OnPaused", flags, null, Type.EmptyTypes, null);
                onContinued = onContinued.DeclaringType.GetMethod("OnContinued", flags, null, Type.EmptyTypes, null);
                harmony.Patch(onPaused, prefix: new HarmonyMethod(typeof(FreePause), nameof(OnPausedPrefix)));
                harmony.Patch(onContinued, prefix: new HarmonyMethod(typeof(FreePause), nameof(OnContinuedPrefix)));
                s_harmony = harmony;
                Log.Info(LogArea.FreePause, "Rhino stays usable while the Script Editor's debugger is paused (FreePause).");
            }
            catch (Exception ex)
            {
                s_error = ex.Message;
                UserLog.Warning("Can't change the debugger's pause, it keeps its Select Action prompt: {0}", ex.Message);
            }
        }

        private static bool PausePrefix(object __instance, ref DebugAction __result)
        {
            if (TakeSkip(__instance))
            {
                __result = DebugAction.Continue; // the freeze guard (Threads): this pause would freeze Rhino
                return false;
            }
            if (!Enabled)
                return true; // the original prompt
            __result = WaitForAction(__instance);
            return false;
        }

        private static readonly HashSet<object> s_skip = new HashSet<object>();

        /// <summary>The coming pause of <paramref name="controls"/> continues at once (the
        /// freeze guard decided in the Paused event, which comes just before it).</summary>
        public static void SkipPause(object controls)
        {
            lock (s_skip)
                s_skip.Add(controls);
        }

        private static bool TakeSkip(object controls)
        {
            lock (s_skip)
                return s_skip.Remove(controls);
        }

        // Skipped pauses whose Continued is still to come (DoContinue, right after Pause).
        private static readonly HashSet<object> s_quiet = new HashSet<object>(ReferenceEqualityComparer.Instance);

        private static Type s_controlsType; // Rhino3DDebugControls: whose Pause is patched

        private static bool OnPausedPrefix(object __instance)
        {
            // OnPaused is the base class's: another kind of debug controls (none in Rhino 8.36)
            // has no patched Pause to take the skip -- its pause stays as it is
            if (s_controlsType != null && !s_controlsType.IsInstanceOfType(__instance))
                return true;
            bool skip;
            try
            {
                skip = SkipAtPause(__instance);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.FreePause, "freeze guard: {0}", Log.Describe(ex));
                return true;
            }
            if (!skip)
                return true;
            SkipPause(__instance);
            lock (s_quiet)
                s_quiet.Add(__instance);
            return false; // no Paused event
        }

        private static bool OnContinuedPrefix(object __instance)
        {
            lock (s_quiet)
                return !s_quiet.Remove(__instance); // a skipped pause: no Continued event either
        }

        private static bool ProceedPrefix(object __instance, DebugAction action)
        {
            Log.Debug(LogArea.FreePause, "proceed: {0}", action);
            lock (s_waiting)
            {
                if (!s_waiting.TryGetValue(__instance, out var stack) || stack.Count == 0)
                {
                    // Not waiting here (yet): the pause may be starting -- Paused already, its
                    // Paused event still being handled -- and with FreePause on it will wait
                    // here: the action is kept for it instead of answering a prompt that
                    // never comes (Continue, Stop or StopPaused pressed in that moment).
                    if (!Enabled || !(__instance is IDebugControls starting) || starting.State != DebugControlsState.Paused)
                        return true; // paused in the original prompt: answer it
                    s_early.Add(__instance);
                }
            }
            lock (s_actionLock.GetValue(__instance))
                s_action.SetValue(__instance, (DebugAction?)action);
            return false;
        }

        /// <summary>Runs <paramref name="job"/> on the thread paused for
        /// <paramref name="controls"/> -- the one holding Python's GIL -- when it waits
        /// here. False when it doesn't (FreePause off, or not paused).</summary>
        public static bool TryRunOnPausedThread(object controls, Action job)
        {
            lock (s_waiting)
            {
                if (!s_waiting.TryGetValue(controls, out var stack) || stack.Count == 0)
                    return false;
                stack.Peek().Enqueue(job);
                return true;
            }
        }

        private static void RunJob(Action job)
        {
            try
            {
                job();
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.FreePause, "work on the paused thread failed: {0}", Log.Describe(ex));
            }
        }

        // Debug controls given their action while their pause was starting (ProceedPrefix).
        private static readonly HashSet<object> s_early = new HashSet<object>(ReferenceEqualityComparer.Instance);

        private static DebugAction WaitForAction(object controls)
        {
            var work = new ConcurrentQueue<Action>();
            lock (s_waiting)
            {
                // An action given while this pause was starting is this pause's; any other is stale.
                if (!s_early.Remove(controls))
                {
                    lock (s_actionLock.GetValue(controls))
                        s_action.SetValue(controls, null);
                }
                if (!s_waiting.TryGetValue(controls, out var stack))
                    s_waiting[controls] = stack = new Stack<ConcurrentQueue<Action>>();
                stack.Push(work);
            }
            var onUiThread = !RhinoApp.InvokeRequired;
            var nextCheck = DateTime.UtcNow.AddSeconds(0.5);
            try
            {
                Waiting?.Invoke(controls);
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.FreePause, "work on the paused thread failed: {0}", Log.Describe(ex));
            }
            Log.Debug(LogArea.Pause, "paused ({0}) -- Rhino stays usable; continue, step or stop from the Script Editor.",
                onUiThread ? "script on the UI thread, Pump=" + Pump : "script on its own thread");
            try
            {
                while (true)
                {
                    while (work.TryDequeue(out var job))
                        RunJob(job);
                    var action = (DebugAction?)s_action.GetValue(controls);
                    if (action.HasValue)
                    {
                        // Taken: an outer pause of the same debugger waits for its own action.
                        lock (s_actionLock.GetValue(controls))
                            s_action.SetValue(controls, null);
                        return action.Value;
                    }
                    if (DateTime.UtcNow >= nextCheck)
                    {
                        nextCheck = DateTime.UtcNow.AddSeconds(0.5);
                        bool abandoned;
                        try
                        {
                            abandoned = Abandoned(controls);
                        }
                        catch (Exception)
                        {
                            abandoned = false;
                        }
                        if (abandoned)
                        {
                            UserLog.Warning("A paused script's Script Editor was closed: the script is stopped (nothing could continue it).");
                            return DebugAction.Stop;
                        }
                    }
                    if (onUiThread)
                    {
                        // views, panels, commands typed meanwhile, the editor's buttons
                        if (Pump == PumpKind.Eto)
                            Eto.Forms.Application.Instance.RunIteration();
                        else
                            RhinoApp.Wait();
                    }
                    Thread.Sleep(10);
                }
            }
            finally
            {
                // No more work from now on; what was queued before still runs here, on the
                // paused thread -- its callers wait for an answer.
                lock (s_waiting)
                {
                    if (s_waiting.TryGetValue(controls, out var stack))
                    {
                        if (stack.Count > 0 && ReferenceEquals(stack.Peek(), work))
                            stack.Pop();
                        if (stack.Count == 0)
                            s_waiting.Remove(controls);
                    }
                }
                while (work.TryDequeue(out var late))
                    RunJob(late);
                Log.Trace(LogArea.FreePause, "pause over");
            }
        }
    }
}
