using System;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;

namespace MiniVivisection
{
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
}
