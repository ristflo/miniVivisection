using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eto.Forms;
using HarmonyLib;
#if RHINO9
using System.ComponentModel;
#endif

namespace MiniVivisection
{
#if !RHINO9
    /// <summary>
    /// Two fixed run buttons in the Script Editor's dashboard instead of its "smart" one.
    /// The editor's Run slot (DashboardPanel._runContainer) shows Run, or Debug when the
    /// script has a breakpoint, or Continue while debugging: F5 debugs only with a
    /// breakpoint, and a plain Run isn't traced -- Break now (button, hotkey) can't pause it.
    /// Here, always both:
    /// - <b>Run</b>: the editor's Run command (editor.runCode, its Run Without Debugging):
    ///   a normal run, also when the script has breakpoints;
    /// - <b>Debug</b>: the editor's Debug command (editor.debugCode, Run > Debug): in the
    ///   debugger, also without a breakpoint ("Debug (Async)" for async:true scripts).
    /// They are the commands' own buttons (CreateButton), so they look like the editor's and
    /// are grayed out while a script runs. The Run slot shows only while debugging, when it
    /// is Continue. Placed like the top bar's switches (PluginSwitch): the slot's cell in
    /// LeftTray gets a group -- slot, Run, Debug. Options: "Run and Debug buttons".
    /// </summary>
    internal static class DebugRunButton
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private sealed class Buttons
        {
            public WeakReference<object> Editor;
            public Control Slot;
            public Control Run;
            public Control Debug;
        }

        private static readonly ConditionalWeakTable<object, Buttons> s_added = new ConditionalWeakTable<object, Buttons>();
        private static readonly List<Buttons> s_all = new List<Buttons>();
        private static UITimer s_timer;
        private static string s_problem;
        private static Harmony s_harmony;

        /// <summary>For the health line (EditorHooks): null when added (or not tried), else why not.</summary>
        internal static string HookProblem => s_problem;

