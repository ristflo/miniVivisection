using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Eto.Forms;
using HarmonyLib;

namespace MiniVivisection
{
    /// <summary>
    /// Hidden editors for Grasshopper's Python 3 script components, under Always debug.
    ///
    /// A component's debugger lives in its script editor: with the editor closed, Grasshopper
    /// runs the script untraced, and Ctrl+Alt+B can only stop it. So, while Always debug is on
    /// (and "Hidden editors for Grasshopper components"), every Python 3 script component in an
    /// open definition gets its editor -- out of sight (DWM-cloaked, as RunPythonScript's Debug
    /// does), without taking the focus. Its solutions then run in the debugger (GrasshopperDebug:
    /// the editor counts as open), a breakpoint or Ctrl+Alt+B pauses them, and the pause brings
    /// the editor up (ScriptEditorHook -> ScriptRunCommand.Reveal). Opening the component's
    /// editor yourself brings it up too (a prefix on RhinoCodePlatform.GH.Context's
    /// ScriptEditor.BeginEdit -- not generic, so .NET's recompiling keeps the patch).
    ///
    /// A scan every two seconds (UI thread, never during a solution or a pause) opens one such
    /// editor at a time, and closes the hidden ones whose component is gone, or all of them when
    /// Always debug or the option is turned off. An editor brought up stays as you leave it;
    /// closed, it's hidden again by the next scan.
    ///
    /// A hidden editor isn't "visible" to Grasshopper (a postfix on ScriptEditor.HasVisibleEditor,
    /// not generic either): the canvas would draw the cone from the component to its editor's
    /// place, and the run's output would go to an editor nobody sees. Always debug asks
    /// <see cref="HasHiddenEditor"/> instead.
    ///
    /// Rhino 9: RhinoCodePlatform.GH.Context is loaded with Rhino's code platform at startup, long
    /// before Grasshopper, and its ScriptEditor is generic -- ScriptEditor&lt;TOwner&gt;; the
    /// component's is RhinoCodePluginGH's nested BaseScriptComponent`2+ScriptEditor, whose base
    /// ScriptEditor&lt;BaseScriptComponent&lt;ScriptParam, ScriptLibraryParam&gt;&gt; declares BeginEdit and
    /// HasVisibleEditor: patched when RhinoCodePluginGH loads. The window is IScriptEditor.GetWindow()
    /// (a GH1Editor form). A new one shows itself in RhinoCodeEditor's CodeEditorForm&lt;TEditor&gt;.BeginEdit(),
    /// kept invisible there (Rhino 8 does it in ScriptRunCommand). Members of generic classes: when
    /// .NET recompiles their shared code the patches are gone -- put back by the scan (RepairPatches).
    /// </summary>
    internal static class GrasshopperHiddenEditors
    {
#if RHINO9
        // Patched when Grasshopper's script components load (see the summary).
        private const string HookAssembly = "RhinoCodePluginGH";
#else
        private const string HookAssembly = "RhinoCodePlatform.GH.Context";
#endif
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const int Most = 24; // hidden editors at a time: each one is a Script Editor window

        private sealed class Hidden
        {
            public WeakReference<object> Component;
            public WeakReference<Window> Window;
        }

        private static readonly List<Hidden> s_hidden = new List<Hidden>();
        private static readonly ConditionalWeakTable<object, object> s_failed = new ConditionalWeakTable<object, object>();
        private static UITimer s_scan;
        private static bool s_opening;
        private static bool s_listening;
        private static Harmony s_harmony;
        private static string s_error;
        private static bool s_reportedMost;

#if RHINO9
        /// <summary>For the health line (EditorHooks): null when installed; "waiting for Grasshopper"
        /// while Grasshopper's script components aren't loaded yet.</summary>
        internal static string HookProblem => s_error ?? (s_harmony == null ? GrasshopperRuns.WaitingForGrasshopper : null);
#else
        /// <summary>For the health line (EditorHooks): null when installed or Grasshopper isn't loaded yet.</summary>
        internal static string HookProblem => s_error;
#endif

