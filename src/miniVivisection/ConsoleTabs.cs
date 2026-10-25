using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eto.Forms;
using HarmonyLib;
using Rhino;

namespace MiniVivisection
{
    /// <summary>
    /// Adds the plug-in's tabs -- "V Watch" (RhinoWatchTab), "Python" (PythonTerminal),
    /// "PowerShell" (PowerShellTerminal) and "Claude" (ClaudeChat) --
    /// to the Script Editor's console tray, next to Terminal, Variables and Watch. The
    /// tray is internal editor UI (RhinoCode 8.36, by reflection):
    /// - a tab: <c>ConsolePanel.TrayBar.AddTab(ConsoleTray, title, iconId)</c>, with a
    ///   ConsoleTray value the editor doesn't know;
    /// - its content: a second <c>TerminalTray</c> holding the plug-in's panel --
    ///   <c>ConsolePanel.ActiveTray</c> casts the shown content to the editor's
    ///   <c>BaseConsoleTray</c>, which this keeps working;
    /// - showing it: the editor switches content only for its own trays, so
    ///   <c>ConsolePanel.OnTrayBarChanged</c> is patched (Harmony) to show the panel
    ///   for these tabs and put the right content back for the others.
    /// </summary>
    internal static class ConsoleTabs
    {
        private const int PythonTray = 0x5059; // "PY": not one of the editor's own trays
        private const int ClaudeTray = 0x434C; // "CL"
        private const int RhinoWatchTray = 0x5257; // "RW"
        private const int PowerShellTray = 0x5053; // "PS"
        private const int ThreadsTray = 0x5448; // "TH"

        private static readonly ConditionalWeakTable<object, Dictionary<int, Panel>> s_hosts =
            new ConditionalWeakTable<object, Dictionary<int, Panel>>();
        private static readonly List<WeakReference<PythonTerminal>> s_terminals = new List<WeakReference<PythonTerminal>>();
        private static FieldInfo s_activeTray;
        private static MethodInfo s_setActiveTray;
        private static Harmony s_harmony;
        private static string s_error;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_error ?? "not installed";

        /// <summary>One editor's console and the plug-in's tabs in it.</summary>
        private sealed class Tabs
        {
            public object Editor;
            public object Console;
            public ScriptEditorHook Hook;
            public Dictionary<int, Panel> Hosts;
            public object TrayBar;
            public PythonTerminal Python;
            public readonly List<object> Items = new List<object>();
            public readonly Dictionary<object, int> TrayOf = new Dictionary<object, int>();
        }

        private static readonly List<WeakReference<Tabs>> s_consoles = new List<WeakReference<Tabs>>();

        // Each console's Tabs, kept alive as long as the console is: s_consoles only refers to
        // them weakly, and nothing else held them -- a garbage collection dropped them, and then
        // ApplyVisibility (a tab turned on or off in the settings) missed the open editors.
        private static readonly ConditionalWeakTable<object, Tabs> s_tabsOf = new ConditionalWeakTable<object, Tabs>();

        // The tabs, each by its setting (live: turned on, it's added to open editors).
        private static readonly (int Tray, string Title, Func<bool> On)[] Definitions =
        {
            (RhinoWatchTray, "V Watch", () => MiniVivisectionSettings.TabRhinoWatch),
            (ThreadsTray, "V Threads", () => MiniVivisectionSettings.TabThreads),
            (PythonTray, "V Python", () => MiniVivisectionSettings.TabPython),
            (PowerShellTray, "V PowerShell", () => MiniVivisectionSettings.TabPowerShell),
            (ClaudeTray, "V Claude", () => MiniVivisectionSettings.TabClaude),
        };

        /// <summary>Whether a tab shows now: its setting, and the AI switch for Claude,
        /// the Debug switch for the others.</summary>
        private static bool Shown(int tray) =>
            Definitions.First(d => d.Tray == tray).On() && (tray == ClaudeTray ? MiniVivisectionSettings.AiOn : MiniVivisectionSettings.PluginOn);

