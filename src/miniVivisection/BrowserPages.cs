using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eto.Drawing;
using Eto.Forms;
using HarmonyLib;

namespace MiniVivisection
{
    /// <summary>
    /// Adds the plug-in's pages -- Claude, V Watch, PowerShell -- to the Script
    /// Editor's left panel, next to Explorer, Search and Help: an icon in its strip and
    /// a page with the editor's own header. They are instances of their own, next to
    /// the console tray's tabs. The panel is internal editor UI (RhinoCode 8.36, by
    /// reflection), handled like the console tray (ConsoleTabs):
    /// - a page's icon: <c>BrowserPanel.TrayBar.AddTab(BrowsersTray, title, iconId)</c>
    ///   with a BrowsersTray value the editor doesn't know, and the plug-in's own icon;
    /// - its content: a <c>HelpCardTray</c> -- a header and a container, nothing else --
    ///   holding the page;
    /// - showing it: <c>BrowserPanel.OnTrayBarChanged</c> ignores unknown values and is
    ///   patched (Harmony) to show the page, unfold the panel, and put the editor's own
    ///   page back when one of its icons is clicked. The editor's state never holds the
    ///   unknown value, so its saved layout stays valid.
    /// <c>MiniVivisection LeftPanelPages=Off</c> leaves them out of Script Editors
    /// opened afterwards.
    ///
    /// The compact layout (the editor's "compact browser" option; the Grasshopper
    /// components' editors have it): the strip is hidden, and the status bar at the bottom
    /// gets a small button per page instead (<c>StatusPanel.CreateCompactTray</c>, the
    /// editor's pages only). A postfix there adds a button per page of the plug-in, after a
    /// separator; a click shows the page as its icon in the strip does. The editor's own
    /// buttons toggle -- their page already "active" in the editor's state, they would fold
    /// the panel away instead of showing it -- so a prefix on
    /// <c>BrowserTrayToggleCommand.ExecuteWith</c> puts the editor's page back when one of
    /// these shows.
    /// </summary>
    internal static class BrowserPages
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const int FirstTrayValue = 0x5700; // not one of the editor's BrowsersTray values
        private const int SettingsTrayValue = FirstTrayValue + 4;

        private sealed class Pages
        {
            public object Editor;
            public ScriptEditorHook Hook;
            public readonly Dictionary<int, Panel> Hosts = new Dictionary<int, Panel>();
            public readonly Dictionary<int, Control> Contents = new Dictionary<int, Control>();
            public readonly List<object> Tabs = new List<object>();
            public readonly Dictionary<object, int> TrayOf = new Dictionary<object, int>();
            public object TrayBar;
            public object Browser;
        }

        // The pages, each by its setting (live: turned on, it's added to open editors);
        // a page keeps its tray value whichever others are on.
        private static readonly (int Value, string Title, Func<bool> On, Action<Graphics> Icon)[] Definitions =
        {
            (FirstTrayValue, "V Claude", () => MiniVivisectionSettings.PageClaude, DrawChat),
            (FirstTrayValue + 1, "V Watch", () => MiniVivisectionSettings.PageRhinoWatch, DrawEye),
            (FirstTrayValue + 5, "V Threads", () => MiniVivisectionSettings.PageThreads, DrawThreads),
            (FirstTrayValue + 2, "V PowerShell", () => MiniVivisectionSettings.PagePowerShell, DrawPrompt),
            (FirstTrayValue + 3, "V Log", () => MiniVivisectionSettings.PageLog, DrawLog),
            (SettingsTrayValue, "V Settings", () => MiniVivisectionSettings.PageSettings, DrawGear),
        };

        /// <summary>Whether a page shows now: its setting, and the AI switch for Claude,
        /// none for Settings (it can turn the switches back on), Debug for the others.</summary>
        private static bool Shown(int value) =>
            Definitions.First(d => d.Value == value).On() &&
            (value == SettingsTrayValue || (value == FirstTrayValue ? MiniVivisectionSettings.AiOn : MiniVivisectionSettings.PluginOn));

