#if RHINO9
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Rhino.Runtime.Code.Editor.Aspects;
using Rhino.Runtime.Code.Execution.Debugging;
using RcCommand = Rhino.Runtime.Code.Editor.Commands.Command;
using RcICommand = Rhino.Runtime.Code.Editor.Commands.ICommand;

namespace MiniVivisection
{
    /// <summary>
    /// The plug-in's entries in Rhino 9's code view menu (right click). Rhino 9 builds that menu
    /// as a model at every right click -- the editor's menu bar's <c>GetTextContextMenu()</c>
    /// (RhinoCodeEditor's <c>MainMenuBarVM</c>, <c>MiniMenuBarVM</c>), shown by <c>EditorContexts</c>
    /// as an <c>EditorMenu</c> -- so a Harmony postfix appends model items to it, drawn like the
    /// editor's own: "Breakpoint Condition…", "Hit Count…", "Log Message…", "Remove Condition"
    /// for the right-clicked line (BreakpointUi, BreakpointMarkers.MenuLine), and while paused
    /// "Watch in Rhino", "Bake to Rhino", "Edit in Rhino" for the selection or the name at the
    /// cursor (EditorMenus). Rhino 8: BreakpointUi.AddCodeMenuItems and EditorMenus.AddItems on
    /// the editor's Eto menu.
    /// </summary>
    internal static class Rhino9CodeMenu
    {
        private static readonly string[] MenuBarTypes = { "RhinoCodeEditor.Aspects.MainMenuBarVM", "RhinoCodeEditor.Aspects.MiniMenuBarVM" };

        private static Harmony s_harmony;
        private static bool s_failed;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_failed ? "the editor's text menu (GetTextContextMenu) is missing" : "not installed";

