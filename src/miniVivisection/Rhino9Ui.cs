#if RHINO9
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// The plug-in's tabs in Rhino 9. Rhino 9's Script Editor is a new program: Rhino 8's
    /// console tray, left panel and top bar are gone. The tabs -- V°Watch, V°Threads, V°Python,
    /// V°PowerShell, V°Claude and V°Log, each by its setting -- go into Rhino 9's console tray,
    /// after the editor's own (Rhino9Trays: no patch, an unknown ConsoleTray value). Where that
    /// tray can't be reached (another Rhino 9 build), each editor window gets a companion window
    /// instead -- "miniVivisection", owned by the editor -- which then also comes up by itself at
    /// the first pause of a debug run. A "V°" menu in the editor's menu bar shows the tabs and
    /// holds the Debug and AI switches, V°Help and V°Settings.
    /// </summary>
    internal static class Rhino9Ui
    {
        private sealed class Companion
        {
            public Window Editor;
            public ScriptEditorHook Hook;
            public Form Form;
            public TabControl Tabs;
            public PythonTerminal Python;
            public bool ShownOnce;
            public readonly Dictionary<string, TabPage> Pages = new Dictionary<string, TabPage>();
            // Rhino 9's console tray (null: not found -- the companion window then)
            public Panel Console;
            public Panel Container;
            public readonly Dictionary<string, TrayTabCommand> Commands = new Dictionary<string, TrayTabCommand>();
            public readonly Dictionary<string, Control> TrayTabs = new Dictionary<string, Control>();
            public readonly Dictionary<string, Panel> Hosts = new Dictionary<string, Panel>();
            public readonly Dictionary<string, Control> TrayContents = new Dictionary<string, Control>();
            public PythonTerminal TrayPython;
        }

        // Each tab's ConsoleTray value: negative, none of the editor's (Rhino9Trays).
        private const int FirstConsoleValue = -0x5600;

        private static string s_consoleProblem;

        private static readonly ConditionalWeakTable<Window, Companion> s_companions = new ConditionalWeakTable<Window, Companion>();
        private static readonly List<WeakReference<Companion>> s_all = new List<WeakReference<Companion>>();

        // The tabs, each by its setting, as in Rhino 8's console tray (ConsoleTabs) plus V Log.
        private static readonly (string Name, string Title, Func<bool> On, bool Ai)[] Definitions =
        {
            ("Watch", "V Watch", () => MiniVivisectionSettings.TabRhinoWatch, false),
            ("Threads", "V Threads", () => MiniVivisectionSettings.TabThreads, false),
            ("Python", "V Python", () => MiniVivisectionSettings.TabPython, false),
            ("PowerShell", "V PowerShell", () => MiniVivisectionSettings.TabPowerShell, false),
            ("Claude", "V Claude", () => MiniVivisectionSettings.TabClaude, true),
            ("Log", "V Log", () => true, false),
        };

        private static bool Shown((string Name, string Title, Func<bool> On, bool Ai) tab) =>
            tab.On() && (tab.Ai ? MiniVivisectionSettings.AiOn : MiniVivisectionSettings.PluginOn || tab.Name == "Log");

        /// <summary>For the health line (EditorHooks).</summary>
        internal static string HookProblem { get; private set; }

        /// <summary>Follows <paramref name="editor"/> (a Rhino 9 Script Editor window): its
        /// "V°" menu, and its companion window, made when first shown.</summary>
        public static void TryAdd(Window editor, ScriptEditorHook hook)
        {
            try
            {
                var companion = s_companions.GetValue(editor, e =>
                {
                    var made = new Companion { Editor = e, Hook = hook };
                    lock (s_all)
                        s_all.Add(new WeakReference<Companion>(made));
                    EditorWindows.WhenClosed(e, () => Close(made));
                    Log.Info(LogArea.Tabs, "Rhino 9: following the Script Editor '{0}' (its V° menu and companion window).", e.Title);
                    return made;
                });
                if (companion.Console == null && s_consoleProblem == null)
                    TryAddToConsole(companion);
                EnsureMenu(companion);
            }
            catch (Exception ex)
            {
                HookProblem = ex.Message;
                if (EditorHooks.FirstFailure(editor, "rhino9 ui"))
                    UserLog.Warning("Can't add miniVivisection to Rhino 9's Script Editor: {0}", ex.Message);
            }
        }

        /// <summary>The "V°" menu at the end of the editor's menu bar: shows the companion
        /// window and its tabs, and the Debug and AI switches. Put back when the editor rebuilds
        /// its menus.</summary>
        private static void EnsureMenu(Companion companion)
        {
            var menu = companion.Editor.Menu;
            if (menu == null)
                return;
            var title = "V" + VNames.Mark;
            if (menu.Items.OfType<ButtonMenuItem>().Any(i => i.Text == title))
                return;
            var item = new ButtonMenuItem { Text = title };
            if (companion.Console == null)
            {
                item.Items.Add(new ButtonMenuItem(new Command((s, e) => Show(companion, null)) { MenuText = "miniVivisection tabs" }));
                item.Items.Add(new SeparatorMenuItem());
            }
            foreach (var tab in Definitions)
            {
                var name = tab.Name;
                item.Items.Add(new ButtonMenuItem(new Command((s, e) => Show(companion, name)) { MenuText = VNames.Text(tab.Title) }));
            }
            item.Items.Add(new ButtonMenuItem(new Command((s, e) => _ = EditorExamples.ShowLessons(companion.Editor)) { MenuText = "Lessons in the Examples tab" }));
            item.Items.Add(new SeparatorMenuItem());
            var debug = new CheckMenuItem { Text = VNames.Text("V Debug"), Checked = MiniVivisectionSettings.PluginOn };
            debug.CheckedChanged += (s, e) => MiniVivisectionSettings.PluginOn = debug.Checked;
            var ai = new CheckMenuItem { Text = VNames.Text("V AI"), Checked = MiniVivisectionSettings.AiOn };
            ai.CheckedChanged += (s, e) => MiniVivisectionSettings.AiOn = ai.Checked;
            item.Items.Add(debug);
            item.Items.Add(ai);
            item.Items.Add(new SeparatorMenuItem());
            item.Items.Add(new ButtonMenuItem(new Command((s, e) => RhinoApp.RunScript("_miniVivisectionManual", false)) { MenuText = VNames.Text("V Help") }));
            item.Items.Add(new ButtonMenuItem(new Command((s, e) => RhinoApp.RunScript("_OptionsPage miniVivisection", false)) { MenuText = VNames.Text("V Settings") }));
            menu.Items.Add(item);
        }

        /// <summary>The companion window of <paramref name="editor"/> shown, with tab
        /// <paramref name="name"/> in front (Watch, Threads, Python, PowerShell, Claude, Log; null:
        /// as it was). False when the editor isn't followed.</summary>
        internal static bool Show(object editor, string name)
        {
            if (!(editor is Window window) || !s_companions.TryGetValue(window, out var companion))
                return false;
            Show(companion, name);
            return true;
        }

        private static void Show(Companion companion, string name)
        {
            if (companion.Console != null)
            {
                EnsureConsoleTabs(companion);
                if (companion.Commands.TryGetValue(name ?? "Watch", out var command))
                {
                    command.Select();
                    return;
                }
            }
            // the companion window (fallback)
            if (companion.Form == null)
                companion.Form = Make(companion);
            Ensure(companion);
            if (name != null && companion.Pages.TryGetValue(name, out var page))
                companion.Tabs.SelectedPage = page;
            if (!companion.Form.Visible)
                companion.Form.Show();
            companion.Form.BringToFront();
            companion.ShownOnce = true;
        }

        private static Form Make(Companion companion)
        {
            var tabs = new TabControl();
            companion.Tabs = tabs;
            var editor = companion.Editor;
            var bounds = editor.Bounds;
            var form = new Form
            {
                Title = "miniVivisection -- " + editor.Title,
                Owner = editor,
                Content = tabs,
                ClientSize = new Size(Math.Max(420, bounds.Width / 3), Math.Max(360, bounds.Height * 2 / 3)),
                Resizable = true,
                Minimizable = false,
                ShowInTaskbar = false,
            };
            form.Location = new Point(bounds.Right + 4, bounds.Top);
            form.Closing += (s, e) =>
            {
                // Closing only hides: the tabs keep their state (V Python's history, Claude's chat).
                if (companion.Editor.Visible)
                {
                    e.Cancel = true;
                    form.Visible = false;
                }
            };
            tabs.SelectedIndexChanged += (s, e) => Focus(tabs.SelectedPage?.Content);
            return form;
        }

        private static void Focus(Control content)
        {
            (content as PythonTerminal)?.FocusInput();
            (content as ClaudeChat)?.FocusInput();
            (content as RhinoWatchTab)?.FocusInput();
            (content as PowerShellTerminal)?.FocusInput();
        }

        /// <summary>The tabs that should show, made once; the others hidden.</summary>
        private static void Ensure(Companion companion)
        {
            if (companion.Tabs == null)
                return;
            foreach (var tab in Definitions)
            {
                var shown = Shown(tab);
                if (shown && !companion.Pages.ContainsKey(tab.Name))
                {
                    var page = new TabPage { Text = VNames.Text(tab.Title), Content = Content(companion, tab.Name) };
                    companion.Pages[tab.Name] = page;
                }
            }
            var wanted = Definitions.Where(Shown).Select(t => companion.Pages[t.Name]).ToList();
            var current = companion.Tabs.Pages.ToList();
            if (!current.SequenceEqual(wanted))
            {
                var selected = companion.Tabs.SelectedPage;
                companion.Tabs.Pages.Clear();
                foreach (var page in wanted)
                    companion.Tabs.Pages.Add(page);
                if (selected != null && wanted.Contains(selected))
                    companion.Tabs.SelectedPage = selected;
            }
        }

        private static Control Content(Companion companion, string name)
        {
            switch (name)
            {
                case "Watch":
                    return new RhinoWatchTab(companion.Editor, companion.Hook);
                case "Threads":
                    return new ThreadsView(companion.Editor, companion.Hook);
                case "Python":
                    return companion.Python = companion.Python ?? new PythonTerminal(companion.Editor, companion.Hook);
                case "PowerShell":
                    return new PowerShellTerminal(companion.Editor);
                case "Claude":
                    return new ClaudeChat(companion.Editor, companion.Hook);
                default:
                    return new UserLogView(companion.Editor);
            }
        }

        /// <summary>A tab's setting or the AI / Debug switch changed: every open companion
        /// window follows (UI thread).</summary>
        public static void ApplyVisibility()
        {
            foreach (var companion in All())
            {
                try
                {
                    Ensure(companion);
                    EnsureConsoleTabs(companion);
                    EnsureMenu(companion);
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.Tabs, "Rhino 9 tabs: {0}", Log.Describe(ex));
                }
            }
        }

        /// <summary>A pause in <paramref name="editor"/>: its companion window (the fallback, when
        /// the tabs aren't in the console tray) comes up the first time (later ones only if it was
        /// left open). Any thread.</summary>
        public static void AtPause(object editor)
        {
            if (!(editor is Window window) || !MiniVivisectionSettings.PluginOn)
                return;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                try
                {
                    if (s_companions.TryGetValue(window, out var companion) && companion.Console == null && !companion.ShownOnce)
                        Show(companion, "Watch");
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.Tabs, "Rhino 9 companion window: {0}", Log.Describe(ex));
                }
            }));
        }

        /// <summary>For V Python's status (ConsoleTabs.UpdateStatus): the Python tabs of every companion.</summary>
        internal static IEnumerable<PythonTerminal> PythonTerminals() =>
            All().SelectMany(c => new[] { c.Python, c.TrayPython }).Where(p => p != null);

        /// <summary>For the screenshots and tests: <paramref name="line"/> typed into the V Python tab.</summary>
        internal static bool TypeInPython(object editor, string line)
        {
            if (!(editor is Window window) || !s_companions.TryGetValue(window, out var companion))
                return false;
            Show(companion, "Python");
            var python = companion.Console != null ? companion.TrayPython : companion.Python;
            if (python == null)
                return false;
            python.Type(line);
            return true;
        }

        /// <summary>The tabs in the editor's console tray, after Terminal ... Help.</summary>
        private static void TryAddToConsole(Companion companion)
        {
            try
            {
                var tray = Rhino9Trays.Find(companion.Editor, "EditorConsoles");
                if (tray == null)
                    return; // not built yet: the next scan
                companion.Container = Rhino9Trays.ContainerOf(tray);
                companion.Console = tray;
                EnsureConsoleTabs(companion);
                EditorWindows.WhenClosed(companion.Editor, () =>
                {
                    foreach (var command in companion.Commands.Values)
                        command.Dispose(); // its bindings to the model
                });
                Log.Info(LogArea.Tabs, "Rhino 9: {0} tab(s) in the Script Editor's console tray.", companion.Commands.Count);
            }
            catch (Exception ex)
            {
                while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                companion.Console = null;
                s_consoleProblem = ex.Message; // the companion window for the session
                HookProblem = "console tray: " + ex.Message;
                UserLog.Warning("The V tabs stay in their own window (Rhino 9's console tray: {0}).", ex.Message);
            }
        }

        /// <summary>The console tabs that should show, made once; the others hidden.</summary>
        private static void EnsureConsoleTabs(Companion c)
        {
            if (c.Console == null)
                return;
            var model = Rhino9Editor.ModelOf(c.Editor) ?? throw new InvalidOperationException("no editor model");
            var changed = false;
            for (var i = 0; i < Definitions.Length; i++)
            {
                var tab = Definitions[i];
                var name = tab.Name;
                var shown = Shown(tab);
                if (shown && !c.Commands.ContainsKey(name))
                {
                    var title = VNames.Text(tab.Title);
                    var content = ConsoleContent(c, name);
                    c.TrayContents[name] = content;
                    c.Hosts[name] = new Panel { Content = content };
                    var trayTab = Rhino9Trays.CreateTab(c.Console);
                    var command = new TrayTabCommand(model, "CurrentConsole", "ShowConsole", FirstConsoleValue - i,
                        "minivivisection.console." + name.ToLowerInvariant(), title,
                        () => ShowHost(c, name), () => Focus(c.TrayContents[name]));
                    Rhino9Trays.SetMember(trayTab, "Command", command);
                    Rhino9Trays.SetMember(trayTab, "Title", title);
                    Rhino9Trays.SetEnumMember(trayTab, "TabStyle", 2); // TextOnly: no theme icon has our ids
                    Rhino9Trays.SetMember(trayTab, "HighlightColor", (Color?)Rhino9Trays.ThemeColor(c.Console, "TrayConsoleActiveTabColor", Colors.Transparent));
                    trayTab.BackgroundColor = Rhino9Trays.ThemeColor(c.Console, "TrayConsoleTabColor", trayTab.BackgroundColor);
                    c.Commands[name] = command;
                    c.TrayTabs[name] = trayTab;
                    changed = true;
                }
                if (c.TrayTabs.TryGetValue(name, out var existing))
                {
                    changed |= existing.Visible != shown;
                    existing.Visible = shown;
                    if (!shown && c.Commands[name].IsCurrent)
                        Rhino9Trays.SetTray(model, "CurrentConsole", Rhino9Trays.Terminal);
                }
            }
            if (changed && c.TrayTabs.Count > 0)
                Rhino9Trays.FitTabs(c.Console); // tabs added to a shown strip: the strip's height, so they paint
        }

        private static void ShowHost(Companion c, string name)
        {
            var host = c.Hosts[name];
            if (!ReferenceEquals(c.Container.Content, host))
                c.Container.Content = host;
        }

        /// <summary>The console tabs' own instances (not the companion window's; V Python kept
        /// in TrayPython).</summary>
        private static Control ConsoleContent(Companion c, string name) =>
            name == "Python" ? (c.TrayPython = c.TrayPython ?? new PythonTerminal(c.Editor, c.Hook)) : Content(c, name);

        /// <summary>For the tests: whether the tabs are in <paramref name="editor"/>'s console tray.</summary>
        internal static bool InConsole(object editor) =>
            editor is Window window && s_companions.TryGetValue(window, out var companion) && companion.Console != null;

        private static List<Companion> All()
        {
            lock (s_all)
            {
                s_all.RemoveAll(r => !r.TryGetTarget(out _));
                return s_all.Select(r => r.TryGetTarget(out var c) ? c : null).Where(c => c != null).ToList();
            }
        }

        private static void Close(Companion companion)
        {
            try
            {
                companion.Form?.Close();
                companion.Form?.Dispose();
                companion.Form = null;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "companion window closing: {0}", Log.Describe(ex));
            }
        }
    }
}
#endif