        private static Control Content(Pages pages, int value)
        {
            switch (value)
            {
                case FirstTrayValue:
                    return new ClaudeChat(pages.Editor, pages.Hook);
                case FirstTrayValue + 1:
                    return new RhinoWatchTab(pages.Editor, pages.Hook, narrow: true);
                case FirstTrayValue + 2:
                    return new PowerShellTerminal(pages.Editor);
                case FirstTrayValue + 3:
                    return new UserLogView(pages.Editor);
                case FirstTrayValue + 5:
                    return new ThreadsView(pages.Editor, pages.Hook, narrow: true);
                default:
                    return new SettingsView(immediate: true, narrow: true).Control;
            }
        }

        private static readonly List<WeakReference<Pages>> s_all = new List<WeakReference<Pages>>();

        /// <summary>A page's setting or the AI / Debug switch changed: in every open left
        /// panel, pages turned on are added (once) and shown, the others hidden (UI thread).</summary>
        public static void ApplyVisibility()
        {
            lock (s_all)
            {
                s_all.RemoveAll(r => !r.TryGetTarget(out _));
                foreach (var reference in s_all)
                {
                    if (!reference.TryGetTarget(out var pages))
                        continue;
                    try
                    {
                        Ensure(pages);
                        TabStrips.SetVisible(pages.TrayBar, pages.Tabs, t => Shown(pages.TrayOf[t]), "Explorer");
                        EnsureStatusButtons(pages);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(LogArea.Tabs, "left panel pages: {0}", Log.Describe(ex));
                    }
                }
            }
        }

        private static readonly ConditionalWeakTable<object, Pages> s_pages = new ConditionalWeakTable<object, Pages>();
        private static FieldInfo s_activeTray;
        private static MethodInfo s_setActiveTray;
        private static Harmony s_harmony;
        private static string s_error;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_error ?? "not installed";

        /// <summary>For the health line: the compact layout's buttons (null: in place or not tried yet).</summary>
        internal static string CompactHookProblem => s_compactFailed ? "its members changed" : null;

        /// <summary>Adds the pages to <paramref name="editor"/> (an RCE window) once.</summary>
        public static void TryAdd(object editor, ScriptEditorHook hook)
        {
            if (s_error != null)
                return;
            try
            {
                var browser = editor.GetType().GetProperty("Browser", Any)?.GetValue(editor);
                if (browser == null || s_pages.TryGetValue(browser, out _))
                    return;
                Patch(browser.GetType());
                var trayBar = browser.GetType().GetProperty("TrayBar", Any)?.GetValue(browser)
                    ?? throw new MissingMemberException(browser.GetType().FullName, "TrayBar");
                var pages = new Pages { Editor = editor, Hook = hook, Browser = browser, TrayBar = trayBar };
                s_pages.Add(browser, pages);
                lock (s_all)
                    s_all.Add(new WeakReference<Pages>(pages));
                Ensure(pages);
                PatchCompact(editor);
                EnsureStatusButtons(pages);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                // A member of the editor missing (another Rhino version): off for the session.
                // Anything else: this editor only, so other editors still try.
                if (ex is MissingMemberException || ex is TypeLoadException)
                    s_error = ex.Message;
                if (EditorHooks.FirstFailure(editor, "pages"))
                    UserLog.Warning("Can't add pages to the left panel: {0}", ex.Message);
                else
                    Log.Debug(LogArea.Tabs, "left panel pages, again: {0}", ex.Message); // retried at the next scan, said once
            }
        }

        /// <summary>The pages that should show and aren't there yet, made and added.</summary>
        private static void Ensure(Pages pages)
        {
            var added = new List<string>();
            var trayBar = pages.TrayBar;
            var trayEnum = trayBar.GetType().GetProperty("SelectedTray", Any).PropertyType;
            trayEnum = Nullable.GetUnderlyingType(trayEnum) ?? trayEnum;
            var addTab = trayBar.GetType().GetMethod("AddTab", Any, null, new[] { trayEnum, typeof(string), typeof(string) }, null)
                ?? throw new MissingMethodException(trayBar.GetType().FullName, "AddTab");
            foreach (var (value, title, _, draw) in Definitions)
            {
                if (!Shown(value) || pages.Hosts.ContainsKey(value))
                    continue;
                var shown = VNames.Text(title); // V°Watch
                addTab.Invoke(trayBar, new[] { Enum.ToObject(trayEnum, value), shown, "" });
                var tab = TabStrips.LastTab(trayBar);
                pages.Tabs.Add(tab);
                pages.TrayOf[tab] = value;
                // No theme icon has an empty id, so the theme leaves these in place.
                var icon = DrawnIcon(draw);
                FindField(tab.GetType(), "m_icon").SetValue(tab, icon);
                FindField(tab.GetType(), "m_highIcon").SetValue(tab, icon);
                var content = Content(pages, value);
                pages.Hosts[value] = CreateHost(pages.Editor, shown, content);
                pages.Contents[value] = content;
                added.Add(shown);
            }
            if (added.Count == 0)
                return;
            trayBar.GetType().GetMethod("UpdateTheme", Any, null, Type.EmptyTypes, null)?.Invoke(trayBar, null);
            // The strip was drawn before these tabs had icons: repaint it now and once
            // laid out, or they only show after the mouse passes over it.
            TabStrips.KeepLaidOut(trayBar); // laid out again on resize, now and once settled
            Log.Info(LogArea.Tabs, "added {0} to the Script Editor's left panel.", string.Join(", ", added));
        }