        /// <summary>Adds the tabs to <paramref name="editor"/> (an RCE window) once.</summary>
        public static void TryAdd(object editor, ScriptEditorHook hook)
        {
            if (s_error != null)
                return;
            try
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var console = editor.GetType().GetProperty("Console", any)?.GetValue(editor);
                if (console == null || s_hosts.TryGetValue(console, out _))
                    return;
                Patch(console.GetType());

                var tabs = new Tabs
                {
                    Editor = editor,
                    Console = console,
                    Hook = hook,
                    Hosts = new Dictionary<int, Panel>(),
                    TrayBar = console.GetType().GetProperty("TrayBar", any)?.GetValue(console)
                        ?? throw new MissingMemberException(console.GetType().FullName, "TrayBar"),
                };
                s_hosts.Add(console, tabs.Hosts);
                s_tabsOf.Add(console, tabs);
                lock (s_consoles)
                    s_consoles.Add(new WeakReference<Tabs>(tabs));
                Ensure(tabs);
                TabStrips.KeepLaidOut(tabs.TrayBar); // shown at once, not after a window resize
                Log.Info(LogArea.Tabs, "added {0} console tab(s) to the Script Editor.", tabs.Hosts.Count);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                // A member of the editor missing (another Rhino version): off for the session.
                // Anything else: this editor only, so other editors still try.
                if (ex is MissingMemberException || ex is TypeLoadException)
                    s_error = ex.Message;
                if (EditorHooks.FirstFailure(editor, "tabs"))
                    UserLog.Warning("Can't add the console tabs: {0}", ex.Message);
                else
                    Log.Debug(LogArea.Tabs, "console tabs, again: {0}", ex.Message); // retried at the next scan, said once
            }
        }

        /// <summary>The tabs that should show and aren't there yet, made and added.</summary>
        private static void Ensure(Tabs tabs)
        {
            foreach (var (tray, title, _) in Definitions)
            {
                if (Shown(tray) && !tabs.Hosts.ContainsKey(tray))
                    AddTab(tabs, tray, VNames.Text(title), Content(tabs, tray));
            }
        }

        private static Control Content(Tabs tabs, int tray)
        {
            switch (tray)
            {
                case RhinoWatchTray:
                    return new RhinoWatchTab(tabs.Editor, tabs.Hook);
                case PythonTray:
                    if (tabs.Python == null)
                    {
                        tabs.Python = new PythonTerminal(tabs.Editor, tabs.Hook);
                        lock (s_terminals)
                            s_terminals.Add(new WeakReference<PythonTerminal>(tabs.Python));
                    }
                    return tabs.Python;
                case PowerShellTray:
                    return new PowerShellTerminal(tabs.Editor);
                case ThreadsTray:
                    return new ThreadsView(tabs.Editor, tabs.Hook);
                default:
                    return new ClaudeChat(tabs.Editor, tabs.Hook);
            }
        }

        private static void AddTab(Tabs tabs, int trayValue, string title, Control content)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var editor = tabs.Editor;
            var console = tabs.Console;
            var trayBar = tabs.TrayBar;
            var addTab = trayBar.GetType().GetMethods().First(m => m.Name == "AddTab" && m.GetParameters().Length == 4);
            var tray = Enum.ToObject(addTab.GetParameters()[0].ParameterType, trayValue);
            var terminalCommand = console.GetType().GetField("_toggleTerminalTrayCommand", any).GetValue(console);
            var iconId = (string)terminalCommand.GetType().GetProperty("IconId", any).GetValue(terminalCommand);

