using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MiniVivisection
{
    /// <summary>
    /// A folder dropped anywhere on a Script Editor opens in the editor's own Explorer
    /// (left panel), as its "Explore Path" command does: <c>ActiveState.ExplorerDirectory</c>,
    /// <c>ActiveBrowser = Explorer</c>, <c>ActiveOptions.ShowBrowser</c>, and an undo step
    /// (ExplorerChangeDirectoryCommand.Action). Files dropped are left to the editor
    /// (it opens them); only drops of folders alone are taken.
    ///
    /// The window's WPF Preview drag events (through reflection: the plug-in has no WPF
    /// reference) reach the window before any of its panels, so the drop works over the
    /// tabs, the panels and the trays alike. Options: "Drop a folder: open it in the editor's Explorer".
    /// </summary>
    internal sealed class FolderDrop
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly ConditionalWeakTable<object, FolderDrop> s_added = new ConditionalWeakTable<object, FolderDrop>();

        /// <summary>Why the drop hook isn't in place (EditorHooks), or null.</summary>
        internal static string HookProblem { get; private set; }

        private readonly object _editor;

        private FolderDrop(object editor) => _editor = editor;

        public static void TryAdd(object editor)
        {
            if (s_added.TryGetValue(editor, out _))
                return;
            var drop = new FolderDrop(editor);
            s_added.Add(editor, drop);
            try
            {
                var window = (editor as Eto.Forms.Control)?.ControlObject;
                var element = window?.GetType();
                while (element != null && element.FullName != "System.Windows.UIElement")
                    element = element.BaseType;
                if (element == null)
                    throw new InvalidOperationException("the editor isn't a WPF window");
                var addHandler = element.GetMethod("AddHandler", new[] { element.Assembly.GetType("System.Windows.RoutedEvent"), typeof(Delegate), typeof(bool) });
                foreach (var name in new[] { "PreviewDragEnterEvent", "PreviewDragOverEvent", "PreviewDropEvent" })
                {
                    var routedEvent = element.GetField(name, BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    var handlerType = (Type)routedEvent.GetType().GetProperty("HandlerType").GetValue(routedEvent);
                    var method = typeof(FolderDrop).GetMethod(name == "PreviewDropEvent" ? nameof(OnDrop) : nameof(OnDragOver), Any);
                    addHandler.Invoke(window, new[] { routedEvent, Delegate.CreateDelegate(handlerType, drop, method), (object)false });
                }
            }
            catch (Exception ex)
            {
                if (HookProblem == null)
                    UserLog.Warning("Dropping a folder on the Script Editor won't open it in Explorer: {0}", Log.Describe(ex));
                HookProblem = ex.Message;
            }
        }

        // WPF's DragEventHandler(object, DragEventArgs): these take its arguments as
        // object and EventArgs (a delegate may take less derived parameters).
        private void OnDragOver(object sender, EventArgs e)
        {
            if (FolderOf(e) == null)
                return;
            SetEffects(e, 4); // DragDropEffects.Link: the folder isn't moved or copied
            Handled(e);
        }

        private void OnDrop(object sender, EventArgs e)
        {
            var folder = FolderOf(e);
            if (folder == null)
                return;
            Handled(e);
            try
            {
                Explore(_editor, folder);
                UserLog.Info("Explorer: {0}", folder);
            }
            catch (Exception ex)
            {
                UserLog.Warning("Can't open {0} in Explorer: {1}", folder, Log.Describe(ex));
            }
        }

        /// <summary>The first folder, when only folders are dropped; else null (files: the editor's).</summary>
        private static string FolderOf(EventArgs e)
        {
            if (!MiniVivisectionSettings.FolderDrop)
                return null;
            try
            {
                var data = e.GetType().GetProperty("Data")?.GetValue(e);
                var getData = data?.GetType().GetInterface("System.Windows.IDataObject")?.GetMethod("GetData", new[] { typeof(string) });
                if (!(getData?.Invoke(data, new object[] { "FileDrop" }) is string[] paths) || paths.Length == 0)
                    return null;
                return paths.All(Directory.Exists) ? paths[0] : null;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "folder drop: {0}", Log.Describe(ex));
                return null;
            }
        }

        private static void SetEffects(EventArgs e, int effects)
        {
            var property = e.GetType().GetProperty("Effects");
            property?.SetValue(e, Enum.ToObject(property.PropertyType, effects));
        }

        private static void Handled(EventArgs e) => e.GetType().GetProperty("Handled")?.SetValue(e, true);

        /// <summary>What the editor's "Explore Path" command does, for <paramref name="folder"/>.</summary>
        internal static void Explore(object editor, string folder)
        {
            var state = editor.GetType().GetProperty("ActiveState", Any).GetValue(editor);
            var directory = state.GetType().GetProperty("ExplorerDirectory", Any);
            var before = (Uri)directory.GetValue(state);
            var after = new Uri(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            directory.SetValue(state, after);
            var browser = state.GetType().GetProperty("ActiveBrowser", Any);
            if (browser != null)
                browser.SetValue(state, Enum.Parse(browser.PropertyType, "Explorer"));
            var options = editor.GetType().GetProperty("ActiveOptions", Any).GetValue(editor);
            options.GetType().GetProperty("ShowBrowser", Any).SetValue(options, true);

            // Undo (Ctrl+Z) goes back to the folder before, as after "Explore Path".
            try
            {
                var actionType = editor.GetType().Assembly.GetType("RhinoCodeEditor.Editor.Commands.ExplorerChangeDirectoryCommand+Action");
                var actions = editor.GetType().GetProperty("Actions", Any)?.GetValue(editor);
                var push = actions?.GetType().GetMethod("Push", Any);
                if (actionType != null && push != null)
                    push.Invoke(actions, new[] { Activator.CreateInstance(actionType, before, after) });
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "folder drop: no undo step: {0}", Log.Describe(ex));
            }
        }
    }
}