        /// <summary>A <c>HelpCardTray</c>: the editor's header with <paramref name="title"/>
        /// above <paramref name="content"/>, in the editor's colors.</summary>
        private static Panel CreateHost(object editor, string title, Control content)
        {
            var console = editor.GetType().GetProperty("Console", Any).GetValue(editor);
            var helpCardType = console.GetType().Assembly.GetType("RhinoCodeEditor.Editor.Panels.Consoles.HelpCardTray", throwOnError: true);
            var commands = editor.GetType().GetProperty("ActiveCommands", Any).GetValue(editor);
            var options = editor.GetType().GetProperty("LaunchOptions", Any).GetValue(editor);
            var host = (Panel)Activator.CreateInstance(helpCardType, commands, options);
            helpCardType.GetProperty("Title", Any).SetValue(host, title);
            ((Panel)FindField(helpCardType, "m_container").GetValue(host)).Content = content;
            helpCardType.GetMethod("UpdateStyle", Any)?.Invoke(host, new[] { editor });
            return host;
        }

        private static void Patch(Type browserPanel)
        {
            if (s_harmony != null)
                return;
            var onTrayBarChanged = browserPanel.GetMethod("OnTrayBarChanged", Any)
                ?? throw new MissingMethodException(browserPanel.FullName, "OnTrayBarChanged");
            s_activeTray = browserPanel.GetField("_activeTray", Any)
                ?? throw new MissingFieldException(browserPanel.FullName, "_activeTray");
            s_setActiveTray = browserPanel.GetMethods(Any).FirstOrDefault(m => m.Name == "SetActiveTray" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum)
                ?? throw new MissingMethodException(browserPanel.FullName, "SetActiveTray");
            var harmony = new Harmony("minivivisection.browserpages");
            harmony.Patch(onTrayBarChanged, prefix: new HarmonyMethod(typeof(BrowserPages), nameof(OnTrayBarChangedPrefix)));
            s_harmony = harmony;
        }

        // Nothing may escape: an exception in a prefix would also stop the editor's own
        // switch.
        private static bool OnTrayBarChangedPrefix(object __instance, object[] __args)
        {
            try
            {
                return SwitchPage(__instance, __args);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                Log.Error(LogArea.Tabs, "left panel page switch: {0} ({1})", ScriptEditorHook.FirstLine(ex), ex.GetType().Name);
                return true;
            }
        }

        private static bool SwitchPage(object __instance, object[] __args)
        {
            if (!s_pages.TryGetValue(__instance, out var pages))
                return true;
            var activeTray = (Panel)s_activeTray.GetValue(__instance);
            var value = Convert.ToInt32(__args[0]);
            if (pages.Hosts.TryGetValue(value, out var host))
            {
                activeTray.Content = host;
                ShowPanel(pages.Editor);
                (pages.Contents[value] as ClaudeChat)?.FocusInput();
                (pages.Contents[value] as RhinoWatchTab)?.FocusInput();
                (pages.Contents[value] as PowerShellTerminal)?.FocusInput();
                return false;
            }
            // Leaving one of these pages: the editor only switches content when its
            // state's active page changes, which it may not.
            if (pages.Hosts.ContainsValue(activeTray.Content as Panel))
                s_setActiveTray.Invoke(__instance, new[] { __args[0] });
            return true;
        }