            var terminalTray = console.GetType().GetProperty("Terminal", any).GetValue(console);
            var commands = editor.GetType().GetProperty("ActiveCommands", any).GetValue(editor);
            var options = editor.GetType().GetProperty("LaunchOptions", any).GetValue(editor);
            var host = (Panel)Activator.CreateInstance(terminalTray.GetType(), commands, options);
            host.Content = content;
            host.GetType().GetProperty("Title", any)?.SetValue(host, title);
            tabs.Hosts[trayValue] = host;
            addTab.Invoke(trayBar, new[] { tray, title, iconId, null });
            var tab = TabStrips.LastTab(trayBar);
            tabs.Items.Add(tab);
            tabs.TrayOf[tab] = trayValue;
        }

        /// <summary>For the screenshots and tests: the plug-in's console tab <paramref name="name"/>
        /// (Watch, Threads, Python, PowerShell, Claude) brought forward in <paramref name="editor"/>,
        /// as a click does. False when the editor or the tab isn't there.</summary>
        internal static bool Show(object editor, string name)
        {
            var tray = name == "Watch" ? RhinoWatchTray : name == "Threads" ? ThreadsTray : name == "Python" ? PythonTray
                : name == "PowerShell" ? PowerShellTray : name == "Claude" ? ClaudeTray : 0;
            lock (s_consoles)
            {
                foreach (var reference in s_consoles)
                {
                    if (!reference.TryGetTarget(out var tabs) || !ReferenceEquals(tabs.Editor, editor) || !tabs.Hosts.ContainsKey(tray))
                        continue;
                    var selected = tabs.TrayBar.GetType().GetProperty("SelectedTray", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var trayType = Nullable.GetUnderlyingType(selected.PropertyType) ?? selected.PropertyType;
                    selected.SetValue(tabs.TrayBar, Enum.ToObject(trayType, tray));
                    return true;
                }
            }
            return false;
        }

        /// <summary>For the screenshots and tests: <paramref name="line"/> typed into
        /// <paramref name="editor"/>'s V Python tab and run. False without that tab.</summary>
        internal static bool TypeInPython(object editor, string line)
        {
            lock (s_consoles)
            {
                foreach (var reference in s_consoles)
                {
                    if (reference.TryGetTarget(out var tabs) && ReferenceEquals(tabs.Editor, editor) && tabs.Python != null)
                    {
                        tabs.Python.Type(line);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>A tab's setting or the AI / Debug switch changed: in every open
        /// console, tabs turned on are added (once) and shown, the others hidden (UI
        /// thread).</summary>
        public static void ApplyVisibility()
        {
            lock (s_consoles)
            {
                s_consoles.RemoveAll(r => !r.TryGetTarget(out _));
                foreach (var reference in s_consoles)
                {
                    if (!reference.TryGetTarget(out var tabs))
                        continue;
                    try
                    {
                        Ensure(tabs);
                        TabStrips.SetVisible(tabs.TrayBar, tabs.Items, t => Shown(tabs.TrayOf[t]), "Terminal");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(LogArea.Tabs, "console tabs: {0}", Log.Describe(ex));
                    }
                }
            }
        }

        /// <summary>Paused/continued: the Python tabs say where their code runs.</summary>
        public static void UpdateStatus()
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                Python2Notice.UpdateAll(); // V Python and V Claude: an IronPython 2 script is being debugged
                lock (s_terminals)
                {
                    s_terminals.RemoveAll(r => !r.TryGetTarget(out _));
                    foreach (var reference in s_terminals)
                    {
                        if (reference.TryGetTarget(out var terminal))
                            terminal.UpdateStatus();
                    }
                }
            }));
        }

        private static void Patch(Type consolePanel)
        {
            if (s_harmony != null)
                return;
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var onTrayBarChanged = consolePanel.GetMethod("OnTrayBarChanged", any)
                ?? throw new MissingMethodException(consolePanel.FullName, "OnTrayBarChanged");
            s_activeTray = consolePanel.GetField("_activeTray", any)
                ?? throw new MissingFieldException(consolePanel.FullName, "_activeTray");
            s_setActiveTray = consolePanel.GetMethods(any).FirstOrDefault(m => m.Name == "SetActiveTray" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum)
                ?? throw new MissingMethodException(consolePanel.FullName, "SetActiveTray");
            var harmony = new Harmony("minivivisection.consoletabs");
            harmony.Patch(onTrayBarChanged, prefix: new HarmonyMethod(typeof(ConsoleTabs), nameof(OnTrayBarChangedPrefix)));
            s_harmony = harmony;
        }

        // Nothing may escape: an exception in a prefix would also stop the editor's own
        // switch -- its tabs wouldn't switch any more.
        private static bool OnTrayBarChangedPrefix(object __instance, object[] __args)
        {
            try
            {
                if (!s_hosts.TryGetValue(__instance, out var hosts))
                    return true;
                var activeTray = (Panel)s_activeTray.GetValue(__instance);
                var value = Convert.ToInt32(__args[0]);
                Log.Trace(LogArea.Tabs, "console tab {0}", value);
                if (hosts.TryGetValue(value, out var host))
                {
                    activeTray.Content = host;
                    (host.Content as PythonTerminal)?.FocusInput();
                    (host.Content as ClaudeChat)?.FocusInput();
                    (host.Content as RhinoWatchTab)?.FocusInput();
                    (host.Content as PowerShellTerminal)?.FocusInput();
                    return false;
                }
                // Leaving one of these tabs: the editor only switches content when its
                // state's active console changes, which it may not.
                if (hosts.ContainsValue(activeTray.Content as Panel))
                    s_setActiveTray.Invoke(__instance, new[] { __args[0] });
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                Log.Error(LogArea.Tabs, "console tab switch: {0} ({1})", ScriptEditorHook.FirstLine(ex), ex.GetType().Name);
            }
            return true;
        }
    }
}
