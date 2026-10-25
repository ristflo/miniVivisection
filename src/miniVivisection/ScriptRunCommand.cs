using System;
using System.Linq;
using System.Reflection;
using Eto.Forms;
using HarmonyLib;
using Rhino;
using Rhino.Commands;

namespace MiniVivisection
{
    /// <summary>
    /// Rhino's commands for running a script file run it plainly: no debugger, its breakpoints
    /// never pause, Break now can't pause it.
    /// - <c>RunPythonScript</c> (the IronPython plug-in, RhinoDLR_Python; a Python 3 script --
    ///   <c>#! python 3</c> -- it hands to RhinoCode in RunPython3): with the option
    ///   "RunPythonScript command" = Debug, a prefix on RunPython3 runs a Python 3 script in
    ///   the debugger as below instead. Scripts it runs in its own IronPython engine are left
    ///   alone (that engine has no debugger to stop).
    /// - <c>ScriptEditor</c> > Run (<c>_-ScriptEditor _Run "file"</c>, what toolbar buttons
    ///   use): its prompt gets a Debug=Off/On option (<c>_-ScriptEditor _Run _Debug=_On
    ///   "file"</c>), Off unless that option is Debug.
    /// Debug: the file opens in the Script Editor
    /// and runs there with the editor's Debug command (also without a breakpoint); the editor
    /// minimizes while it runs (Rhino's own way, kept here although "Keep the Script Editor
    /// open" is on) and comes back at a pause -- a breakpoint, or Break now (Ctrl+Alt+B) --
    /// with the line shown and everything the editor gives a paused script. Prefixes on
    /// RhinoCodePlugin's ScriptEditorCommand.RunScript (the prompt; Debug) and TryGetScript
    /// (a plain run: the script already asked for), patched when RhinoCodePlugin is loaded.
    /// </summary>
    internal static class ScriptRunCommand
    {
        private const string PluginAssembly = "RhinoCodePlugin";
        private static readonly Guid PluginId = new Guid("c9cba87a-23ce-4f15-a918-97645c05cde7"); // RhinoCodePlugin
        private const string IronPythonAssembly = "RhinoDLR_Python";
        private static Harmony s_runPythonHarmony;
        private const string CommandType = "RhinoCodePlugin.Commands.ScriptEditorCommand";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Harmony s_harmony;
        private static string s_error;
        private static bool s_listening;
        private static MethodInfo s_tryGetScript;
        private static MethodInfo s_fromText;
        private static MethodInfo s_fromPath;
        private static MethodInfo s_browse;
        private static MethodInfo s_openEditor;
        private static object s_pending; // a plain run's script, already asked for
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_minimizeOnce =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

        /// <summary>For the health line (EditorHooks): null when installed or not loaded yet.</summary>
        internal static string HookProblem => s_error;

        private static string s_runPythonError;