        /// <summary>Unfolds the left panel, as the editor's own icons do
        /// (<c>ActiveOptions.ShowBrowser</c>, which updates the layout).</summary>
        private static void ShowPanel(object editor)
        {
            var options = editor.GetType().GetProperty("ActiveOptions", Any)?.GetValue(editor);
            var show = options?.GetType().GetProperty("ShowBrowser", Any);
            if (show != null && !(bool)show.GetValue(options))
                show.SetValue(options, true);
        }

        // --- the compact layout: buttons in the status bar ---------------------------------

        private static readonly ConditionalWeakTable<object, Dictionary<int, StatusPageButton>> s_statusButtons =
            new ConditionalWeakTable<object, Dictionary<int, StatusPageButton>>();
        private static Harmony s_compactHarmony;
        private static bool s_compactFailed;

        private static void PatchCompact(object editor)
        {
            if (s_compactHarmony != null || s_compactFailed)
                return;
            try
            {
                var status = editor.GetType().GetProperty("Status", Any)?.PropertyType
                    ?? throw new MissingMemberException(editor.GetType().FullName, "Status");
                var createCompactTray = status.GetMethod("CreateCompactTray", Any)
                    ?? throw new MissingMethodException(status.FullName, "CreateCompactTray");
                var toggle = editor.GetType().Assembly.GetTypes().FirstOrDefault(t => t.Name == "BrowserTrayToggleCommand")
                    ?? throw new TypeLoadException("BrowserTrayToggleCommand");
                var executeWith = toggle.GetMethod("ExecuteWith", Any)
                    ?? throw new MissingMethodException(toggle.FullName, "ExecuteWith");
                var harmony = new Harmony("minivivisection.browserpages.compact");
                try
                {
                    harmony.Patch(createCompactTray, postfix: new HarmonyMethod(typeof(BrowserPages), nameof(CreateCompactTrayPostfix)));
                    harmony.Patch(executeWith, prefix: new HarmonyMethod(typeof(BrowserPages), nameof(ToggleTrayPrefix)));
                }
                catch
                {
                    harmony.UnpatchAll(harmony.Id); // both or neither
                    throw;
                }
                s_compactHarmony = harmony;
            }
            catch (Exception ex)
            {
                s_compactFailed = true;
                Log.Warning(LogArea.Tabs, "left panel pages in the compact layout (status bar): {0}", Log.Describe(ex));
            }
        }

