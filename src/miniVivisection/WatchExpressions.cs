using System;
using System.Reflection;
using HarmonyLib;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Makes the Script Editor's own Watch tab evaluate expressions -- <c>1+2</c>,
    /// <c>crv.PointAtStart</c>, <c>[c.PointAtEnd for c in curves]</c> -- not only
    /// variable names.
    ///
    /// The Watch tab (<c>VariableWatchView</c>, its only caller) asks the paused frame,
    /// <c>ExecFrame.Evaluate(DebugExpression, out result)</c>, which looks the
    /// expression up among the frame's variables. A Harmony postfix takes over when
    /// that finds nothing: the hook evaluates the expression in the paused Python frame
    /// and wraps the value with RhinoCode's own <c>__pynet_tracevar__</c> -- the same
    /// value text, type and expandable children as the Variables tab. A Python error
    /// becomes the row's value (<c>NameError: ...</c>). C# and frames other than the
    /// paused one keep the editor's behavior.
    /// </summary>
    internal static class WatchExpressions
    {
        private static Harmony s_harmony;
        private static ScriptEditorHook s_hook;
        private static string s_error;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_error ?? "not installed";
        private static bool s_reportedFailure;

        [ThreadStatic]
        private static bool t_bypass;

        /// <summary>MiniVivisection WatchExpressions=On/Off.</summary>
        public static bool Enabled
        {
            get => MiniVivisectionSettings.WatchExpressions;
            set => MiniVivisectionSettings.WatchExpressions = value;
        }

        /// <summary>The plug-in's own evaluation (the V Watch tab's drawing) wants the
        /// editor's plain result: set while it asks the frame.</summary>
        internal static bool Bypass
        {
            get => t_bypass;
            set => t_bypass = value;
        }

        public static void TryInstall(ScriptEditorHook hook)
        {
            s_hook = hook;
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                var evaluate = typeof(ExecFrame).GetMethod(nameof(ExecFrame.Evaluate), BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(DebugExpression), typeof(IDebugExpressionResult).MakeByRefType() }, null)
                    ?? throw new MissingMethodException(nameof(ExecFrame), nameof(ExecFrame.Evaluate));
                var harmony = new Harmony("minivivisection.watchexpressions");
                harmony.Patch(evaluate, postfix: new HarmonyMethod(typeof(WatchExpressions), nameof(EvaluatePostfix)));
                s_harmony = harmony;
                Log.Debug(LogArea.Watch, "Watch tab expressions: ExecFrame.Evaluate patched");
            }
            catch (Exception ex)
            {
                s_error = ex.Message;
                UserLog.Warning("The Watch tab keeps evaluating variable names only: {0}", ex.Message);
            }
        }

        private static void EvaluatePostfix(ExecFrame __instance, DebugExpression expression, ref IDebugExpressionResult result, ref bool __result)
        {
            if (__result || !Enabled || t_bypass || s_hook == null || expression == null || string.IsNullOrWhiteSpace(expression.ExpressionString))
                return;
            t_bypass = true; // RhinoCode's variable code may ask frames again
            try
            {
                if (s_hook.TryEvaluateWatch(__instance, expression, out var variable))
                {
                    result = new DebugExpressionExecVariableResult(expression, variable);
                    __result = true;
                }
            }
            catch (Exception ex)
            {
                if (!s_reportedFailure)
                {
                    s_reportedFailure = true;
                    Log.Warning(LogArea.Watch, "couldn't evaluate a Watch expression: {0}", ScriptEditorHook.FirstLine(ex));
                }
            }
            finally
            {
                t_bypass = false;
            }
        }
    }
}
