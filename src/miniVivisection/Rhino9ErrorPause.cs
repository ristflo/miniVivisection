#if RHINO9
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RhinoCodeItem = Rhino.Runtime.Code.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Rhino 9 runs on at an uncaught error, where Rhino 8 holds the pause at the failing line
    /// (lesson 17 relies on it). Rhino 9's <c>NextStep.ShouldPause</c> lost Rhino 8's rule -- pause
    /// at the "return" of the frame an exception leaves, while that frame and its values are still
    /// there -- and pauses only after the script has ended (<c>Code.TraceUncaughtException</c>, in
    /// <c>Code.Debug</c>'s catch: no Python frames left, no variables), where its editor disconnects
    /// at once (<c>CodesAspect.OnItemDebuggerPaused</c>: <c>controls.Disconnect()</c>). Here:
    /// - Rhino 8's rule is back: a postfix on <c>NextStep.ShouldPause</c> (policy PauseOnUncaught);
    /// - for that pause the editor takes its normal pause path: a prefix on the editor's
    ///   <c>OnItemDebuggerPaused</c> makes its item not "attached" for the call (the field, no
    ///   events), so its uncaught-error branch (Disconnect) isn't taken.
    /// After Continue the exception leaves the script and Rhino 9 pauses at it once more, post
    /// mortem; that pause is Rhino 9's own (its editor disconnects it and marks the error).
    /// Also: Rhino 9 shows the editor's Variables and Call Stack tabs only while
    /// <c>Code.CanDebug</c> -- which asks for a breakpoint; a postfix makes it true during a debug
    /// run too (Break now, the Debug button, an error pause without a breakpoint).
    /// </summary>
    internal static class Rhino9ErrorPause
    {
        private static Harmony s_harmony;
        private static string s_error;
        private static bool s_editorPatched;
        private static string s_editorError;
        private static FieldInfo s_stepKind;

        /// <summary>For the health line (EditorHooks): null when in place, else why not.</summary>
        internal static string HookProblem => s_error ?? s_editorError ?? (s_harmony == null ? "not installed" : null);

        // Debug controls paused by the rule below -> its exception frame, until that pause ends.
        private static readonly ConditionalWeakTable<DebugControls, ExecFrame> s_holding = new ConditionalWeakTable<DebugControls, ExecFrame>();
        // The exception a held pause was continued from: Rhino 9 pauses at it again after the script ended.
        private static readonly ConditionalWeakTable<DebugControls, ExecFrame> s_continued = new ConditionalWeakTable<DebugControls, ExecFrame>();
        private static readonly ConditionalWeakTable<DebugControls, object> s_followed = new ConditionalWeakTable<DebugControls, object>();

        /// <summary>Early (RhinoCode is loaded with RhinoCommon): the pause rule and Code.CanDebug.</summary>
        public static void TryInstall()
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                s_stepKind = typeof(NextStep).GetField("_stepKind", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new MissingFieldException(nameof(NextStep), "_stepKind");
                var shouldPause = typeof(NextStep).GetMethod(nameof(NextStep.ShouldPause), BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingMethodException(nameof(NextStep), nameof(NextStep.ShouldPause));
                var canDebug = typeof(RhinoCodeItem).GetProperty(nameof(RhinoCodeItem.CanDebug), BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod()
                    ?? throw new MissingMethodException(nameof(RhinoCodeItem), "get_CanDebug");
                var harmony = new Harmony("minivivisection.rhino9errorpause");
                try
                {
                    harmony.Patch(shouldPause, postfix: new HarmonyMethod(typeof(Rhino9ErrorPause), nameof(ShouldPausePostfix)));
                    harmony.Patch(canDebug, postfix: new HarmonyMethod(typeof(Rhino9ErrorPause), nameof(CanDebugPostfix)));
                }
                catch
                {
                    harmony.UnpatchAll(harmony.Id); // both or neither
                    throw;
                }
                s_harmony = harmony;
                Log.Info(LogArea.Pause, "Rhino 9: an uncaught error pauses at its line, as in Rhino 8 (NextStep.ShouldPause, Code.CanDebug patched)");
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                s_error = ex.Message;
                UserLog.Warning("Rhino 9 keeps running on at an uncaught error (no pause there): {0}", ex.Message);
            }
        }

        /// <summary>Once an editor window is known (ScriptEditorHook.Scan): the editor's
        /// OnItemDebuggerPaused, on its closed CodesAspect type (the generic class's code is
        /// shared by every kind of editor: one patch for all).</summary>
        public static void TryPatchEditor(Type codesAspectType)
        {
            if (s_editorPatched || s_harmony == null || codesAspectType == null)
                return;
            s_editorPatched = true;
            try
            {
                var paused = codesAspectType.GetMethod("OnItemDebuggerPaused", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    ?? throw new MissingMethodException(codesAspectType.FullName, "OnItemDebuggerPaused");
                s_harmony.Patch(paused,
                    prefix: new HarmonyMethod(typeof(Rhino9ErrorPause), nameof(OnItemDebuggerPausedPrefix)),
                    finalizer: new HarmonyMethod(typeof(Rhino9ErrorPause), nameof(OnItemDebuggerPausedFinalizer)));
                Log.Info(LogArea.Pause, "Rhino 9: the editor holds the pause at an uncaught error ({0}.OnItemDebuggerPaused patched)", codesAspectType.Name);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                s_editorError = "the editor's pause: " + ex.Message;
                UserLog.Warning("The Script Editor disconnects at an uncaught error (no pause there): {0}", ex.Message);
            }
        }

        // --- Rhino 8's rule ------------------------------------------------------------------------

        private static void ShouldPausePostfix(NextStep __instance, DebugControls controls, ExecFrame frame, DebugThreadEventStack stack, ref bool __result, ref ExecFrame headFrame)
        {
            if (frame == null || frame.Event != ExecEvent.Return || controls == null || stack == null)
                return;
            try
            {
                if (!MiniVivisectionSettings.PluginOn || controls.PauseOnExceptionPolicy != DebugPauseOnExceptionPolicy.PauseOnUncaught)
                    return;
                if ((NextStepKind)s_stepKind.GetValue(__instance) == NextStepKind.DoNotPause)
                    return;
                if (!stack.TryPeek(out var top) || top == null || top.Event != ExecEvent.Exception || top.Reference != frame.Reference)
                    return; // not the frame the exception leaves
                __result = true;
                headFrame = top; // shown and evaluated as the exception (Rhino 8: the same)
                Follow(controls);
                s_holding.AddOrUpdate(controls, top);
                Log.Info(LogArea.Pause, "uncaught {0} leaving line {1}: paused there (Rhino 8's rule)", top.Exception?.Id, top.Reference?.Position.LineNumber);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Pause, "the pause at an uncaught error: {0}", Log.Describe(ex));
            }
        }

        // --- the editor's normal pause path for that pause ----------------------------------------

        // CodesAspect<,,>.CodeItem.m_attached, per closed item type (the standalone editor's and
        // Grasshopper's are different instantiations: one's FieldInfo doesn't fit the other's item).
        private static readonly ConcurrentDictionary<Type, FieldInfo> s_attachedFields = new ConcurrentDictionary<Type, FieldInfo>();

        private static FieldInfo AttachedField(object item) => s_attachedFields.GetOrAdd(item.GetType(), t => TabStrips.FindField(t, "m_attached"));

        private static void OnItemDebuggerPausedPrefix(object item, IDebugControls controls, out bool __state)
        {
            __state = false;
            try
            {
                if (item == null || !(controls is DebugControls debugger) || !s_holding.TryGetValue(debugger, out var held) ||
                    !ReferenceEquals(controls.Results?.CurrentThread?.CurrentFrame, held))
                    return; // any other pause, the post-mortem one included: as Rhino 9 does it
                var field = AttachedField(item);
                if (!(field.GetValue(item) is bool attached) || !attached)
                    return;
                field.SetValue(item, false); // the field, not Attached: no events
                __state = true;
                Log.Debug(LogArea.Pause, "the editor's Disconnect at the uncaught error: not for this pause");
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Pause, "the editor at the uncaught error: {0}", Log.Describe(ex));
            }
        }

        private static Exception OnItemDebuggerPausedFinalizer(object item, bool __state, Exception __exception)
        {
            if (__state)
            {
                try
                {
                    AttachedField(item).SetValue(item, true);
                }
                catch (Exception)
                {
                    // the item is gone
                }
            }
            return __exception;
        }

        // --- the held pause's end, and Rhino 9's repeat --------------------------------------------

        private static void Follow(DebugControls controls)
        {
            lock (s_followed)
            {
                if (s_followed.TryGetValue(controls, out _))
                    return;
                s_followed.Add(controls, null);
            }
            controls.Continued += c => Ended(c, continued: true);
            controls.SteppedOver += c => Ended(c, continued: true); // at an exception a step goes on as Continue
            controls.SteppedIn += c => Ended(c, continued: true);
            controls.SteppedOut += c => Ended(c, continued: true);
            controls.Stopped += c => Ended(c, continued: false);
            controls.Disconnected += c => Ended(c, continued: false);
            controls.Detached += t =>
            {
                if (t is DebugControls d)
                {
                    s_holding.Remove(d);
                    s_continued.Remove(d);
                }
            };
        }

        private static void Ended(IDebugControls controls, bool continued)
        {
            if (!(controls is DebugControls d) || !s_holding.TryGetValue(d, out var held))
                return;
            s_holding.Remove(d);
            if (continued)
                s_continued.AddOrUpdate(d, held);
            else
                s_continued.Remove(d);
        }

        /// <summary>Whether this pause is Rhino 9's post-mortem repeat of an error the user paused
        /// at and continued from (Code.TraceUncaughtException: the same frame object).</summary>
        internal static bool IsRepeat(IDebugControls controls)
        {
            try
            {
                return controls is DebugControls d && s_continued.TryGetValue(d, out var held) &&
                       ReferenceEquals(controls.Results?.CurrentThread?.CurrentFrame, held);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // --- Variables and Call Stack during a debug run without breakpoints ------------------------

        private static void CanDebugPostfix(RhinoCodeItem __instance, ref bool __result)
        {
            if (__result || __instance == null || !MiniVivisectionSettings.PluginOn)
                return;
            try
            {
                // During a debug run of this code (its tracer attached): breakpoint or not.
                if (__instance.HasDebugControls && __instance.Language.Capabilities.CanTrace && __instance.CanRun &&
                    __instance.DebugControls.TracerState == TracerState.Attached)
                    __result = true;
            }
            catch (Exception)
            {
                // as Rhino says
            }
        }

        /// <summary>The editor's Variables and Call Stack tabs follow Code.CanDebug: told it may
        /// have changed (a pause, the run's end). UI thread.</summary>
        internal static void RefreshCanDebug(object window)
        {
            try
            {
                var codes = Rhino9Editor.ModelOf(window)?.Codes;
                codes?.GetType().GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)
                    ?.Invoke(codes, new object[] { "CanDebug" });
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Pause, "the editor's Variables tab: {0}", Log.Describe(ex));
            }
        }
    }
}
#endif