        /// <summary>Once, when RhinoCodeEditor is loaded (Scan).</summary>
        public static void TryInstall()
        {
            if (s_harmony != null || s_failed)
                return;
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "RhinoCodeEditor");
            if (assembly == null)
                return; // the next scan
            try
            {
                var targets = MenuBarTypes
                    .Select(name => assembly.GetType(name)?.GetMethod("GetTextContextMenu", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null))
                    .Where(m => m != null && typeof(IGroupMenu).IsAssignableFrom(m.ReturnType))
                    .ToList();
                if (targets.Count == 0)
                    throw new MissingMethodException("RhinoCodeEditor.Aspects.MainMenuBarVM", "GetTextContextMenu");
                var harmony = new Harmony("minivivisection.rhino9codemenu");
                foreach (var target in targets)
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(Rhino9CodeMenu), nameof(TextMenuPostfix)));
                s_harmony = harmony;
                Log.Info(LogArea.Menus, "Rhino 9: breakpoint conditions and Watch / Bake / Edit in Rhino in the code view's menu ({0} menu bars).", targets.Count);
            }
            catch (Exception ex)
            {
                s_failed = true;
                UserLog.Warning("Can't add to the code view's menu: {0}", ex.Message);
            }
        }

        private static void TextMenuPostfix(object __instance, IGroupMenu __result)
        {
            try
            {
                if (__result is GroupMenu group && WindowOf(__instance) is object editor)
                    Append(group, editor);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Menus, "the code view's menu: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The Script Editor window whose model has <paramref name="menuBar"/>.</summary>
        private static object WindowOf(object menuBar)
        {
            foreach (var window in Eto.Forms.Application.Instance.Windows)
            {
                if (ReferenceEquals(Rhino9Editor.ModelOf(window)?.MenuBar, menuBar))
                    return window;
            }
            return null;
        }

        private static void Append(GroupMenu group, object editor)
        {
            var access = new ScriptEditorAccess(editor);
            var uri = access.Code?.Uri;

            // The breakpoint's spec, for the right-clicked line.
            var conditions = MiniVivisectionSettings.ConditionalBreakpoints && access.Monaco != null && uri != null;
            group.Add(new Separator());
            foreach (var (id, text, field) in new[]
            {
                ("condition", "Breakpoint Condition…", SpecField.Condition),
                ("hitcount", "Hit Count…", SpecField.HitCount),
                ("logmessage", "Log Message…", SpecField.LogMessage),
            })
                group.Add(Item("minivivisection.breakpoint." + id, text, conditions, async () => BreakpointUi.Edit(editor, await BreakpointMarkers.MenuLine(editor), field)));
            var remove = Item("minivivisection.breakpoint.remove", "Remove Condition", false, async () =>
            {
                if (new ScriptEditorAccess(editor).Code?.Uri is Uri file)
                    BreakpointSpecs.Remove(file, await BreakpointMarkers.MenuLine(editor));
            });
            remove.Visible = false;
            group.Add(remove);
            if (conditions && BreakpointSpecs.HasAny(uri))
                ShowRemoveIfSpec(remove, editor, uri);

            // While paused: the value at the cursor in Rhino.
            var hook = MiniVivisectionPlugin.Instance?.Hook;
            if (hook == null || !MiniVivisectionSettings.PluginOn || EditorMenus.PausedControls(editor) == null)
                return;
            Func<Task<string>> expressionOf = () => EditorMenus.ExpressionAtCursor(new ScriptEditorAccess(editor).EditContext);
            group.Add(new Separator());
            group.Add(PausedItem("minivivisection.watch", "Watch in Rhino", editor, expressionOf, (controls, expression) => hook.AddRhinoWatch(controls, expression)));
            group.Add(PausedItem("minivivisection.bake", "Bake to Rhino", editor, expressionOf, (controls, expression) => WatchActions.Bake(hook, controls, expression)));
            group.Add(PausedItem("minivivisection.edit", "Edit in Rhino", editor, expressionOf, (controls, expression) => hook.Edits.Start(controls, expression)));
        }

        private static ActionMenu Item(string id, string title, bool enabled, Func<Task> run) =>
            new ActionMenu(new ActionCommand(id, title, enabled, run));

        private static ActionMenu PausedItem(string id, string title, object editor, Func<Task<string>> expressionOf, Action<IDebugControls, string> action) =>
            Item(id, title, true, async () =>
            {
                var controls = EditorMenus.PausedControls(editor);
                if (controls == null)
                {
                    UserLog.Warning("{0}: the script isn't paused.", title);
                    return;
                }
                var expression = (await expressionOf())?.Trim();
                if (string.IsNullOrEmpty(expression))
                {
                    UserLog.Warning("{0}: no variable or expression there -- select one or put the cursor on its name.", title);
                    return;
                }
                action(controls, expression);
            });

        /// <summary>"Remove Condition" shown once the right-clicked line is known to have a spec
        /// (the open menu follows the item's Visible and CanExecute).</summary>
        private static async void ShowRemoveIfSpec(ActionMenu remove, object editor, Uri uri)
        {
            try
            {
                if (BreakpointSpecs.Find(uri, await BreakpointMarkers.MenuLine(editor)) == null)
                    return;
                remove.Action.Enable(true);
                remove.Visible = true;
            }
            catch (Exception ex)
            {
                Log.Trace(LogArea.Menus, "Remove Condition: {0}", Log.Describe(ex));
            }
        }

        /// <summary>A menu entry's action as one of the editor's commands.</summary>
        private sealed class ActionCommand : RcCommand
        {
            private readonly Func<Task> _run;

            public ActionCommand(string id, string title, bool enabled, Func<Task> run)
            {
                Id = id;
                Title = title;
                LongTitle = title;
                CanExecute = enabled;
                _run = run;
            }

            public void Enable(bool enabled) => CanExecute = enabled;

            public override async Task Execute(CancellationToken token)
            {
                try
                {
                    await _run();
                }
                catch (Exception ex)
                {
                    UserLog.Error("{0} failed: {1}", Title, ScriptEditorHook.FirstLine(ex));
                }
            }
        }

        /// <summary>A CommandMenu&lt;ICommand&gt;: the editor draws it as a CommandButtonMenu.</summary>
        private sealed class ActionMenu : CommandMenu<RcICommand>
        {
            public ActionMenu(ActionCommand command)
                : base(command.Id, command)
            {
                Action = command;
            }

            public ActionCommand Action { get; }
        }

        private sealed class Separator : SeparatorMenu
        {
            public Separator()
                : base("separator")
            {
            }
        }
    }
}
#endif