        /// <summary>How many hidden editors there are now (for the live test).</summary>
        internal static int Count
        {
            get
            {
                lock (s_hidden)
                    return s_hidden.Count(h => h.Window.TryGetTarget(out var w) && IsHidden(w));
            }
        }

        /// <summary>At load (UI thread): the scan, and the patch once Grasshopper's script editor is loaded.</summary>
        public static void Start()
        {
            if (!s_listening)
            {
                s_listening = true;
                AppDomain.CurrentDomain.AssemblyLoad += (sender, e) =>
                {
                    if (e.LoadedAssembly.GetName().Name == HookAssembly)
                        TryInstall(e.LoadedAssembly);
                };
            }
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == HookAssembly);
            if (loaded != null)
                TryInstall(loaded);
            if (s_scan == null)
            {
                ScriptRunCommand.StandingRevealed += _ => RedrawCanvas();
                s_scan = new UITimer { Interval = 2 };
                s_scan.Elapsed += (sender, e) => Scan();
                s_scan.Start();
            }
        }

        private static void TryInstall(Assembly assembly)
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
#if RHINO9
                var editor = EditorType9(assembly);
#else
                var editor = assembly.GetType("RhinoCodePlatform.GH.Context.ScriptEditor", throwOnError: true);
#endif
                var beginEdit = editor.GetMethods(Any).FirstOrDefault(m => m.Name == "BeginEdit" && m.DeclaringType == editor && m.GetParameters().Length == 4)
                    ?? throw new MissingMethodException(editor.FullName, "BeginEdit(window, script, culture, culture)");
                var hasVisible = editor.GetProperty("HasVisibleEditor", Any)?.GetGetMethod(true)
                    ?? throw new MissingMethodException(editor.FullName, "HasVisibleEditor");
                var harmony = new Harmony("minivivisection.grasshopperhiddeneditors");
                try
                {
                    harmony.Patch(beginEdit, prefix: new HarmonyMethod(typeof(GrasshopperHiddenEditors), nameof(BeginEditPrefix)));
                    harmony.Patch(hasVisible, postfix: new HarmonyMethod(typeof(GrasshopperHiddenEditors), nameof(HasVisibleEditorPostfix)));
#if RHINO9
                    s_beginEdit = beginEdit;
                    s_hasVisible = hasVisible;
                    PatchNewForms9(assembly, harmony);
#endif
                }
                catch
                {
                    harmony.UnpatchAll(harmony.Id); // both or neither
                    throw;
                }
                s_harmony = harmony;
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                s_error = ex.Message;
                UserLog.Warning("Grasshopper's hidden editors are off: opening a component's editor couldn't bring a hidden one up ({0}).", ex.Message);
            }
        }

        // The component's editor opened -- by you (double-click, its menu): a hidden one comes up.
        private static void BeginEditPrefix(object __instance)
        {
            if (s_opening)
                return; // the scan's own
            try
            {
                var window = WindowOf(__instance);
                if (window != null && ScriptRunCommand.IsStanding(window))
                {
                    ScriptRunCommand.Reveal(window, paused: true);
                    Log.Info(LogArea.Breakpoints, "Grasshopper: a hidden script editor opened by the user -- shown");
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper hidden editor, opened: {0}", Log.Describe(ex));
            }
        }

        // A hidden editor: not visible to Grasshopper -- no cone on the canvas, output stays the run's.
        private static void HasVisibleEditorPostfix(object __instance, ref bool __result)
        {
            try
            {
                if (__result && ScriptRunCommand.IsStanding(WindowOf(__instance)))
                    __result = false;
            }
            catch (Exception)
            {
                // as Grasshopper says
            }
        }

        /// <summary>Whether a script component's context (ScriptContext) has a hidden editor:
        /// for Always debug, which runs such a component in the debugger as if its editor were open.</summary>
        internal static bool HasHiddenEditor(object context)
        {
            var editor = context?.GetType().GetProperty("ScriptEditor", Any)?.GetValue(context);
            return editor != null && ScriptRunCommand.IsStanding(WindowOf(editor));
        }

        /// <summary>The editor window of a component's script editor (RhinoCodePlatform.GH.Context's
        /// ScriptEditor: its CodeEditor's _rce), or null while it has none.</summary>
        private static Window WindowOf(object scriptEditor)
        {
#if RHINO9
            // Rhino 9: IScriptEditor.GetWindow() -- the editor's form (a GH1Editor), null without one.
            // Called by HasVisibleEditor's postfix in every solution: the method looked up once (every
            // component's editor is the same closed type).
            if (scriptEditor == null)
                return null;
            var getWindow = s_getWindow;
            if (getWindow == null || !getWindow.DeclaringType.IsInstanceOfType(scriptEditor))
                s_getWindow = getWindow = scriptEditor.GetType().GetMethod("GetWindow", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return getWindow?.Invoke(scriptEditor, null) is Window window && !window.IsDisposed ? window : null;
#else
            var codeEditor = scriptEditor?.GetType().GetProperty("CodeEditor", Any)?.GetValue(scriptEditor);
            return codeEditor?.GetType().GetField("_rce", Any)?.GetValue(codeEditor) as Window;
#endif
        }

        /// <summary>Grasshopper's canvas drawn again: a shown editor's cone (from the component to
        /// its editor) appears only when the canvas is drawn, and nothing else on it changed.</summary>
        internal static void RedrawCanvas()
        {
            try
            {
                var grasshopper = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Grasshopper");
                var canvas = grasshopper?.GetType("Grasshopper.Instances")?.GetProperty("ActiveCanvas")?.GetValue(null);
                canvas?.GetType().GetMethod("Invalidate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(canvas, null); // a WinForms control
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper canvas redraw: {0}", Log.Describe(ex));
            }
        }

        private static object ScriptEditorOf(object component)
        {
            var context = GrasshopperRuns.ContextOf(component);
            return context?.GetType().GetProperty("ScriptEditor", Any)?.GetValue(context);
        }

        /// <summary>A hidden editor still: kept invisible, and not closed (hidden by its close).</summary>
        private static bool IsHidden(Window window) => ScriptRunCommand.IsStanding(window) && window.Visible;

        private static bool Wanted => MiniVivisectionSettings.GrasshopperHiddenEditors && s_harmony != null;

        private static void Scan()
        {
            try
            {
                if (s_opening || GrasshopperRuns.Solving || GrasshopperPauseGuard.AnyPaused())
                    return;
                var wanted = Wanted;
                Tidy(wanted);
#if RHINO9
                if (wanted)
                    RepairPatches();
                if (!wanted || !s_newFormsPatched)
                    return;
#else
                if (!wanted || !ScriptRunCommand.CanHideNewEditors())
                    return;
#endif
                var candidate = Candidates().FirstOrDefault();
                if (candidate == null)
                    return;
                if (Count >= Most)
                {
                    if (!s_reportedMost)
                    {
                        s_reportedMost = true;
                        UserLog.Warning("Grasshopper: {0} hidden script editors -- no more are opened. Open a component's editor to debug it.", Most);
                    }
                    return;
                }
                OpenHidden(candidate);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper hidden editors: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The hidden editors still wanted: shown ones (a pause, you) are yours now;
        /// those of deleted components -- or all, when no longer wanted -- are closed.</summary>
        private static void Tidy(bool wanted)
        {
            List<Hidden> each;
            lock (s_hidden)
                each = s_hidden.ToList();
            foreach (var hidden in each)
            {
                hidden.Window.TryGetTarget(out var window);
                hidden.Component.TryGetTarget(out var component);
                var standing = window != null && IsHidden(window);
                var gone = component == null || GrasshopperRuns.DocumentOf(component) == null;
                if (standing && (!wanted || gone))
                {
                    try
                    {
                        // A component still there keeps its editor, hidden (as its close button
                        // does): an editor made anew for the same code has debug controls of its
                        // own, and couldn't continue a pause of the code's first ones.
                        var editor = gone && component != null ? ScriptEditorOf(component) : null;
                        if (editor != null)
                            editor.GetType().GetMethod("EndEdit", Any, null, Type.EmptyTypes, null)?.Invoke(editor, null);
                        else
                            ScriptRunCommand.CloseStanding(window);
                        Log.Info(LogArea.Breakpoints, "Grasshopper: a hidden script editor closed ({0})", gone ? "its component is gone" : "Always debug or hidden editors off");
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(LogArea.Breakpoints, "closing a hidden editor: {0}", Log.Describe(ex));
                    }
                    standing = false;
                }
                if (!standing)
                {
                    lock (s_hidden)
                        s_hidden.Remove(hidden);
                }
            }
        }

        /// <summary>Python 3 script components of the open, enabled definitions without an editor on
        /// screen -- or with only a hidden one.</summary>
        private static IEnumerable<object> Candidates()
        {
            var grasshopper = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Grasshopper");
            var server = grasshopper?.GetType("Grasshopper.Instances")?.GetProperty("DocumentServer")?.GetValue(null) as IEnumerable;
            if (server == null)
                yield break;
            foreach (var document in server.Cast<object>().ToList())
            {
                if (document.GetType().GetProperty("Enabled")?.GetValue(document) as bool? != true)
                    continue;
                if (!(document.GetType().GetProperty("Objects")?.GetValue(document) is IEnumerable objects))
                    continue;
                foreach (var component in objects.Cast<object>().ToList())
                {
                    if (!GrasshopperRuns.IsScriptComponent(component) || s_failed.TryGetValue(component, out _))
                        continue;
                    var type = component.GetType();
                    if (type.GetProperty("Locked")?.GetValue(component) as bool? == true ||
                        type.GetProperty("UsingScriptInputParam", Any)?.GetValue(component) as bool? == true)
                        continue;
                    if (type.Name != "Python3Component" && !GrasshopperRuns.IsPython3(GrasshopperRuns.CodeOf(component)))
                        continue;
                    if (GrasshopperRuns.HasVisibleEditor(component) || HasHiddenEditor(GrasshopperRuns.ContextOf(component)))
                        continue; // on screen, or hidden already
                    yield return component;
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        private static void OpenHidden(object component)
        {
            var name = GrasshopperRuns.NickName(component);
            var before = GetForegroundWindow();
            var existing = WindowOf(ScriptEditorOf(component)); // closed by you: there, but hidden
            s_opening = true;
            try
            {
                if (existing != null)
                {
                    ScriptRunCommand.HideStanding(existing);
                    GrasshopperRuns.OpenEditor(component);
                }
                else
                {
#if RHINO9
                    s_hideNewForm = true; // NewFormPrefix9: the new form kept invisible
                    try
                    {
                        GrasshopperRuns.OpenEditor(component);
                    }
                    finally
                    {
                        s_hideNewForm = false;
                    }
#else
                    ScriptRunCommand.OpenHidden(() => GrasshopperRuns.OpenEditor(component));
#endif
                }
            }
            catch (Exception ex)
            {
                s_failed.AddOrUpdate(component, null);
                Log.Warning(LogArea.Breakpoints, "Grasshopper: '{0}': its hidden editor: {1}", name, Log.Describe(ex));
            }
            finally
            {
                s_opening = false;
                if (before != IntPtr.Zero && GetForegroundWindow() != before)
                    SetForegroundWindow(before); // the focus stays where it was
            }
            var window = WindowOf(ScriptEditorOf(component));
            if (window == null || !ScriptRunCommand.IsStanding(window))
            {
                s_failed.AddOrUpdate(component, null); // not again for this component
                Log.Info(LogArea.Breakpoints, "Grasshopper: '{0}': no hidden editor ({1})", name, window == null ? "none opened" : "it's on screen");
                return;
            }
            lock (s_hidden)
                s_hidden.Add(new Hidden { Component = new WeakReference<object>(component), Window = new WeakReference<Window>(window) });
#if RHINO9
            RepairPatches(ScriptEditorOf(component));
#endif
            Log.Info(LogArea.Breakpoints, "Grasshopper: '{0}' has its script editor, out of sight -- Ctrl+Alt+B or a breakpoint pauses it", name);
        }

#if RHINO9
        // --- Rhino 9 -------------------------------------------------------------------------------

        private static MethodInfo s_beginEdit;  // ScriptEditor<TOwner>.BeginEdit(window, script, culture, culture)
        private static MethodInfo s_hasVisible; // ScriptEditor<TOwner>.HasVisibleEditor's getter
        private static MethodInfo s_newForm;    // CodeEditorForm<IMiniEditor>.BeginEdit()
        private static MethodInfo s_getWindow;  // ScriptEditor<TOwner>.GetWindow()
        private static bool s_newFormsPatched;
        private static bool s_hideNewForm;      // set around OpenEditor by OpenHidden (UI thread)
        private static long s_lastRepair;

        /// <summary>For the live test: how often the patches were put back.</summary>
        internal static int Repairs { get; private set; }

        /// <summary>The script editor type declaring BeginEdit and HasVisibleEditor:
        /// ScriptEditor&lt;BaseScriptComponent&lt;ScriptParam, ScriptLibraryParam&gt;&gt;, the base of the
        /// component's nested BaseScriptComponent`2+ScriptEditor.</summary>
        private static Type EditorType9(Assembly plugin)
        {
            var nested = plugin.GetType("RhinoCodePluginGH.Components.BaseScriptComponent`2+ScriptEditor", throwOnError: true);
            for (var type = Close9(plugin, nested).BaseType; type != null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "RhinoCodePlatform.GH.Context.ScriptEditor`1")
                    return type;
            }
            throw new TypeLoadException("RhinoCodePlatform.GH.Context.ScriptEditor`1 (the base of BaseScriptComponent`2+ScriptEditor)");
        }

        // A type nested in BaseScriptComponent<TScriptParam, TLibraryParam>, closed as Grasshopper's
        // script components are: <ScriptParam, ScriptLibraryParam>.
        private static Type Close9(Assembly plugin, Type nested) => nested.MakeGenericType(
            plugin.GetType("RhinoCodePluginGH.Parameters.ScriptParam", throwOnError: true),
            plugin.GetType("RhinoCodePluginGH.Parameters.ScriptLibraryParam", throwOnError: true));

        /// <summary>A new component editor shows itself in RhinoCodeEditor's CodeEditorForm&lt;TEditor&gt;.BeginEdit()
        /// (Show() while not Loaded), called by ScriptEditor&lt;TOwner&gt;.BeginEdit right after it made the form
        /// (RhinoCodePluginGH's GH1Editor : ScriptEditorForm : MiniEditor : CodeEditorForm&lt;IMiniEditor&gt;).
        /// Shared generic code: every Rhino 9 editor window's BeginEdit, gated by s_hideNewForm.</summary>
        private static void PatchNewForms9(Assembly plugin, Harmony harmony)
        {
            var form = Close9(plugin, plugin.GetType("RhinoCodePluginGH.Components.BaseScriptComponent`2+GH1Editor", throwOnError: true));
            while (form != null && !(form.IsGenericType && form.GetGenericTypeDefinition().FullName == "RhinoCodeEditor.CodeEditorForm`1"))
                form = form.BaseType;
            s_newForm = form?.GetMethod("BeginEdit", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException("RhinoCodeEditor.CodeEditorForm`1", "BeginEdit()");
            harmony.Patch(s_newForm, prefix: new HarmonyMethod(typeof(GrasshopperHiddenEditors), nameof(NewFormPrefix9)));
            s_newFormsPatched = true;
        }

        // CodeEditorForm<TEditor>.BeginEdit: a component's new editor, opened by OpenHidden, about to
        // show itself -- invisible from the start (cloaked: ScriptRunCommand's standing editors), and
        // shown without taking the focus from Grasshopper's canvas.
        private static void NewFormPrefix9(object __instance)
        {
            try
            {
                if (!s_hideNewForm || !(__instance is Window window) || window.Loaded)
                    return;
                var wpf = (window as Control)?.ControlObject;
                wpf?.GetType().GetProperty("ShowActivated")?.SetValue(wpf, false);
                ScriptRunCommand.HideStanding(window);
                Log.Info(LogArea.Tabs, "a new editor is made invisible before it shows (a Grasshopper component's hidden editor)");
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Tabs, "a new editor kept invisible: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Before a script component's run (GrasshopperRuns.ComputePrefix, UI thread): its
        /// hidden editor still not visible to Grasshopper (the patches there), else put back now --
        /// the run's output and the canvas's cone go by it.</summary>
        internal static void CheckBeforeRun(object component)
        {
            if (!Wanted)
                return;
            try
            {
                var editor = ScriptEditorOf(component);
                if (editor != null && ScriptRunCommand.IsStanding(WindowOf(editor)))
                    RepairPatches(editor);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "Grasshopper: the hidden editor's patches, checked before a run: {0}", Log.Describe(ex));
            }
        }

        private static void PatchAll9(Harmony harmony)
        {
            harmony.Patch(s_beginEdit, prefix: new HarmonyMethod(typeof(GrasshopperHiddenEditors), nameof(BeginEditPrefix)));
            harmony.Patch(s_hasVisible, postfix: new HarmonyMethod(typeof(GrasshopperHiddenEditors), nameof(HasVisibleEditorPostfix)));
            harmony.Patch(s_newForm, prefix: new HarmonyMethod(typeof(GrasshopperHiddenEditors), nameof(NewFormPrefix9)));
        }

        /// <summary>BeginEdit and HasVisibleEditor belong to generic classes: their code is shared, and
        /// when .NET recompiles it (tiered compilation; HasVisibleEditor runs in every solution) the new
        /// code comes without the patches -- as Always debug's GetBuildKind (GrasshopperRuns.RepairAlwaysDebug).
        /// A hidden editor Grasshopper sees as visible says so (a reflection call runs the method's
        /// current code): all put back, at most once a second. UI thread (Scan).</summary>
        private static void RepairPatches(object editor = null)
        {
            if (s_harmony == null || s_hasVisible == null || s_newForm == null || (editor == null && Environment.TickCount64 - s_lastRepair < 1000))
                return;
            try
            {
                if (editor == null)
                {
                    lock (s_hidden)
                    {
                        foreach (var hidden in s_hidden)
                        {
                            if (hidden.Window.TryGetTarget(out var window) && IsHidden(window) && hidden.Component.TryGetTarget(out var component))
                            {
                                editor = ScriptEditorOf(component);
                                if (editor != null)
                                    break;
                            }
                        }
                    }
                }
                if (editor == null || !ScriptRunCommand.IsStanding(WindowOf(editor)) || s_hasVisible.Invoke(editor, null) as bool? != true)
                    return;
                s_lastRepair = Environment.TickCount64;
                Repairs++;
                s_harmony.UnpatchAll(s_harmony.Id);
                PatchAll9(s_harmony);
                Log.Info(LogArea.Breakpoints, "Grasshopper: .NET recompiled the script editor's code without the hidden editors' patches -- put back");
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "Grasshopper: putting the hidden editors' patches back: {0}", Log.Describe(ex));
            }
        }
#endif
    }
}