        // The editor made its status bar's compact tray (again: options changed).
        private static void CreateCompactTrayPostfix(object rce)
        {
            s_inCreateCompactTray = true;
            try
            {
                var browser = rce?.GetType().GetProperty("Browser", Any)?.GetValue(rce);
                if (browser != null && s_pages.TryGetValue(browser, out var pages))
                    EnsureStatusButtons(pages);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "status bar page buttons: {0}", Log.Describe(ex));
            }
            finally
            {
                s_inCreateCompactTray = false;
            }
        }

        // In CreateCompactTray's postfix: its new tray isn't shown yet (SetLeftTray comes next).
        [ThreadStatic]
        private static bool s_inCreateCompactTray;

        /// <summary>The editor's compact tray built again, as when its options change
        /// (StatusPanel's IOptionsControl.UpdateOptions: CreateCompactTray, then SetLeftTray) --
        /// the postfix fills the new tray before it's shown. A tray on screen doesn't show cells
        /// added to it (its TableLayout is built once): the page buttons only appeared once the
        /// editor rebuilt the tray -- a Grasshopper component's editor, open before the plug-in
        /// found it, after some change to the window.</summary>
        private static void RebuildCompactTray(object editor, object status)
        {
            try
            {
                var update = status.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "IOptionsControl")?.GetMethod("UpdateOptions");
                if (update == null || update.GetParameters().Length != 1 || !update.GetParameters()[0].ParameterType.IsInstanceOfType(editor))
                {
                    Log.Debug(LogArea.Tabs, "status bar page buttons: no UpdateOptions to show them now");
                    return;
                }
                // The tray shown now (a Markdown file has another): shown again after the rebuild.
                var index = status.GetType().GetProperty("LeftTrayIndex", Any);
                var before = index?.GetValue(status);
                update.Invoke(status, new[] { editor });
                if (before != null && !Equals(index.GetValue(status), before))
                    status.GetType().GetMethod("SetLeftTray", Any, null, new[] { typeof(int) }, null)?.Invoke(status, new[] { before });
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "status bar page buttons, shown: {0}", Log.Describe(ex));
            }
        }

        // One of the editor's own page buttons (or menu items) while a page of the plug-in
        // shows: the editor's page back, shown -- not a toggle that folds the panel away.
        private static bool ToggleTrayPrefix(object __instance, object rce, ref System.Threading.Tasks.Task __result)
        {
            try
            {
                var browser = rce?.GetType().GetProperty("Browser", Any)?.GetValue(rce);
                if (browser == null || !s_pages.TryGetValue(browser, out var pages))
                    return true;
                var activeTray = (Panel)s_activeTray.GetValue(browser);
                if (!pages.Hosts.ContainsValue(activeTray.Content as Panel))
                    return true;
                var tray = __instance.GetType().GetProperty("Tray", Any).GetValue(__instance);
                s_setActiveTray.Invoke(browser, new[] { tray });
                pages.TrayBar.GetType().GetProperty("SelectedTray", Any)?.SetValue(pages.TrayBar, tray);
                var state = rce.GetType().GetProperty("ActiveState", Any)?.GetValue(rce);
                state?.GetType().GetProperty("ActiveBrowser", Any)?.SetValue(state, tray);
                ShowPanel(pages.Editor);
                RefreshStatusButtons(pages);
                __result = System.Threading.Tasks.Task.CompletedTask;
                return false;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "status bar page switch: {0}", Log.Describe(ex));
                return true;
            }
        }

        /// <summary>The status bar's compact tray, when the editor shows one with the page
        /// buttons (compact browser): its page buttons for the plug-in's pages, added once,
        /// shown as their settings say.</summary>
        private static void EnsureStatusButtons(Pages pages)
        {
            if (s_compactHarmony == null)
                return;
            try
            {
                var editor = pages.Editor;
                var status = editor.GetType().GetProperty("Status", Any)?.GetValue(editor);
                var options = editor.GetType().GetProperty("ActiveOptions", Any)?.GetValue(editor);
                if (status == null || !(options?.GetType().GetProperty("CompactBrowser", Any)?.GetValue(options) is bool compact) || !compact)
                    return;
                if (!(FindField(status.GetType(), "m_leftTrays").GetValue(status) is System.Collections.IList trays) || trays.Count < 3)
                    return;
                var tray = trays[2]; // the compact tray (StatusPanel.GetTrayIndex)
                if (!s_statusButtons.TryGetValue(tray, out var buttons))
                {
                    buttons = new Dictionary<int, StatusPageButton>();
                    s_statusButtons.Add(tray, buttons);
                    tray.GetType().GetMethod("AddSeparatorToTray", Any, null, Type.EmptyTypes, null)?.Invoke(tray, null);
                }
                var addToTray = tray.GetType().GetMethods(Any).First(m => m.Name == "AddToTray" && m.GetParameters().Length >= 1);
                var added = 0;
                foreach (var (value, title, _, draw) in Definitions)
                {
                    if (buttons.ContainsKey(value) || !pages.Hosts.ContainsKey(value))
                        continue;
                    var button = new StatusPageButton(pages, value, VNames.Text(title), draw);
                    addToTray.Invoke(tray, addToTray.GetParameters().Length == 2 ? new object[] { button, false } : new object[] { button });
                    buttons[value] = button;
                    added++;
                }
                foreach (var pair in buttons)
                    pair.Value.Visible = Shown(pair.Key);
                if (added > 0)
                {
                    Log.Info(LogArea.Tabs, "compact layout: {0} page buttons added to the Script Editor's status bar", added);
                    if (!s_inCreateCompactTray)
                        RebuildCompactTray(editor, status); // its tray may be on screen: shown now, not after a change to the window
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Tabs, "status bar page buttons: {0}", Log.Describe(ex));
            }
        }

        private static void RefreshStatusButtons(Pages pages)
        {
            var editor = pages.Editor;
            var status = editor.GetType().GetProperty("Status", Any)?.GetValue(editor);
            if (status == null || !(FindField(status.GetType(), "m_leftTrays").GetValue(status) is System.Collections.IList trays))
                return;
            foreach (var tray in trays)
            {
                if (tray != null && s_statusButtons.TryGetValue(tray, out var buttons))
                {
                    foreach (var button in buttons.Values)
                        button.Invalidate();
                }
            }
        }

        /// <summary>Whether <paramref name="value"/>'s page shows in the left panel now.</summary>
        private static bool IsShowing(Pages pages, int value)
        {
            try
            {
                var options = pages.Editor.GetType().GetProperty("ActiveOptions", Any)?.GetValue(pages.Editor);
                var shown = options?.GetType().GetProperty("ShowBrowser", Any)?.GetValue(options) as bool? != false;
                return shown && pages.Hosts.TryGetValue(value, out var host) && ReferenceEquals(((Panel)s_activeTray.GetValue(pages.Browser)).Content, host);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A page's button in the status bar, drawn like the editor's own there: its
        /// icon, a light background on hover, a line on top while its page shows. A click
        /// shows the page; on its page already, folds the panel away (as the editor's do).</summary>
        private sealed class StatusPageButton : Drawable
        {
            private readonly Pages _pages;
            private readonly int _value;
            private readonly Icon _icon;
            private bool _hover;

            public StatusPageButton(Pages pages, int value, string title, Action<Graphics> draw)
            {
                _pages = pages;
                _value = value;
                var iconSize = CompactIconSize();
                _icon = DrawnIcons.Draw(draw, iconSize);
                Size = new Size(iconSize.Width + 8, TrayHeight());
                ToolTip = title;
                Cursor = Cursors.Pointer;
                MouseEnter += (s, e) => { _hover = true; Invalidate(); };
                MouseLeave += (s, e) => { _hover = false; Invalidate(); };
                MouseDown += (s, e) =>
                {
                    e.Handled = true;
                    Click();
                };
            }

            private void Click()
            {
                try
                {
                    if (IsShowing(_pages, _value))
                    {
                        var options = _pages.Editor.GetType().GetProperty("ActiveOptions", Any)?.GetValue(_pages.Editor);
                        options?.GetType().GetProperty("ShowBrowser", Any)?.SetValue(options, false);
                    }
                    else
                    {
                        var trayBar = _pages.TrayBar;
                        var property = trayBar.GetType().GetProperty("SelectedTray", Any);
                        var trayEnum = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                        var tray = Enum.ToObject(trayEnum, _value);
                        property.SetValue(trayBar, tray); // the hidden strip follows
                        SwitchPage(_pages.Browser, new[] { tray });
                    }
                    RefreshStatusButtons(_pages);
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.Tabs, "status bar page button: {0}", Log.Describe(ex));
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var bounds = new RectangleF(0, 0, Width, Height);
                if (_hover)
                    g.FillRectangle(Color.FromArgb(128, 128, 128, 48), bounds);
                var size = _icon.Size;
                g.DrawImage(_icon, (Width - size.Width) / 2f, (Height - size.Height) / 2f, size.Width, size.Height);
                if (IsShowing(_pages, _value))
                    g.FillRectangle(Color.FromArgb(0, 120, 215), 2, 0, Width - 4, 2);
            }

            /// <summary>The editor's compact icon size (Theme.GetIconSize(Compact)), else 16 x 16.</summary>
            private static Size CompactIconSize()
            {
                try
                {
                    var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "RhinoCodeEditor");
                    var registry = assembly.GetTypes().First(t => t.Name == "ThemeRegistry");
                    var theme = registry.GetProperty("ActiveTheme", Any | BindingFlags.Static).GetValue(null);
                    var getIconSize = theme.GetType().GetMethod("GetIconSize", Any);
                    var sizeType = getIconSize.GetParameters()[0].ParameterType;
                    if (getIconSize.Invoke(theme, new[] { Enum.Parse(sizeType, "Compact") }) is Size size && size.Width > 0)
                        return size;
                }
                catch (Exception)
                {
                    // the default below
                }
                return new Size(16, 16);
            }

            /// <summary>The status bar's height (Theme.TrayHeight), else 25.</summary>
            private static int TrayHeight()
            {
                try
                {
                    var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "RhinoCodeEditor");
                    var theme = assembly.GetTypes().First(t => t.Name == "Theme" && t.GetProperty("TrayHeight", Any | BindingFlags.Static) != null);
                    if (theme.GetProperty("TrayHeight", Any | BindingFlags.Static).GetValue(null) is int height && height > 0)
                        return height;
                }
                catch (Exception)
                {
                    // the default below
                }
                return 25;
            }
        }

        // Icons: drawn on a 20 x 20 grid, scaled to the strip's icon size, for 100 % and
        // 200 % displays -- filled shapes in color, like the editor's own icons.
        private static Icon DrawnIcon(Action<Graphics> draw) => DrawnIcons.Draw(draw, TabIconSize());

        private static void DrawChat(Graphics g)
        {
            var color = Color.FromArgb(217, 119, 87); // warm orange
            g.FillPath(color, GraphicsPath.GetRoundRect(new RectangleF(2, 3, 16, 11), 3));
            g.FillPolygon(color, new PointF(5, 13), new PointF(10, 13), new PointF(5, 18));
            foreach (var x in new[] { 6.5f, 10f, 13.5f })
                g.FillEllipse(Colors.White, x - 1.2f, 7.3f, 2.4f, 2.4f);
        }

        private static void DrawEye(Graphics g)
        {
            var color = Color.FromArgb(47, 128, 237); // blue
            var eye = new GraphicsPath();
            eye.AddBezier(new PointF(1.5f, 10), new PointF(5, 3.5f), new PointF(15, 3.5f), new PointF(18.5f, 10));
            eye.AddBezier(new PointF(18.5f, 10), new PointF(15, 16.5f), new PointF(5, 16.5f), new PointF(1.5f, 10));
            eye.CloseFigure();
            g.DrawPath(new Pen(color, 1.8f), eye);
            g.FillEllipse(color, 6.5f, 6.5f, 7, 7);
            g.FillEllipse(Colors.White, 8.2f, 7.8f, 2.2f, 2.2f);
        }

        private static void DrawLog(Graphics g)
        {
            var color = Color.FromArgb(120, 130, 145); // slate gray
            g.FillPath(color, GraphicsPath.GetRoundRect(new RectangleF(3, 2, 14, 16), 2));
            var pen = new Pen(Colors.White, 1.4f);
            foreach (var y in new[] { 6f, 9.5f, 13f })
                g.DrawLine(pen, 6, y, 14, y);
        }

        private static void DrawGear(Graphics g)
        {
            var color = Color.FromArgb(110, 125, 150); // gray blue
            for (var i = 0; i < 8; i++)
            {
                g.SaveTransform();
                g.TranslateTransform(10, 10);
                g.RotateTransform(i * 45);
                g.FillRectangle(color, -1.9f, -9.3f, 3.8f, 4.5f);
                g.RestoreTransform();
            }
            g.FillEllipse(color, 3.3f, 3.3f, 13.4f, 13.4f);
            g.FillEllipse(Colors.White, 7, 7, 6, 6);
        }

        private static void DrawThreads(Graphics g)
        {
            // three threads side by side, one stopped (a short bar): a teal timeline
            var color = Color.FromArgb(0, 150, 136);
            g.FillPath(color, GraphicsPath.GetRoundRect(new RectangleF(2, 3, 16, 3.2f), 1.6f));
            g.FillPath(color, GraphicsPath.GetRoundRect(new RectangleF(2, 8.4f, 9, 3.2f), 1.6f));
            g.FillPath(color, GraphicsPath.GetRoundRect(new RectangleF(2, 13.8f, 13, 3.2f), 1.6f));
            g.FillEllipse(color, 13, 7.5f, 5, 5);
        }

        private static void DrawPrompt(Graphics g)
        {
            g.FillPath(Color.FromArgb(38, 113, 190), GraphicsPath.GetRoundRect(new RectangleF(1.5f, 3, 17, 14), 2.5f)); // PowerShell blue
            var pen = new Pen(Colors.White, 1.8f);
            g.DrawLine(pen, 5, 7, 8.5f, 10);
            g.DrawLine(pen, 8.5f, 10, 5, 13);
            g.DrawLine(pen, 10, 13.5f, 15, 13.5f);
        }

        /// <summary>Rhino's tab icon size (<c>BaseTabControl.ImageSize</c>, internal), else 20 x 20.</summary>
        private static Size TabIconSize()
        {
            try
            {
                var type = typeof(Rhino.UI.Panels).Assembly.GetType("Rhino.UI.Internal.TabPanels.Controls.BaseTabControl");
                if (type?.GetProperty("ImageSize", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is Size size && size.Width > 0)
                    return size;
            }
            catch (Exception)
            {
                // the default below
            }
            return new Size(20, 20);
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }
    }
}
