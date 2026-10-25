// A folder dropped on the Script Editor opens in its Explorer (FolderDrop): real WPF
// drag events on a stand-in editor window with the editor's state members.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Eto.Forms;
using Wpf = System.Windows;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        internal enum StandInBrowser
        {
            Project,
            Explorer,
            Search,
        }

        internal sealed class StandInEditorState
        {
            public Uri ExplorerDirectory { get; set; }

            public StandInBrowser ActiveBrowser { get; set; } = StandInBrowser.Search;
        }

        internal sealed class StandInEditorOptions
        {
            public bool ShowBrowser { get; set; }
        }

        /// <summary>The editor's members FolderDrop uses: RCE.ActiveState and ActiveOptions.</summary>
        internal sealed class StandInEditor : Form
        {
            public StandInEditorState ActiveState { get; } = new StandInEditorState();

            public StandInEditorOptions ActiveOptions { get; } = new StandInEditorOptions();
        }

        private static Wpf.DragEventArgs Drag(Wpf.RoutedEvent routedEvent, Wpf.DependencyObject target, params string[] paths)
        {
            var data = new Wpf.DataObject(Wpf.DataFormats.FileDrop, paths);
            // WPF makes these only inside a drag: its internal constructor, by parameter type.
            var constructor = typeof(Wpf.DragEventArgs).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var args = constructor.GetParameters().Select(p =>
                p.ParameterType == typeof(Wpf.IDataObject) ? data
                : p.ParameterType == typeof(Wpf.DragDropEffects) ? Wpf.DragDropEffects.All
                : p.ParameterType == typeof(Wpf.DependencyObject) ? target
                : (object)Activator.CreateInstance(p.ParameterType)).ToArray();
            var e = (Wpf.DragEventArgs)constructor.Invoke(args);
            e.RoutedEvent = routedEvent;
            e.Effects = Wpf.DragDropEffects.Move;
            return e;
        }

        private static void FolderDropChecks()
        {
            var app = Application.Instance ?? new Application(Eto.Platforms.Wpf);
            var folder = Path.Combine(Path.GetTempPath(), "minivivisection-drop-checks");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "script.py");
            File.WriteAllText(file, "x = 1\n");

            var editor = new StandInEditor { Width = 300, Height = 200 };
            editor.Show();
            try
            {
                FolderDrop.TryAdd(editor);
                Check("hook in place", FolderDrop.HookProblem, null);
                var window = (Wpf.Window)editor.ControlObject;

                var over = Drag(Wpf.UIElement.PreviewDragOverEvent, window, folder);
                window.RaiseEvent(over);
                Check("drag over: a folder is taken", over.Handled, true);
                Check("drag over: as a link", over.Effects, Wpf.DragDropEffects.Link);

                var overFile = Drag(Wpf.UIElement.PreviewDragOverEvent, window, file);
                window.RaiseEvent(overFile);
                Check("drag over: a file is the editor's", overFile.Handled, false);
                var mixed = Drag(Wpf.UIElement.PreviewDragOverEvent, window, folder, file);
                window.RaiseEvent(mixed);
                Check("drag over: a folder with a file is the editor's", mixed.Handled, false);

                var drop = Drag(Wpf.UIElement.PreviewDropEvent, window, folder);
                window.RaiseEvent(drop);
                Check("drop: handled", drop.Handled, true);
                Check("drop: Explorer directory", editor.ActiveState.ExplorerDirectory?.LocalPath, folder + Path.DirectorySeparatorChar);
                Check("drop: Explorer shown", editor.ActiveState.ActiveBrowser, StandInBrowser.Explorer);
                Check("drop: left panel open", editor.ActiveOptions.ShowBrowser, true);
            }
            finally
            {
                editor.Close();
            }
        }
    }
}