        /// <summary>For the health line: RunPythonScript's Debug / Stop and record (null: in place or not loaded yet).</summary>
        internal static string RunPythonHookProblem => s_runPythonError;

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
                    else if (name == IronPythonAssembly)
                        TryInstallRunPython(e.LoadedAssembly);
                };
            }
            var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == PluginAssembly);
            if (loaded != null)
                TryInstall(loaded);
            var ironPython = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == IronPythonAssembly);
            if (ironPython != null)
                TryInstallRunPython(ironPython);
        }

        // --- RunPythonScript -----------------------------------------------------------------

        private static void TryInstallRunPython(Assembly assembly)
        {
            if (s_runPythonHarmony != null)
                return;
            try
            {
                var type = assembly.GetTypes().FirstOrDefault(t => t.Name == "RunPythonScriptCommand")
                    ?? throw new TypeLoadException("RunPythonScriptCommand");
                var runPython3 = type.GetMethod("RunPython3", Any) ?? throw new MissingMethodException(type.FullName, "RunPython3");
                // The run's Task (Stop and record): RunPython3 discards it.
                var runScript = typeof(Rhino.Runtime.Code.RhinoCode).GetMethod("RunScriptAsyncMaybe",
                    new[] { typeof(Rhino.Runtime.Code.ICode), typeof(Rhino.Runtime.Code.Execution.RunContext) })
                    ?? throw new MissingMethodException("RhinoCode", "RunScriptAsyncMaybe(ICode, RunContext)");
                var harmony = new Harmony("minivivisection.runpythonscript");
                try
                {
                    harmony.Patch(runPython3, prefix: new HarmonyMethod(typeof(ScriptRunCommand), nameof(RunPython3Prefix)),
                        finalizer: new HarmonyMethod(typeof(ScriptRunCommand), nameof(RunPython3Finalizer)));
                    harmony.Patch(runScript, postfix: new HarmonyMethod(typeof(ScriptRunCommand), nameof(RunScriptAsyncPostfix)));
                }
                catch
                {
                    harmony.UnpatchAll(harmony.Id); // both or neither: a Begin without its Finish holds the hotkey
                    throw;
                }
                s_runPythonHarmony = harmony;
                Log.Info(LogArea.Runs, "RunPythonScript followed (RunPythonScriptCommand.RunPython3 patched; \"RunPythonScript command\": {0})", MiniVivisectionSettings.RunCommandDebug ? "Debug" : "Normal");
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                s_runPythonError = ex.Message;
                UserLog.Warning("RunPythonScript keeps running scripts plainly (the setting \"Debug\" can't apply): {0}", ex.Message);
            }
        }

        // A Python 3 script RunPythonScript would run plainly: in the Script Editor's debugger instead.
        private static bool RunPython3Prefix(object source, ref Result result, ref bool __result, out StoppedRuns.Run __state)
        {
            __state = null;
            try
            {
                var spec = (source as Rhino.Runtime.Code.Template)?.LanguageSpec
                    ?? source?.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(p => p.Name == "LanguageSpec")?.GetValue(source) as Rhino.Runtime.Code.Languages.LanguageSpec;
                var python3 = spec != null && Rhino.Runtime.Code.Languages.LanguageSpec.Python3.Matches(spec);
                var title = source?.GetType().GetProperty("Title")?.GetValue(source) as string ?? "the script";
                Log.Info(LogArea.Runs, "RunPythonScript: {0} ({1}, Python 3: {2}); \"RunPythonScript command\": {3}",
                    title, spec, python3, MiniVivisectionSettings.RunCommandDebug ? "Debug" : "Normal");
                if (MiniVivisectionSettings.PluginOn)
                    RhinoApp.WriteLine(RunHint(title, python3));
                if (python3 && MiniVivisectionSettings.RunCommandRecord)
                {
                    __state = StoppedRuns.Begin((source as Rhino.Runtime.Code.Template)?.Uri, title); // plainly, followed
                    return true;
                }
                if (!MiniVivisectionSettings.RunCommandDebug || !python3)
                    return true; // as RunPythonScript does it (not Python 3: its own IronPython engine)
                if (!DebugInEditor(source, "RunPythonScript"))
                    return true;
                result = Result.Success;
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                UserLog.Warning("RunPythonScript in the debugger failed ({0}): running it plainly.", Log.Describe(ex));
                return true;
            }
        }

        private static Exception RunPython3Finalizer(Exception __exception, StoppedRuns.Run __state)
        {
            StoppedRuns.AfterRunPython3(__state);
            return __exception;
        }

        private static void RunScriptAsyncPostfix(System.Threading.Tasks.Task<Rhino.Runtime.Code.Code> __result) => StoppedRuns.Capture(__result);

        /// <summary>The line RunPythonScript prints first: how the script runs, and what
        /// Break now can do with it.</summary>
        internal static string RunHint(string title, bool python3) => VNames.Text(RunHintText(title, python3));

        private static string RunHintText(string title, bool python3)
        {
            var key = MiniVivisectionSettings.BreakHotkey;
            var hotkey = key == "Off" ? "Break now (V Threads)" : key;
            if (!python3)
                return string.Format("miniVivisection: {0} runs as Python 2 (IronPython; no \"#! python 3\" first line) -- {1} can't pause it.", title, hotkey);
            if (MiniVivisectionSettings.RunCommandDebug)
                return string.Format("miniVivisection: {0} runs as Python 3 in the debugger -- {1} pauses it. For full speed, set V Settings > RunPythonScript command to Stop and record (or Normal).", title, hotkey);
            if (MiniVivisectionSettings.RunCommandRecord)
                return string.Format("miniVivisection: {0} runs as Python 3 at full speed -- {1} stops it; then the Script Editor shows where (an error too). To pause it instead, set V Settings > RunPythonScript command to Debug.", title, hotkey);
            return string.Format("miniVivisection: {0} runs as Python 3, without the debugger. To stop it with {1} and see where, set V Settings > RunPythonScript command to Stop and record (or Debug to pause it).", title, hotkey);
        }

        /// <summary>Opens <paramref name="script"/> (a Template) in the Script Editor and runs
        /// it there in the debugger, the editor minimized until it pauses; false: couldn't
        /// (the caller runs it as it would).</summary>
        internal static bool DebugInEditor(object script, string via)
        {
            if (s_openEditor == null)
            {
                // RhinoCodePlugin (the ScriptEditor command's) loads on demand
                Rhino.PlugIns.PlugIn.LoadPlugIn(PluginId);
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == PluginAssembly);
                if (loaded != null)
                    TryInstall(loaded);
            }
            if (s_openEditor == null)
            {
                UserLog.Warning("{0}: the Script Editor isn't available ({1}) -- running the script plainly.", via, s_error ?? "RhinoCodePlugin not loaded");
                return false;
            }
            object codeEditor = null;
            try
            {
                s_hideNewEditor = true; // OpenEditor creates and shows a new editor: invisible from the start
                try
                {
                    codeEditor = s_openEditor.Invoke(null, null);
                }
                finally
                {
                    s_hideNewEditor = false;
                }
                if (codeEditor == null)
                    return false;
                codeEditor.GetType().GetMethods(Any).First(m => m.Name == "AddCode" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType.IsInstanceOfType(script))
                    .Invoke(codeEditor, new object[] { script, false, false });
                var rce = codeEditor.GetType().GetField("_rce", Any)?.GetValue(codeEditor)
                    ?? throw new MissingFieldException("CodeEditor", "_rce");
                // Out of sight while it runs, unless it's open on screen already: cloaked (DWM)
                // from the moment its window exists -- a new editor's in BeginEdit, before it shows
                // -- so it shows nothing, keeps its own place and size, and loads normally;
                // uncloaked at a pause (Reveal). Minimizing flashed it, moving it off screen upset
                // a new editor's size, and WPF refuses a layered (transparent) style.
                var window = rce as Window;
                // Hidden as well: an editor on screen only because the last such run's pause
                // brought it up.
                var madeInvisible = window != null && s_invisible.TryGetValue(window, out _); // new: in BeginEdit
                var onScreen = window != null && !madeInvisible && IsOnScreen(window);
                var shownByPause = window != null && s_shownByPause.TryGetValue(window, out _);
                var hidden = window != null && (madeInvisible || !onScreen || shownByPause);
                Log.Info(LogArea.Tabs, "the editor {0}: new and invisible {1}, on screen {2}, shown by a pause {3}",
                    hidden ? "is kept out of sight" : "stays as it is", madeInvisible, onScreen, shownByPause);
                if (hidden && !madeInvisible)
                    MakeInvisible(window);
                codeEditor.GetType().GetMethod("Show", Any, null, Type.EmptyTypes, null)?.Invoke(codeEditor, null);
                DebugWhenReady(rce, script.GetType().GetProperty("Title")?.GetValue(script) as string ?? "the script", via,
                    script.GetType().GetProperty("Uri")?.GetValue(script) as Uri);
                return true;
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                UserLog.Warning("{0} in the debugger failed ({1}): running it plainly.", via, ex.Message);
                // The editor may be invisible already (BeginEdit, MakeInvisible): back on screen.
                try
                {
                    if (codeEditor?.GetType().GetField("_rce", Any)?.GetValue(codeEditor) is object editor)
                        Reveal(editor, paused: true);
                }
                catch (Exception revealing)
                {
                    Log.Warning(LogArea.Tabs, "showing the editor again: {0}", Log.Describe(revealing));
                }
                return false;
            }
        }

        private static void TryInstall(Assembly assembly)
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                var type = assembly.GetType(CommandType, throwOnError: true);
                var runScript = type.GetMethod("RunScript", Any) ?? throw new MissingMethodException(CommandType, "RunScript");
                s_tryGetScript = type.GetMethod("TryGetScript", Any) ?? throw new MissingMethodException(CommandType, "TryGetScript");
                s_fromText = type.GetMethod("TryGetScriptFromText", Any) ?? throw new MissingMethodException(CommandType, "TryGetScriptFromText");
                s_fromPath = type.GetMethod("TryGetScriptFromPath", Any) ?? throw new MissingMethodException(CommandType, "TryGetScriptFromPath");
                s_browse = type.GetMethod("TryBrowseScript", Any) ?? throw new MissingMethodException(CommandType, "TryBrowseScript");
                s_openEditor = type.GetMethod("OpenEditor", Any) ?? throw new MissingMethodException(CommandType, "OpenEditor");
                // A new editor shows itself in CodeEditor.BeginEdit (_rce.Show(), called by
                // OpenEditor right after creating it, before the editor's long first load):
                // the only point where its window can be made invisible before it's drawn.
                var beginEdit = s_openEditor.ReturnType.GetMethod("BeginEdit", Any, null, Type.EmptyTypes, null)
                    ?? throw new MissingMethodException(s_openEditor.ReturnType.FullName, "BeginEdit");
                var harmony = new Harmony("minivivisection.scriptruncommand");
                try
                {
                    harmony.Patch(runScript, prefix: new HarmonyMethod(typeof(ScriptRunCommand), nameof(RunScriptPrefix)));
                    harmony.Patch(s_tryGetScript, prefix: new HarmonyMethod(typeof(ScriptRunCommand), nameof(TryGetScriptPrefix)));
                    harmony.Patch(beginEdit, prefix: new HarmonyMethod(typeof(ScriptRunCommand), nameof(BeginEditPrefix)));
                }
                catch
                {
                    harmony.UnpatchAll(harmony.Id); // all or none
                    throw;
                }
                s_harmony = harmony;
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                s_error = ex.Message;
                UserLog.Warning("ScriptEditor Run keeps running scripts plainly (its Debug option and the setting \"Debug\" can't apply): {0}", ex.Message);
            }
        }

        /// <summary>The script to run, asked for as Rhino does (a path, "(code)", or Browse),
        /// with the Debug option; null: cancelled.</summary>
        private static object AskForScript(out bool debug)
        {
            var toggle = new Rhino.Input.Custom.OptionToggle(MiniVivisectionSettings.RunCommandDebug, "Off", "On");
            while (true)
            {
                var get = new Rhino.Input.Custom.GetString();
                get.SetCommandPrompt(Rhino.UI.Localization.LocalizeString("Path of script to run", 11));
                var browse = get.AddOption(Rhino.UI.Localization.LocalizeCommandOptionName("Browse", 10));
                get.AddOptionToggle("Debug", ref toggle);
                var result = get.GetLiteralString();
                debug = toggle.CurrentValue;
                object[] args;
                if (result == Rhino.Input.GetResult.Option)
                {
                    if (get.OptionIndex() != browse)
                        continue; // Debug toggled: ask again
                    args = new object[] { null };
                    return (bool)s_browse.Invoke(null, args) ? args[0] : null;
                }
                if (result != Rhino.Input.GetResult.String)
                    return null;
                var text = get.StringResult().Trim();
                args = new object[] { text, null };
                if ((bool)s_fromText.Invoke(null, args))
                    return args[1];
                args = new object[] { text, null };
                return (bool)s_fromPath.Invoke(null, args) ? args[1] : null;
            }
        }

        // A plain run: Rhino's RunScript asks for the script -- it was asked for already.
        private static bool TryGetScriptPrefix(object[] __args, ref bool __result)
        {
            var pending = s_pending;
            if (pending == null)
                return true;
            s_pending = null;
            __args[1] = pending;
            __result = true;
            return false;
        }

        /// <summary>For KeepEditorOpen: whether this editor's run was started by ScriptEditor
        /// Run with Debug, so it minimizes as Rhino's option says (asked once per run).</summary>
        internal static bool TakeMinimize(object rce) => rce != null && s_minimizeOnce.TryGetValue(rce, out _) && s_minimizeOnce.Remove(rce);

        private static bool RunScriptPrefix(ref Result __result)
        {
            if (!MiniVivisectionSettings.PluginOn)
                return true; // as Rhino does it
            object script;
            bool debug;
            try
            {
                script = AskForScript(out debug);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                UserLog.Warning("ScriptEditor Run: asking for the script failed ({0}) -- Rhino asks again.", ex.Message);
                return true;
            }
            if (script == null)
            {
                __result = Result.Cancel;
                return false;
            }
            if (debug && DebugInEditor(script, "ScriptEditor Run"))
            {
                __result = Result.Success;
                return false;
            }
            s_pending = script; // Rhino's RunScript runs it plainly (TryGetScriptPrefix)
            return true;
        }

        // An editor kept invisible (cloaked): -1 until its window is cloaked, 0 after.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, System.Runtime.CompilerServices.StrongBox<long>> s_invisible =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Window, System.Runtime.CompilerServices.StrongBox<long>>();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

        private static bool s_hideNewEditor; // set around OpenEditor by DebugInEditor (UI thread)
        private static bool s_standingNew;   // with it: a Grasshopper component's hidden editor (OpenHidden)

        // Editors kept invisible until a pause shows them, or the user opens them (Grasshopper's
        // hidden editors): not tied to a run -- not shown at its end, nor after four hours.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> s_standing =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Window, object>();

        /// <summary>Whether a new editor can be kept invisible: CodeEditor.BeginEdit patched
        /// (with RhinoCodePlugin, loaded here if it isn't yet). For Grasshopper's hidden editors.</summary>
        internal static bool CanHideNewEditors()
        {
            if (s_harmony == null && s_error == null)
            {
                Rhino.PlugIns.PlugIn.LoadPlugIn(PluginId);
                var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == PluginAssembly);
                if (loaded != null)
                    TryInstall(loaded);
            }
            return s_harmony != null;
        }

        /// <summary>A new editor made by <paramref name="open"/> is invisible from the start, without
        /// taking the focus, and stays so until a pause or <see cref="Reveal"/> (Grasshopper's
        /// hidden editors). UI thread.</summary>
        internal static void OpenHidden(Action open)
        {
            s_hideNewEditor = true;
            s_standingNew = true;
            try
            {
                open();
            }
            finally
            {
                s_hideNewEditor = false;
                s_standingNew = false;
            }
        }

        /// <summary>An editor that exists but isn't on screen (closed by the user: hidden), kept
        /// invisible from now on, as <see cref="OpenHidden"/>'s.</summary>
        internal static void HideStanding(Window window)
        {
            s_standing.AddOrUpdate(window, null);
            MakeInvisible(window);
        }

        /// <summary>A hidden editor closed -- hidden, as its close button does -- and no longer kept
        /// invisible: shown as usual when it's opened again.</summary>
        internal static void CloseStanding(Window window)
        {
            window.Close(); // HideOnClose: the editor stays loaded, hidden
            s_standing.Remove(window);
            if (s_invisible.TryGetValue(window, out _))
            {
                s_invisible.Remove(window);
                var handle = HandleOf(window);
                if (handle != IntPtr.Zero)
                {
                    SetCloak(handle, false);
                    SetOutOfTaskbar(handle, false);
                }
            }
        }

        /// <summary>Whether <paramref name="window"/> is a hidden editor of <see cref="OpenHidden"/> /
        /// <see cref="HideStanding"/>, still invisible.</summary>
        internal static bool IsOutOfTaskbar(object window) =>
            window is Window w && HandleOf(w) is IntPtr handle && handle != IntPtr.Zero && IsOutOfTaskbar(handle);

        internal static bool IsStanding(object window) => window is Window w && s_standing.TryGetValue(w, out _) && s_invisible.TryGetValue(w, out _);

        // CodeEditor.BeginEdit: a new editor is about to show itself -- invisible, when a
        // debug run of RunPythonScript / ScriptEditor Run creates it.
        private static void BeginEditPrefix(object __instance)
        {
            try
            {
                if (!s_hideNewEditor)
                    return;
                // Its Win32 window may exist already (made before it's shown): not on screen is
                // what counts -- made invisible now, or the moment Windows makes it.
                if (__instance.GetType().GetField("_rce", Any)?.GetValue(__instance) is Window window && !IsOnScreen(window))
                {
                    if (s_standingNew)
                    {
                        s_standing.AddOrUpdate(window, null);
                        // Shown without taking the focus from Grasshopper's canvas.
                        var wpf = (window as Control)?.ControlObject;
                        wpf?.GetType().GetProperty("ShowActivated")?.SetValue(wpf, false);
                    }
                    MakeInvisible(window);
                    Log.Info(LogArea.Tabs, "a new editor is made invisible before it shows{0}", s_standingNew ? " (a Grasshopper component's hidden editor)" : "");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Tabs, "a new editor kept invisible: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Whether the editor is on screen now: its WPF window shown (IsVisible -- Eto's
        /// Visible is true for a window never shown yet) and not minimized.</summary>
        private static bool IsOnScreen(Window window)
        {
            try
            {
                if (window.WindowState == WindowState.Minimized || HandleOf(window) == IntPtr.Zero)
                    return false;
                var wpf = (window as Control)?.ControlObject;
                return wpf?.GetType().GetProperty("IsVisible")?.GetValue(wpf) as bool? ?? window.Visible;
            }
            catch (Exception)
            {
                return window.Visible;
            }
        }

        /// <summary>The editor's Win32 window (its WPF window's), or zero before it exists.</summary>
        private static IntPtr HandleOf(Window window)
        {
            var wpf = (window as Control)?.ControlObject;
            var type = wpf?.GetType();
            while (type != null && type.FullName != "System.Windows.Window")
                type = type.BaseType; // Eto's window derives from WPF's
            var helper = type?.Assembly.GetType("System.Windows.Interop.WindowInteropHelper");
            if (helper == null)
                return IntPtr.Zero;
            var instance = Activator.CreateInstance(helper, wpf);
            return helper.GetProperty("Handle")?.GetValue(instance) is IntPtr handle ? handle : IntPtr.Zero;
        }

        /// <summary>The editor invisible -- cloaked (DWM) -- now if its
        /// window exists, else the moment Windows makes it (WPF's SourceInitialized: before
        /// anything is drawn).</summary>
        private static void MakeInvisible(Window window)
        {
            try
            {
                var box = new System.Runtime.CompilerServices.StrongBox<long>(-1);
                s_invisible.AddOrUpdate(window, box);
                KeepInvisible(window);
                var handle = HandleOf(window);
                Log.Info(LogArea.Tabs, "the editor is kept invisible while the script runs ({0})", handle == IntPtr.Zero ? "its window is made later" : "its window exists");
                if (handle != IntPtr.Zero)
                {
                    ApplyInvisible(handle, box);
                    return;
                }
                var wpf = (window as Control)?.ControlObject;
                var initialized = wpf?.GetType().GetEvent("SourceInitialized");
                if (initialized == null)
                    return;
                EventHandler onCreated = null;
                onCreated = (sender, e) =>
                {
                    initialized.RemoveEventHandler(wpf, onCreated);
                    if (s_invisible.TryGetValue(window, out var pending) && pending.Value == -1)
                        ApplyInvisible(HandleOf(window), pending);
                };
                initialized.AddEventHandler(wpf, onCreated);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Tabs, "the editor couldn't be made invisible: {0}", Log.Describe(ex));
            }
        }

        // DWM cloaking: Windows' compositor doesn't show the window; for WPF and the editor it
        // stays a normal, visible window that draws and loads (a layered style, the first try,
        // is refused by WPF's windows).
        private const int DwmwaCloak = 13, DwmwaCloaked = 14;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        private static bool IsCloaked(IntPtr handle) => DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, 4) == 0 && cloaked != 0;

        private static void SetCloak(IntPtr handle, bool cloak)
        {
            var value = cloak ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaCloak, ref value, 4);
        }

        private static void ApplyInvisible(IntPtr handle, System.Runtime.CompilerServices.StrongBox<long> box)
        {
            if (handle == IntPtr.Zero)
                return;
            box.Value = 0; // applied
            SetCloak(handle, true);
            SetOutOfTaskbar(handle, true);
            Log.Info(LogArea.Tabs, "editor invisible (cloaked): window {0}, cloaked now {1}, out of the taskbar {2}", handle.ToInt64(), IsCloaked(handle), IsOutOfTaskbar(handle));
        }

        // Out of the taskbar and Alt+Tab while invisible: a cloaked window still has its button
        // there. The tool window style takes it out; the window's own style is back when shown.
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x80, WsExAppWindow = 0x40000;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, long> s_ownStyles =
            new System.Collections.Concurrent.ConcurrentDictionary<IntPtr, long>();

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private static bool IsOutOfTaskbar(IntPtr handle) => (GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExToolWindow) != 0;

        private static void SetOutOfTaskbar(IntPtr handle, bool hide)
        {
            try
            {
                var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
                long wanted;
                if (hide)
                {
                    s_ownStyles.TryAdd(handle, style); // the first time: the window's own
                    wanted = (style | WsExToolWindow) & ~WsExAppWindow;
                }
                else
                {
                    if (!s_ownStyles.TryRemove(handle, out var own))
                        return; // never taken out
                    wanted = (style & ~(WsExToolWindow | WsExAppWindow)) | (own & (WsExToolWindow | WsExAppWindow));
                }
                if (wanted == style)
                    return;
                SetWindowLongPtr(handle, GwlExStyle, new IntPtr(wanted));
                // NOMOVE | NOSIZE | NOZORDER | NOACTIVATE | FRAMECHANGED: the shell sees the new style
                SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x2 | 0x1 | 0x4 | 0x10 | 0x20);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "editor in the taskbar: {0}", Log.Describe(ex));
            }
        }

        private static UITimer s_keepInvisible;
        private static int s_reapplied;
        private static DateTime s_keepSince;

        /// <summary>While editors are to stay invisible: their windows checked ten times a
        /// second -- WPF sets its window's style again when it shows, restores or activates it,
        /// which takes the cloaking away -- and made invisible again. Every such editor, not
        /// only the first; stops when none is left. After four hours an editor still kept
        /// invisible (a run that never paused nor ended) is shown again.</summary>
        private static void KeepInvisible(Window window)
        {
            s_keepSince = DateTime.Now;
            if (s_keepInvisible != null)
                return;
            s_reapplied = 0;
            s_keepInvisible = new UITimer { Interval = 0.1 };
            s_keepInvisible.Elapsed += (sender, e) =>
            {
                try
                {
                    var invisible = s_invisible.Select(p => p.Key).ToList();
                    if (DateTime.Now - s_keepSince > TimeSpan.FromHours(4))
                    {
                        // A run's editor is never left invisible for good; Grasshopper's hidden
                        // editors are meant to wait.
                        foreach (var left in invisible.Where(w => !s_standing.TryGetValue(w, out _)).ToList())
                            Reveal(left, paused: true);
                        invisible = s_invisible.Select(p => p.Key).ToList();
                    }
                    if (invisible.Count == 0)
                    {
                        s_keepInvisible.Stop();
                        s_keepInvisible = null;
                        if (s_reapplied > 0)
                            Log.Info(LogArea.Tabs, "the editor was kept invisible: cloaked again {0} time(s)", s_reapplied);
                        return;
                    }
                    foreach (var each in invisible)
                    {
                        if (!s_invisible.TryGetValue(each, out var box))
                            continue;
                        var handle = HandleOf(each);
                        if (handle == IntPtr.Zero)
                            continue; // not made yet
                        if (box.Value < 0 || !IsCloaked(handle) || !IsOutOfTaskbar(handle))
                        {
                            ApplyInvisible(handle, box);
                            s_reapplied++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Tabs, "keeping the editor invisible: {0}", Log.Describe(ex));
                }
            };
            s_keepInvisible.Start();
        }

        /// <summary>Whether <paramref name="window"/> is an editor kept invisible now (cloaked:
        /// Eto still calls it visible).</summary>
        internal static bool IsKeptInvisible(object window) => window is Window w && s_invisible.TryGetValue(w, out _);

        /// <summary>A pause, or the run's end: an editor kept invisible is visible again -- in
        /// front at a pause, minimized at the end (UI thread).</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> s_shownByPause =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Window, object>();

        // Editors whose debug run DebugWhenReady started: a run's end reveals only these.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> s_debugStarted =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Window, object>();

        internal static void Reveal(object editor, bool paused)
        {
            if (!(editor is Window window) || !s_invisible.TryGetValue(window, out var box))
                return;
            if (!paused && !s_debugStarted.TryGetValue(window, out _))
                return; // a run ended, but not this editor's: its debug run hasn't started yet
            s_debugStarted.Remove(window);
            s_invisible.Remove(window);
            var wasStanding = s_standing.TryGetValue(window, out _);
            s_standing.Remove(window);
            if (paused)
                s_shownByPause.AddOrUpdate(window, null); // the next such run hides it again
            else
                s_shownByPause.Remove(window);
            try
            {
                if (!paused)
                    window.WindowState = WindowState.Minimized;
                var handle = HandleOf(window);
                if (handle != IntPtr.Zero)
                {
                    SetCloak(handle, false);
                    SetOutOfTaskbar(handle, false); // in the taskbar and Alt+Tab again
                    RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x80 | 0x100); // INVALIDATE | ALLCHILDREN | UPDATENOW
                    Log.Info(LogArea.Tabs, "editor visible again (uncloaked): window {0}, cloaked now {1}", handle.ToInt64(), IsCloaked(handle));
                }
                if (paused)
                {
                    window.WindowState = WindowState.Normal;
                    window.BringToFront();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "editor visible again: {0}", Log.Describe(ex));
            }
            if (wasStanding)
                StandingRevealed?.Invoke(window);
        }

        /// <summary>A hidden editor of <see cref="OpenHidden"/> / <see cref="HideStanding"/> is
        /// visible now (UI thread).</summary>
        internal static event Action<Window> StandingRevealed;

        /// <summary>Whether <paramref name="script"/> is the editor's active tab (true when its
        /// file isn't known: then as before).</summary>
        private static bool IsActive(object rce, Uri script)
        {
            if (script == null)
                return true;
            var state = rce.GetType().GetProperty("ActiveState", Any)?.GetValue(rce);
            var active = state?.GetType().GetProperty("ActiveCode", Any)?.GetValue(state);
            var code = active?.GetType().GetProperty("Code", Any)?.GetValue(active) as Rhino.Runtime.Code.Code;
            if (code?.Uri == null)
                return false;
            return code.Uri == script || (code.Uri.IsFile && script.IsFile && string.Equals(code.Uri.LocalPath, script.LocalPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The editor's Debug command once the script is the editor's active one (the
        /// editor opens it asynchronously): tried for a few seconds.</summary>
        private static void DebugWhenReady(object rce, string title, string via, Uri script)
        {
            DebugRunButton.ForceDebug(rce); // Debug without a breakpoint, whatever "Run and Debug buttons" says
            var commands = rce.GetType().GetProperty("ActiveCommands", Any)?.GetValue(rce);
            var debug = commands?.GetType().GetMethod("Find", Any, null, new[] { typeof(string) }, null)?.Invoke(commands, new object[] { "editor.debugCode" }) as Eto.Forms.Command;
            if (debug == null)
            {
                UserLog.Warning("{0}: the editor's Debug command isn't there -- {1} is open in the Script Editor; use Debug there.", via, title);
                DebugRunButton.EndForcedDebug(rce);
                Reveal(rce, paused: true);
                return;
            }
            var update = debug.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "UpdateState" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(rce));
            var started = DateTime.Now;
            var timer = new UITimer { Interval = 0.2 };
            timer.Elapsed += (sender, e) =>
            {
                try
                {
                    update?.Invoke(debug, new[] { rce }); // its state now: Enabled once the script is active and idle
                    // The editor opens the script asynchronously: Debug only once it's the active
                    // tab -- before that, Debug would run whatever tab was active.
                    if (debug.Enabled && IsActive(rce, script))
                    {
                        DebugRunButton.EndForcedDebug(rce);
                        timer.Stop();
                        s_minimizeOnce.AddOrUpdate(rce, null); // out of the way until it pauses
                        if (rce is Window started)
                            s_debugStarted.AddOrUpdate(started, null);
                        debug.Execute();
                        UserLog.Info("{0}: {1} runs in the debugger, the Script Editor out of sight until it pauses (Ctrl+Alt+B, a breakpoint).", via, title);
                    }
                    else if (DateTime.Now - started > TimeSpan.FromSeconds(15)) // a minimized editor may load slower
                    {
                        timer.Stop();
                        UserLog.Warning("{0}: {1} is open in the Script Editor, but couldn't be started in the debugger -- use Debug there.", via, title);
                        DebugRunButton.EndForcedDebug(rce);
                        Reveal(rce, paused: true);
                    }
                }
                catch (Exception ex)
                {
                    timer.Stop();
                    UserLog.Warning("{0} in the debugger: {1}", via, Log.Describe(ex));
                    DebugRunButton.EndForcedDebug(rce);
                    Reveal(rce, paused: true); // never left invisible
                }
            };
            timer.Start();
        }
    }
}
