using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eto.Forms;
using HarmonyLib;

namespace MiniVivisection
{
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
}
