using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;

namespace MiniVivisection
{
#if !RHINO9
    /// <summary>
    /// The Script Editor minimizes itself when a run starts (its "minimize on execute"
    /// option: <c>RCE.MinimizeOnExecute</c>, also when debugging) and comes back only at a
    /// pause. While a debugged script runs, the V Threads tab's "Where is everyone?" and
    /// "Break now" were then out of reach. This keeps the editor open for debug runs
    /// (state DebuggingCode) and for scripts with <c># async:true</c> (Rhino stays usable
    /// then); a plain Run of anything else still minimizes as the editor's option says.
    /// Options: "Keep the Script Editor open while debugging".
    /// </summary>
    internal static class KeepEditorOpen
    {
        private static Harmony s_harmony;
        private static string s_error;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_error ?? "not installed";

        /// <summary>Patches the editor once its type is known (an RCE window).</summary>
        public static void TryInstall(object editor)
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                var minimize = editor.GetType().GetMethod("MinimizeOnExecute", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null)
                    ?? throw new MissingMethodException(editor.GetType().FullName, "MinimizeOnExecute");
                var harmony = new Harmony("minivivisection.keepeditoropen");
                harmony.Patch(minimize, prefix: new HarmonyMethod(typeof(KeepEditorOpen), nameof(MinimizePrefix)));
                s_harmony = harmony;
            }
            catch (Exception ex)
            {
                s_error = ex.Message;
                Log.Warning(LogArea.Tabs, "the Script Editor will minimize while debugging: {0}", Log.Describe(ex));
            }
        }

        private static bool MinimizePrefix(object __instance, ref Task __result)
        {
            try
            {
                // Taken first, whatever the setting: one run's, never left for a later run.
                var minimize = ScriptRunCommand.TakeMinimize(__instance);
                if (!MiniVivisectionSettings.KeepEditorWhileDebugging)
                    return true;
                if (minimize)
                {
                    Log.Info(LogArea.Tabs, "run started by ScriptEditor Run, Debug: the editor minimizes until a pause");
                    return true;
                }
                var state = __instance.GetType().GetProperty("ActiveState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);
                var current = state?.GetType().GetProperty("CurrentState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(state)?.ToString();
                // Debugging, or an async:true script (Rhino stays usable, so the editor's
                // buttons are worth having); a plain Run of anything else blocks Rhino anyway.
                var debugging = current == "DebuggingCode";
                var isAsync = !debugging && ScriptKinds.AsksForAsync(new ScriptEditorAccess(__instance).ScriptText) == true;
                var keep = debugging || isAsync;
                Log.Info(LogArea.Tabs, "run started ({0}{1}): the editor {2}", current, isAsync ? ", async:true" : "", keep ? "stays open" : "minimizes as its option says");
                if (!keep)
                    return true;
                __result = Task.CompletedTask;
                return false;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "keep the editor open: {0}", Log.Describe(ex));
                return true;
            }
        }
    }
#else
    /// <summary>
    /// Rhino 9: the Script Editor minimizes itself while a script runs (its option "Minimize on
    /// execute": <c>MainEditor.GetBeMinimized</c>, decided again at every IsRunning and
    /// DebugControlsState change) and comes back at a pause. This keeps it open for debug runs and
    /// for scripts with <c># async:true</c> -- a prefix on the <c>BeMinimized</c> setter (Eto's
    /// binding calls it through reflection); a plain Run of anything else still minimizes as the
    /// editor's option says. Options: "Keep the Script Editor open while debugging".
    /// </summary>
    internal static class KeepEditorOpen
    {
        private static Harmony s_harmony;
        private static string s_error;

        /// <summary>For the health line (EditorHooks): null when installed, else why not -- "waiting
        /// for the Script Editor" while only Grasshopper's editors were seen (it patches the
        /// standalone editor's type).</summary>
        internal static string HookProblem => s_harmony != null ? null : s_error ?? "waiting for the Script Editor";

        // Rhino 9 decides at every state change: a change of decision is logged, not each call.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string> s_lastDecision =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, string>();

        /// <summary>Patches the editor once its type is known (the standalone editor's window,
        /// RhinoCodeEditor.MainEditor: Grasshopper's editors don't minimize).</summary>
        public static void TryInstall(object editor)
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                if (editor?.GetType().FullName != "RhinoCodeEditor.MainEditor")
                    return;
                var setter = editor.GetType().GetProperty("BeMinimized", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod()
                    ?? throw new MissingMethodException(editor.GetType().FullName, "set_BeMinimized");
                var harmony = new Harmony("minivivisection.keepeditoropen");
                harmony.Patch(setter, prefix: new HarmonyMethod(typeof(KeepEditorOpen), nameof(BeMinimizedPrefix)));
                s_harmony = harmony;
            }
            catch (Exception ex)
            {
                s_error = ex.Message;
                Log.Warning(LogArea.Tabs, "the Script Editor will minimize while debugging: {0}", Log.Describe(ex));
            }
        }

        private static void BeMinimizedPrefix(object __instance, ref bool value)
        {
            if (!value)
                return; // coming back (a pause, the end): as Rhino does it
            try
            {
                if (ScriptRunCommand.IsKeptInvisible(__instance))
                {
                    value = false; // cloaked for ScriptEditor Run, Debug: minimizing would only animate at the pause
                    return;
                }
                if (!MiniVivisectionSettings.KeepEditorWhileDebugging || ScriptRunCommand.IsCommandDebugRun(__instance))
                    return; // as the editor's option says
                var codes = Rhino9Editor.ModelOf(__instance)?.Codes;
                if (codes == null)
                    return;
                var debugging = codes.RunKind == Rhino.Runtime.Code.Execution.BuildKind.Debug;
                var isAsync = !debugging && ScriptKinds.AsksForAsync(new ScriptEditorAccess(__instance).ScriptText) == true;
                var keep = debugging || isAsync;
                var decision = keep ? (debugging ? "debugging: stays open" : "async:true: stays open") : "minimizes as its option says";
                if (!s_lastDecision.TryGetValue(__instance, out var last) || last != decision)
                {
                    s_lastDecision.AddOrUpdate(__instance, decision);
                    Log.Info(LogArea.Tabs, "run started ({0}): the editor {1}", codes.RunKind, decision);
                }
                if (keep)
                    value = false; // EndMinimize instead: nothing to undo
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "keep the editor open: {0}", Log.Describe(ex));
            }
        }
    }
#endif
}
