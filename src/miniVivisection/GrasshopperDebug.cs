using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Rhino;
using Rhino.Runtime.Code.Execution;

namespace MiniVivisection
{
    /// <summary>
    /// Grasshopper's script components (RhinoCodePluginGH) run a solution in the debugger only
    /// when it was started from the script editor's Debug: their ScriptContext.GetBuildKind
    /// gives Debug only while the static SolutionKind is Debug (set by the editor), the code
    /// has debug controls, its editor is visible and Stop wasn't pressed. Any other solution --
    /// a slider moved, an input changed -- runs plainly: its breakpoints don't pause.
    /// Options: "Grasshopper script components" -- Normal (that), or Always debug: a postfix
    /// gives Debug for every solution of a component whose script editor is open (a pause needs
    /// it to continue) unless Stop was pressed, so its breakpoints pause on any recompute.
    ///
    /// The component editor's own Run and Debug buttons (RhinoCodePlatform.GH.Context's
    /// RunCodeCommand / RunDebugCodeCommand, both an ExecuteCommand that recomputes with
    /// its BuildKind): Debug always runs in the debugger, also without a breakpoint (the
    /// editor enables it only with one: Code.CanDebug; a postfix on its OnStateChanged enables
    /// it for any traceable script while nothing runs; option "Run and Debug buttons"). Run
    /// runs plainly -- except under Always debug, which covers every solution, Run's too, so
    /// Break now (Ctrl+Alt+B) can always pause a component whose editor is open.
    /// </summary>
    internal static class GrasshopperDebug
    {
        private const string PluginAssembly = "RhinoCodePluginGH";
        private const string ContextAssembly = "RhinoCodePlatform.GH.Context";
        private const string ContextType = "RhinoCodePluginGH.Components.BaseScriptComponent`2+ScriptContext";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Harmony s_harmony;
        private static string s_error;
        private static bool s_listening;
        private static Harmony s_buttonsHarmony;
        private static FieldInfo s_commandEditor;

        /// <summary>For the health line (EditorHooks): null when installed or Grasshopper isn't loaded yet.</summary>
        internal static string HookProblem => s_error;

