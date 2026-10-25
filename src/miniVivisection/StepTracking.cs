using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// "Step over" across files. RhinoCode tracks the paused thread's frames in a
    /// <c>DebugThreadEventStack</c> and steps by its depth. A frame's <c>Return</c> or
    /// <c>Exception</c> clears the frames below it only when they are of the same file. When
    /// a function in file B ends with <c>return c.f()</c> (file C), C's return marker is
    /// still on top when B returns: B's frame stays, the depth never comes back to the
    /// stepping line's, and "step over" never pauses again -- the rest of the script runs in
    /// one go. (minivivisection_visualize._visualizer does exactly that; so may any module.)
    ///
    /// A Harmony prefix on <c>OnReturn</c> and <c>OnException</c> first removes finished
    /// frames -- return and exception markers -- of other files from the top of the stack:
    /// they belong to calls that are over. Then RhinoCode does what it always does. Line
    /// and call events are left alone (a line event reads the last return marker for
    /// "step out"). Checked against RhinoCode's real stepping, outside Rhino (StepChecks).
    /// </summary>
    internal static class StepTracking
    {
        private static Harmony s_harmony;
        private static string s_error;
        private static FieldInfo s_eventStack;
        private static bool s_reportedFailure;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_error ?? "not installed";

        /// <summary>For the checks: RhinoCode's own behaviour when false.</summary>
        internal static bool Enabled { get; set; } = true;

        public static void TryInstall()
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                var type = typeof(DebugThreadEventStack);
                s_eventStack = type.GetField("_eventStack", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new MissingFieldException(type.FullName, "_eventStack");
                var flags = BindingFlags.Public | BindingFlags.Instance;
                var onReturn = type.GetMethod(nameof(DebugThreadEventStack.OnReturn), flags)
                    ?? throw new MissingMethodException(type.FullName, nameof(DebugThreadEventStack.OnReturn));
                var onException = type.GetMethod(nameof(DebugThreadEventStack.OnException), flags)
                    ?? throw new MissingMethodException(type.FullName, nameof(DebugThreadEventStack.OnException));
                var harmony = new Harmony("minivivisection.steptracking");
                var prefix = new HarmonyMethod(typeof(StepTracking), nameof(DropFinishedOfOtherFiles));
                harmony.Patch(onReturn, prefix: prefix);
                harmony.Patch(onException, prefix: prefix);
                s_harmony = harmony;
                Log.Debug(LogArea.Pause, "step over across files: DebugThreadEventStack.OnReturn/OnException patched");
            }
            catch (Exception ex)
            {
                s_error = ex.Message;
                Log.Warning(LogArea.Pause, "step over across files stays as RhinoCode has it: {0}", ex.Message);
            }
        }

        // Nothing may escape: this runs inside RhinoCode's trace of every Python event.
        private static void DropFinishedOfOtherFiles(DebugThreadEventStack __instance, ExecFrame frame)
        {
            if (!Enabled || frame == null)
                return;
            try
            {
                var stack = (Stack<ExecFrame>)s_eventStack.GetValue(__instance);
                while (stack.Count > 0)
                {
                    var top = stack.Peek();
                    if ((top.Event != ExecEvent.Return && top.Event != ExecEvent.Exception)
                        || !Equals(top.ContextId, frame.ContextId) || !Equals(top.ThreadId, frame.ThreadId)
                        || top.Reference.Uri == frame.Reference.Uri)
                        break;
                    stack.Pop(); // a finished call into another file
                }
            }
            catch (Exception ex)
            {
                if (!s_reportedFailure)
                {
                    s_reportedFailure = true;
                    Log.Error(LogArea.Pause, "step tracking: {0}", Log.Describe(ex));
                }
            }
        }
    }
}