        /// <summary>The buttons in <paramref name="editor"/>'s dashboard (an RCE window), once.</summary>
        public static void TryAdd(object editor)
        {
            if (s_added.TryGetValue(editor, out _))
                return;
            try
            {
                var dashboard = editor.GetType().GetProperty("Dashboard", Any)?.GetValue(editor);
                var slot = dashboard?.GetType().GetField("_runContainer", Any)?.GetValue(dashboard) as Control;
                var tray = dashboard?.GetType().GetProperty("LeftTray", Any)?.GetValue(dashboard);
                if (slot == null || tray == null)
                    return; // not built yet: the next scan
                var row = (TableRow)TabStrips.FindField(tray.GetType(), "m_tray").GetValue(tray);
                var table = (TableLayout)TabStrips.FindField(tray.GetType(), "m_trayContainer").GetValue(tray);
                var column = row.Cells.ToList().FindIndex(c => ReferenceEquals(c.Control, slot));
                if (column < 0)
                    throw new InvalidOperationException("the Run button isn't where it was");
                try
                {
                    PatchDebugCommand(editor);
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.Tabs, "Debug stays grayed out without a breakpoint: {0}", Log.Describe(ex));
                }
                var compact = dashboard.GetType().GetField("_compact", Any)?.GetValue(dashboard) as bool? ?? false;
                var buttons = new Buttons
                {
                    Editor = new WeakReference<object>(editor),
                    Slot = slot,
                    Run = CommandButton(editor, "editor.runCode", compact),
                    Debug = CommandButton(editor, "editor.debugCode", compact),
                };

                var group = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2, VerticalContentAlignment = VerticalAlignment.Center };
                slot.Detach();
                table.Add(group, column, 0);
                group.Items.Add(slot);
                group.Items.Add(buttons.Run);
                group.Items.Add(buttons.Debug);
                s_added.Add(editor, buttons);
                lock (s_all)
                    s_all.Add(buttons);
                // Its controls lead to the editor: dropped when it closes, or it would never be freed.
                EditorWindows.WhenClosed(editor, () =>
                {
                    lock (s_all)
                    {
                        s_all.Remove(buttons);
                        if (s_all.Count == 0 && s_timer != null)
                        {
                            s_timer.Stop();
                            s_timer = null;
                        }
                    }
                    s_added.Remove(editor);
                    EndForcedDebug(editor);
                });
                Apply(buttons);
                // Shown now, not at the next tab switch: the tray was laid out already, and it
                // lays out again when the editor reloads its Run slot (as a tab switch does).
                Relayout(dashboard, table);
                Application.Instance.AsyncInvoke(() => Relayout(dashboard, table));
                if (s_timer == null)
                {
                    // the slot follows the editor: shown while debugging (Continue)
                    s_timer = new UITimer { Interval = 0.25 };
                    s_timer.Elapsed += (sender, e) => ApplyAll();
                    s_timer.Start();
                }
                Log.Info(LogArea.Tabs, "Run and Debug buttons are in the Script Editor's dashboard");
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                if (s_problem == null)
                    UserLog.Warning("Can't add the Run and Debug buttons: {0} (the editor's Run menu has both)", ex.Message);
                s_problem = ex.Message;
            }
        }

        /// <summary>The editor's Debug command is enabled only with an active breakpoint
        /// (CodeRunDebugCommand.UpdateState: <c>Enabled &amp;= code.CanDebug</c>, and Code.CanDebug
        /// asks for one) -- though debugging doesn't need one. A postfix enables it also without:
        /// the Debug button, Run > Debug and V Threads' Debug. F5's choice is the editor's own
        /// (the Run slot follows Code.CanDebug, unchanged).</summary>
        // Editors whose Debug command a ScriptEditor Run / RunPythonScript debug run needs now,
        // whatever "Run and Debug buttons" says (ScriptRunCommand.DebugWhenReady).
        private static readonly ConditionalWeakTable<object, object> s_forced = new ConditionalWeakTable<object, object>();

        /// <summary>The editor's Debug command enabled without a breakpoint for a debug run
        /// started by a command (until <see cref="EndForcedDebug"/>), also with the buttons off.
        /// False when the command can't be patched.</summary>
        internal static bool ForceDebug(object editor)
        {
            try
            {
                PatchDebugCommand(editor);
                s_forced.AddOrUpdate(editor, null);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Tabs, "Debug without a breakpoint for a command's debug run: {0}", Log.Describe(ex));
                return false;
            }
        }

        internal static void EndForcedDebug(object editor) => s_forced.Remove(editor);

        private static void PatchDebugCommand(object editor)
        {
            if (s_harmony != null)
                return;
            var type = editor.GetType().Assembly.GetType("RhinoCodeEditor.Editor.Commands.CodeRunDebugCommand", throwOnError: true);
            var updateState = type.GetMethod("UpdateState", Any) ?? throw new MissingMethodException(type.FullName, "UpdateState");
            var harmony = new Harmony("minivivisection.debugwithoutbreakpoints");
            harmony.Patch(updateState, postfix: new HarmonyMethod(typeof(DebugRunButton), nameof(UpdateStatePostfix)));
            s_harmony = harmony;
        }

        private static void UpdateStatePostfix(object __instance, object[] __args)
        {
            try
            {
                var enabled = __instance.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
                if (enabled == null || (bool)enabled.GetValue(__instance) || __args.Length == 0 || __args[0] == null)
                    return;
                var rce = __args[0];
                if (!MiniVivisectionSettings.DebugRunButton && !s_forced.TryGetValue(rce, out _))
                    return;
                var state = rce.GetType().GetProperty("ActiveState", Any)?.GetValue(rce);
                if (state == null || state.GetType().GetProperty("CurrentState", Any)?.GetValue(state)?.ToString() != "EditingCode" ||
                    state.GetType().GetProperty("HasPreview", Any)?.GetValue(state) as bool? == true)
                    return; // running, idle or a preview: as the editor says
                var active = state.GetType().GetProperty("ActiveCode", Any)?.GetValue(state);
                var code = active?.GetType().GetProperty("Code", Any)?.GetValue(active) as Rhino.Runtime.Code.Code;
                if (code != null && code.CanRun && code.HasDebugControls && code.Language.Capabilities.CanTrace)
                    enabled.SetValue(__instance, true); // debuggable, breakpoint or not
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Debug without breakpoints: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The dashboard's left tray laid out again: the editor's RefreshRunCommands
        /// with its current state (it reloads the Run slot), and the table's own update.</summary>
        private static void Relayout(object dashboard, TableLayout table)
        {
            try
            {
                var type = dashboard.GetType();
                var canDebug = type.GetField("_canDebug", Any)?.GetValue(dashboard) as bool? ?? false;
                var inDebug = type.GetField("_inDebug", Any)?.GetValue(dashboard) as bool? ?? false;
                type.GetMethod("RefreshRunCommands", Any, null, new[] { typeof(bool), typeof(bool) }, null)?.Invoke(dashboard, new object[] { canDebug, inDebug });
                table.Update();
                ApplyAll();
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "run buttons, layout: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The button of one of the editor's commands (BaseCommand.CreateButton).</summary>
        private static Control CommandButton(object editor, string commandId, bool compact)
        {
            var commands = editor.GetType().GetProperty("ActiveCommands", Any).GetValue(editor);
            var command = commands.GetType().GetMethod("Find", Any, null, new[] { typeof(string) }, null)?.Invoke(commands, new object[] { commandId })
                ?? throw new MissingMemberException("ActiveCommands", commandId);
            var createButton = command.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "CreateButton" && m.GetParameters().Length == 1)
                ?? throw new MissingMethodException(command.GetType().FullName, "CreateButton");
            var button = (Control)createButton.Invoke(command, new object[] { compact });
            UpdateCommand(command, editor);
            // No script open yet: the commands set no icon -- their usual one then.
            var iconId = command.GetType().GetProperty("IconId", Any);
            if (iconId != null && string.IsNullOrEmpty(iconId.GetValue(command) as string) && iconId.CanWrite)
                iconId.SetValue(command, commandId);
            LoadImages(button, editor);
            return button;
        }

        /// <summary>The button's images, from the editor's theme: the editor's buttons load
        /// theirs in UpdateTheme(), which the dashboard calls only for its own (UpdateStyle) --
        /// a new button stayed empty until its icon changed (an async:true script's tab).</summary>
        private static void LoadImages(Control button, object editor)
        {
            try
            {
                var style = button.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "IStyledControl")?.GetMethod("UpdateStyle");
                if (style != null && style.GetParameters().Length == 1 && style.GetParameters()[0].ParameterType.IsInstanceOfType(editor))
                    style.Invoke(button, new[] { editor });
                else
                    button.GetType().GetMethod("UpdateTheme", Any, null, Type.EmptyTypes, null)?.Invoke(button, null);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "run buttons, images: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The command's state now (UpdateState(RCE)): its icon and Enabled. The Debug
        /// command sets its icon there only, which the editor calls on a state change (a tab
        /// switch) -- till then its new button had no icon and drew as nothing.</summary>
        private static void UpdateCommand(object command, object editor)
        {
            try
            {
                var update = command.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "UpdateState" && m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsInstanceOfType(editor));
                update?.Invoke(command, new[] { editor });
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "run buttons, command state: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The setting, the V Debug switch or the editor's state changed (UI thread).</summary>
        public static void ApplyAll()
        {
            lock (s_all)
            {
                s_all.RemoveAll(b => !b.Editor.TryGetTarget(out _));
                foreach (var buttons in s_all)
                    Apply(buttons);
            }
        }

        private static void Apply(Buttons buttons)
        {
            try
            {
                var on = MiniVivisectionSettings.DebugRunButton;
                if (buttons.Run.Visible != on)
                    buttons.Run.Visible = on;
                if (buttons.Debug.Visible != on)
                    buttons.Debug.Visible = on;
                var slot = !on || (buttons.Editor.TryGetTarget(out var editor) && EditorState(editor) == "DebuggingCode");
                if (buttons.Slot.Visible != slot)
                    buttons.Slot.Visible = slot;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "run buttons: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The editor's state: PlayingCode (a plain Run), DebuggingCode, Idling, ...</summary>
        private static string EditorState(object editor)
        {
            var state = editor.GetType().GetProperty("ActiveState", Any)?.GetValue(editor);
            return state?.GetType().GetProperty("CurrentState", Any)?.GetValue(state)?.ToString();
        }
    }
#else
    /// <summary>
    /// Rhino 9: two fixed buttons next to the Script Editor's run slot (MainDashboard's
    /// CodeRunDebugContinueCommand: Run, Debug when the script has a breakpoint, Continue while
    /// paused). Run is the editor's Run command (F5), Debug its Debug command (Shift+F5), enabled
    /// also without a breakpoint (Rhino's DebugCommand asks for one: <c>CanExecute &amp;=
    /// code.CanDebug</c>; the debug run itself doesn't need one). The slot shows only during a
    /// debug run, as Continue. Placed in every left tray of the dashboard (MainEditor.m_dashboard,
    /// ControlBoard.m_traysL) right after the slot. Options: "Run and Debug buttons".
    /// </summary>
    internal static class DebugRunButton
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const string SlotCommandName = "CodeRunDebugContinueCommand";

        private sealed class Buttons
        {
            public WeakReference<Window> Window;
            public readonly List<Control> Slots = new List<Control>();
            public readonly List<Control> Added = new List<Control>();
        }

        private static readonly ConditionalWeakTable<object, Buttons> s_added = new ConditionalWeakTable<object, Buttons>();
        private static readonly List<Buttons> s_all = new List<Buttons>();
        private static readonly MethodInfo s_setCanExecute =
            typeof(Rhino.Runtime.Code.Editor.Commands.Command).GetProperty(nameof(Rhino.Runtime.Code.Editor.Commands.Command.CanExecute))?.GetSetMethod(true);
        private static string s_problem;

        /// <summary>For the health line (EditorHooks): null when added (or not tried), else why not.</summary>
        internal static string HookProblem => s_problem;

        /// <summary>The buttons in <paramref name="editor"/>'s dashboard (the standalone editor's
        /// window, RhinoCodeEditor.MainEditor), once. Grasshopper's editors (MiniDashboard): not yet.</summary>
        public static void TryAdd(object editor)
        {
            if (!(editor is Window window) || s_added.TryGetValue(window, out _))
                return;
            if (window.GetType().FullName != "RhinoCodeEditor.MainEditor")
                return;
            try
            {
                var model = Rhino9Editor.ModelOf(window) as Rhino.Runtime.Code.Editor.IMainEditor;
                if (model == null)
                    return; // not set up yet: the next scan
                var commands = model.Commands.GetItems().ToList();
                var run = commands.FirstOrDefault(c => DerivesFrom(c.GetType(), typeof(Rhino.Runtime.Code.Editor.Commands.RunCommand<>)))
                    ?? throw new MissingMemberException("the editor's commands", "editor.runCode");
                var debug = commands.FirstOrDefault(c => DerivesFrom(c.GetType(), typeof(Rhino.Runtime.Code.Editor.Commands.DebugCommand<>)))
                    ?? throw new MissingMemberException("the editor's commands", "editor.debugCode");
                var dashboard = TabStrips.FindField(window.GetType(), "m_dashboard").GetValue(window);
                if (dashboard == null)
                    return; // not built yet: the next scan
                var trays = ((System.Collections.IEnumerable)TabStrips.FindField(dashboard.GetType(), "m_traysL").GetValue(dashboard)).Cast<object>().ToList();
                var buttons = new Buttons { Window = new WeakReference<Window>(window) };
                // The dashboard's buttons get their commands by binding (DataContext), and only a
                // shown tray is bound: the project tray (MainDashboard's second, shown while a
                // project is open) has none. The slot is found by its command in a bound tray; in
                // an unbound one at the same place -- right after the first separator (MainDashboard:
                // New, Open, Save, ..., separator, the run slot, the steps, Stop).
                object CommandOf(Control c) => c.GetType().GetProperty("Command", Any)?.GetValue(c);
                bool IsSlot(Control c) => CommandOf(c)?.GetType().Name.StartsWith(SlotCommandName, StringComparison.Ordinal) == true;
                var trayItems = trays.Select(t => ((IEnumerable<Control>)t.GetType().GetProperty("Items", Any).GetValue(t)).ToList()).ToList();
                if (!trayItems.Any(items => items.Any(IsSlot)))
                {
                    if (trayItems.Any(items => items.Any(c => CommandOf(c) != null)))
                        throw new InvalidOperationException("the Run button isn't where it was");
                    return; // not bound yet: the next scan
                }
                for (var t = 0; t < trays.Count; t++)
                {
                    var tray = trays[t];
                    var items = trayItems[t];
                    var index = items.FindIndex(IsSlot);
                    if (index < 0 && items.All(c => CommandOf(c) == null))
                    {
                        var separator = items.FindIndex(c => c.GetType().Name == "Separator");
                        index = separator >= 0 && separator + 1 < items.Count && items[separator + 1].GetType() == items[0].GetType() ? separator + 1 : -1;
                        if (index < 0)
                            continue; // an unbound tray of another layout: left as it is
                    }
                    if (index < 0)
                        throw new InvalidOperationException("the Run button isn't where it was");
                    var slot = items[index];
                    // The tray is a TableLayout built once: a cell inserted now (InsertToTray) isn't
                    // laid out -- the buttons would be there but never shown. So the slot's cell
                    // gets a group instead (TableLayout.Add replaces a cell's control): the slot,
                    // then Run and Debug (as PluginSwitch does in the right tray).
                    // A tray not shown yet (the project tray) has no layout yet: there a cell
                    // inserted now is laid out when it's built.
                    var table = (TableLayout)TabStrips.FindField(tray.GetType(), "m_trayContainer").GetValue(tray);
                    var built = table.Loaded;
                    var group = built ? new StackLayout { Orientation = Orientation.Horizontal, Spacing = 0, Padding = 0, VerticalContentAlignment = VerticalAlignment.Center } : null;
                    var insert = tray.GetType().GetMethod("InsertToTray", Any) ?? throw new MissingMethodException(tray.GetType().FullName, "InsertToTray");
                    var at = index + 1;
                    foreach (var command in new[] { run, debug })
                    {
                        var button = (Control)Activator.CreateInstance(slot.GetType(), new object[] { false }); // PushButton(compact: false)
                        button.GetType().GetProperty("Command", Any).SetValue(button, command);
                        CopyTheme(slot, button);
                        if (built)
                            group.Items.Add(button);
                        else
                            insert.Invoke(tray, new object[] { at++, button, false });
                        buttons.Added.Add(button);
                    }
                    if (built)
                    {
                        slot.Detach();
                        try
                        {
                            table.Add(group, index, 0);
                            group.Items.Insert(0, slot);
                        }
                        catch
                        {
                            table.Add(slot, index, 0); // the slot back where it was
                            throw;
                        }
                    }
                    buttons.Slots.Add(slot);
                }
                if (buttons.Slots.Count == 0)
                    throw new InvalidOperationException("the dashboard has no Run button");
                FollowStopClicks(trayItems);
                s_added.Add(window, buttons);
                lock (s_all)
                    s_all.Add(buttons);
                UnlockDebug(debug, model);
                PropertyChangedEventHandler onModel = (s, e) =>
                {
                    if (e.PropertyName == "Theme")
                        Application.Instance.AsyncInvoke(() => buttons.Added.ForEach(b => CopyTheme(buttons.Slots[0], b)));
                };
                PropertyChangedEventHandler onCodes = (s, e) =>
                {
                    if (e.PropertyName == "IsRunning" || e.PropertyName == "RunKind" || e.PropertyName == "DebugControlsState")
                        Application.Instance.AsyncInvoke(() => Apply(buttons));
                };
                model.PropertyChanged += onModel;
                model.Codes.PropertyChanged += onCodes;
                // Its controls lead to the editor: dropped when it closes.
                EditorWindows.WhenClosed(window, () =>
                {
                    model.PropertyChanged -= onModel;
                    model.Codes.PropertyChanged -= onCodes;
                    lock (s_all)
                        s_all.Remove(buttons);
                    s_added.Remove(window);
                });
                Apply(buttons);
                Log.Info(LogArea.Tabs, "Run and Debug buttons are in the Script Editor's dashboard ({0} tray(s))", buttons.Slots.Count);
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                if (s_problem == null)
                    UserLog.Warning("Can't add the Run and Debug buttons: {0} (the editor's Run menu has both)", ex.Message);
                s_problem = ex.Message;
            }
        }

        /// <summary>The dashboard's Stop buttons: a click on them in the log (V°Log, detailed log),
        /// with whether the button and its command could run then -- a user's Stop clicks
        /// during a pause did nothing, and a click can't be sent to the editor in a test.</summary>
        private static void FollowStopClicks(List<List<Control>> trayItems)
        {
            try
            {
                foreach (var items in trayItems)
                {
                    foreach (var stop in items.Where(c => c.GetType().GetProperty("Command", Any)?.GetValue(c)?.GetType().Name.StartsWith("CodeDebugStopCommand", StringComparison.Ordinal) == true))
                    {
                        var button = stop;
                        string State()
                        {
                            var command = button.GetType().GetProperty("Command", Any)?.GetValue(button) as Rhino.Runtime.Code.Editor.Commands.ICommand;
                            return string.Format("button enabled {0}, its command can run {1}", button.Enabled, command?.CanExecute);
                        }
                        button.MouseDown += (s, e) => Log.Info(LogArea.Tabs, "the dashboard's Stop: mouse down ({0})", State());
                        button.MouseUp += (s, e) => Log.Info(LogArea.Tabs, "the dashboard's Stop: mouse up ({0})", State());
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Stop clicks: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The registered Debug command (the button, Run > Debug, Shift+F5) enabled also
        /// without a breakpoint: set back right after Rhino's own state sets it off. The run slot's
        /// own Debug command is another instance: F5's and the slot's choice stay Rhino's.</summary>
        private static void UnlockDebug(Rhino.Runtime.Code.Editor.Commands.ICommand debug, Rhino.Runtime.Code.Editor.IMainEditor model)
        {
            PropertyChangedEventHandler onDebug = (s, e) =>
            {
                if (e.PropertyName == nameof(Rhino.Runtime.Code.Editor.Commands.ICommand.CanExecute))
                    EnableWithoutBreakpoint(debug, model);
            };
            PropertyChangedEventHandler onCodes = (s, e) =>
            {
                if (e.PropertyName == "Current" || e.PropertyName == "IsRunning" || e.PropertyName == "CanDebug")
                    EnableWithoutBreakpoint(debug, model);
            };
            debug.PropertyChanged += onDebug;
            model.Codes.PropertyChanged += onCodes;
            EnableWithoutBreakpoint(debug, model);
        }

        private static void EnableWithoutBreakpoint(Rhino.Runtime.Code.Editor.Commands.ICommand debug, Rhino.Runtime.Code.Editor.IMainEditor model)
        {
            try
            {
                if (debug.CanExecute || debug.Locked || s_setCanExecute == null || !MiniVivisectionSettings.DebugRunButton)
                    return;
                var codes = model.Codes;
                if (codes.IsRunning || model.Preview.IsViewing || !codes.TryGetCurrent(out Rhino.Runtime.Code.Code code) || code == null)
                    return; // running, a preview, nothing open: as Rhino says
                if (!code.CanRun || !code.HasDebugControls || !code.Language.Capabilities.CanTrace)
                    return;
                var project = model.Projects?.Current?.Find(code.Id);
                if (project != null && !project.CanExecute)
                    return; // the editor's project rule (ExecuteCommand)
                s_setCanExecute.Invoke(debug, new object[] { true }); // raises CanExecute again: true, nothing more to do
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "Debug without breakpoints: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The setting, the V Debug switch or the editor's state changed (UI thread).</summary>
        public static void ApplyAll()
        {
            lock (s_all)
            {
                s_all.RemoveAll(b => !b.Window.TryGetTarget(out _));
                foreach (var buttons in s_all)
                    Apply(buttons);
            }
        }

        private static void Apply(Buttons buttons)
        {
            try
            {
                if (!buttons.Window.TryGetTarget(out var window))
                    return;
                var on = MiniVivisectionSettings.DebugRunButton;
                var model = Rhino9Editor.ModelOf(window) as Rhino.Runtime.Code.Editor.IMainEditor;
                var codes = model?.Codes;
                var debugging = codes != null && codes.IsRunning && codes.RunKind == Rhino.Runtime.Code.Execution.BuildKind.Debug;
                foreach (var button in buttons.Added)
                {
                    if (button.Visible != on)
                        button.Visible = on;
                }
                var show = !on || debugging;
                foreach (var slot in buttons.Slots)
                {
                    if (slot.Visible != show)
                        slot.Visible = show;
                }
                var debug = model?.Commands.GetItems().FirstOrDefault(c => DerivesFrom(c.GetType(), typeof(Rhino.Runtime.Code.Editor.Commands.DebugCommand<>)));
                if (debug == null)
                    return;
                if (on)
                    EnableWithoutBreakpoint(debug, model);
                else
                    debug.GetType().GetMethod("OnModelChanged", Any, null, Type.EmptyTypes, null)?.Invoke(debug, null); // Rhino's own state again
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "run buttons: {0}", Log.Describe(ex));
            }
        }

        private static void CopyTheme(Control from, Control to)
        {
            try
            {
                var theme = from.GetType().GetProperty("Theme", Any);
                if (theme != null && theme.CanWrite)
                    theme.SetValue(to, theme.GetValue(from));
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "run buttons, theme: {0}", Log.Describe(ex));
            }
        }

        private static bool DerivesFrom(Type type, Type genericDefinition)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == genericDefinition)
                    return true;
            }
            return false;
        }

        // Rhino 8's ForceDebug/EndForcedDebug (a command's debug run): not needed -- ScriptEditor
        // Run debugs through the editor's Codes.RestoreAndDebug, without the breakpoint rule.
        internal static bool ForceDebug(object editor) => true;

        internal static void EndForcedDebug(object editor)
        {
        }
    }
#endif
}