        /// <summary>At load: patched now if Grasshopper's script components are loaded, else when they load.</summary>
        public static void Start()
        {
            if (!s_listening)
            {
                s_listening = true;
                AppDomain.CurrentDomain.AssemblyLoad += (sender, e) =>
                {
                    var name = e.LoadedAssembly.GetName().Name;
                    if (name == PluginAssembly)
                        TryInstall(e.LoadedAssembly);
                    else if (name == ContextAssembly)
                        TryInstallButtons(e.LoadedAssembly);
                };
            }
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == PluginAssembly);
            if (loaded != null)
                TryInstall(loaded);
            var context = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == ContextAssembly);
            if (context != null)
                TryInstallButtons(context);
        }

        /// <summary>The component editor's Run (always plain) and Debug (also without a breakpoint).</summary>
        private static void TryInstallButtons(Assembly assembly)
        {
            if (s_buttonsHarmony != null)
                return;
            try
            {
                var types = assembly.GetTypes();
                var execute = types.FirstOrDefault(t => t.Name == "ExecuteCommand") ?? throw new TypeLoadException("ExecuteCommand");
                var debug = types.FirstOrDefault(t => t.Name == "RunDebugCodeCommand") ?? throw new TypeLoadException("RunDebugCodeCommand");
                for (var t = execute; t != null && s_commandEditor == null; t = t.BaseType)
                    s_commandEditor = t.GetField("m_editor", Any | BindingFlags.DeclaredOnly);
                if (s_commandEditor == null)
                    throw new MissingFieldException(execute.FullName, "m_editor");
                var onStateChanged = debug.GetMethod("OnStateChanged", Any | BindingFlags.DeclaredOnly) ?? throw new MissingMethodException(debug.FullName, "OnStateChanged");
                var harmony = new Harmony("minivivisection.grasshopperbuttons");
                harmony.Patch(onStateChanged, postfix: new HarmonyMethod(typeof(GrasshopperDebug), nameof(DebugStatePostfix)));
                s_buttonsHarmony = harmony;
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                UserLog.Warning("The Grasshopper script editor's Run and Debug keep their own behaviour: {0}", ex.Message);
            }
        }

        // Debug: enabled for any script that can be traced while nothing runs, breakpoint or not.
        private static void DebugStatePostfix(object __instance)
        {
            try
            {
                var enabled = __instance.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
                if (enabled == null || (bool)enabled.GetValue(__instance) || !MiniVivisectionSettings.DebugRunButton)
                    return;
                var editor = s_commandEditor.GetValue(__instance);
                if (editor == null || editor.GetType().GetProperty("IsExecuting", Any)?.GetValue(editor) as bool? != false)
                    return;
                if (editor.GetType().GetProperty("ActiveCode", Any)?.GetValue(editor) is Rhino.Runtime.Code.Code code && code.Language.Capabilities.CanTrace)
                    enabled.SetValue(__instance, true);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper Debug without breakpoints: {0}", Log.Describe(ex));
            }
        }

        private static void TryInstall(Assembly assembly)
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                var generic = assembly.GetType(ContextType, throwOnError: true);
                var paramType = assembly.GetType("RhinoCodePluginGH.Parameters.ScriptParam") ?? assembly.GetTypes().First(t => t.Name == "ScriptParam");
                var libraryType = assembly.GetType("RhinoCodePluginGH.Parameters.ScriptLibraryParam") ?? assembly.GetTypes().First(t => t.Name == "ScriptLibraryParam");
                var context = generic.MakeGenericType(paramType, libraryType);
                var getBuildKind = context.GetMethod("GetBuildKind", Any) ?? throw new MissingMethodException(context.FullName, "GetBuildKind");
                var harmony = new Harmony("minivivisection.grasshopperdebug");
                harmony.Patch(getBuildKind, postfix: new HarmonyMethod(typeof(GrasshopperDebug), nameof(GetBuildKindPostfix)));
                s_getBuildKind = getBuildKind;
                s_harmony = harmony;
                Log.Info(LogArea.Breakpoints, "Grasshopper's script components followed (\"Grasshopper script components\": {0})", MiniVivisectionSettings.GrasshopperScripts);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                s_error = ex.Message;
                UserLog.Warning("Grasshopper script components keep their own debugging (\"Grasshopper script components: Always debug\" can't apply): {0}", ex.Message);
            }
        }

        private static bool s_reportedNoStop;
        private static MethodInfo s_getBuildKind;

        /// <summary>Calls of the GetBuildKind postfix: GrasshopperRuns checks the patch is still there.</summary>
        internal static int Calls;

        internal static bool IsInstalled => s_harmony != null;

        /// <summary>The patch again (the runtime recompiled the method without it): see GrasshopperRuns.Repair.</summary>
        internal static void Repatch()
        {
            if (s_harmony == null || s_getBuildKind == null)
                return;
            s_harmony.UnpatchAll(s_harmony.Id);
            s_harmony.Patch(s_getBuildKind, postfix: new HarmonyMethod(typeof(GrasshopperDebug), nameof(GetBuildKindPostfix)));
        }

        private static void GetBuildKindPostfix(object __instance, Rhino.Runtime.Code.Code code, ref BuildKind __result)
        {
            Calls++;
            try
            {
                if (__result != BuildKind.Run || !MiniVivisectionSettings.GrasshopperAlwaysDebug || code == null || !code.HasDebugControls)
                    return;
                var type = __instance.GetType();
                // A static of ScriptContext, the base of the component's own context type: found
                // up the base types (GetProperty doesn't look at a base's statics).
                var stopped = type.GetProperty("IsDebugStopped", Any | BindingFlags.FlattenHierarchy);
                if (stopped == null)
                {
                    // Without it Stop couldn't end the solution's debugging: Always debug is off then.
                    if (!s_reportedNoStop)
                    {
                        s_reportedNoStop = true;
                        UserLog.Warning("Grasshopper: Always debug isn't available in this Rhino version.");
                    }
                    return;
                }
                if (stopped.GetValue(null) as bool? == true)
                    return; // Stop was pressed: the rest of this solution runs plainly
                if (RhinoApp.MainWindowHandle() != IntPtr.Zero && type.GetProperty("HasVisibleEditor", Any)?.GetValue(__instance) as bool? != true &&
                    !GrasshopperHiddenEditors.HasHiddenEditor(__instance))
                    return; // no editor to continue from (none open, none hidden): a pause would hold Grasshopper with no way on
                __result = BuildKind.Debug;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper, always debug: {0}", Log.Describe(ex));
            }
        }
    }
}
