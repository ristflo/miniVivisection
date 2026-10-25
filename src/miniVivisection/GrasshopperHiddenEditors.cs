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
    /// </summary>
    internal static class GrasshopperHiddenEditors
    {
        private const string ContextAssembly = "RhinoCodePlatform.GH.Context";
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

        /// <summary>For the health line (EditorHooks): null when installed or Grasshopper isn't loaded yet.</summary>
        internal static string HookProblem => s_error;

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
                    if (e.LoadedAssembly.GetName().Name == ContextAssembly)
                        TryInstall(e.LoadedAssembly);
                };
            }
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == ContextAssembly);
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
                var editor = assembly.GetType("RhinoCodePlatform.GH.Context.ScriptEditor", throwOnError: true);
                var beginEdit = editor.GetMethods(Any).FirstOrDefault(m => m.Name == "BeginEdit" && m.DeclaringType == editor && m.GetParameters().Length == 4)
                    ?? throw new MissingMethodException(editor.FullName, "BeginEdit(window, script, culture, culture)");
                var hasVisible = editor.GetProperty("HasVisibleEditor", Any)?.GetGetMethod(true)
                    ?? throw new MissingMethodException(editor.FullName, "HasVisibleEditor");
                var harmony = new Harmony("minivivisection.grasshopperhiddeneditors");
                try
                {
                    harmony.Patch(beginEdit, prefix: new HarmonyMethod(typeof(GrasshopperHiddenEditors), nameof(BeginEditPrefix)));
                    harmony.Patch(hasVisible, postfix: new HarmonyMethod(typeof(GrasshopperHiddenEditors), nameof(HasVisibleEditorPostfix)));
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
            var codeEditor = scriptEditor?.GetType().GetProperty("CodeEditor", Any)?.GetValue(scriptEditor);
            return codeEditor?.GetType().GetField("_rce", Any)?.GetValue(codeEditor) as Window;
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
                if (!wanted || !ScriptRunCommand.CanHideNewEditors())
                    return;
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
                    ScriptRunCommand.OpenHidden(() => GrasshopperRuns.OpenEditor(component));
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
            Log.Info(LogArea.Breakpoints, "Grasshopper: '{0}' has its script editor, out of sight -- Ctrl+Alt+B or a breakpoint pauses it", name);
        }
    }
}
