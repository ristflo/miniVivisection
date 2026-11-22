#if RHINO9
using System;
using System.Linq;
using System.Reflection;
using Rhino.Runtime.Code.Editing;
using Rhino.Runtime.Code.Editor;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Rhino 9's Script Editor, as the plug-in reaches it. Rhino 9 rewrote the editor: its
    /// windows (RhinoCodeEditor's <c>MainEditor</c>, a <c>CodeEditorForm&lt;IMainEditor&gt;</c>) are
    /// views of a public model, <see cref="IEditor"/> (Rhino.Runtime.Code.Editor), set as the
    /// window's <c>DataContext</c>. The model has what Rhino 8's internal <c>RCE</c> was reached
    /// for by reflection: the debugger (<c>Codes.DebugControls</c>), the open script and its
    /// text editor (<c>Codes.Current</c>), the Terminal's text. The "editor" object the shared
    /// code passes around stays the window, as in Rhino 8.
    /// </summary>
    internal static class Rhino9Editor
    {
        /// <summary>The editor model of <paramref name="window"/> (a Script Editor window), or null.</summary>
        public static IEditor ModelOf(object window)
        {
            try
            {
                return (window as Eto.Forms.BindableWidget)?.DataContext as IEditor;
            }
            catch (Exception)
            {
                return null; // a window being closed
            }
        }

        /// <summary>Whether <paramref name="window"/> is a Script Editor window.</summary>
        public static bool IsEditorWindow(object window) => ModelOf(window) != null;

        /// <summary>The debugger of the editor's scripts (Rhino 8: <c>ActiveState.DebugControls</c>).</summary>
        public static IDebugControls DebugControlsOf(object window)
        {
            try
            {
                return ModelOf(window)?.Codes?.DebugControls;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The text editor of the script shown now (Rhino 8: <c>ActiveEditContext</c>).</summary>
        public static ITextEditor TextEditorOf(object window)
        {
            try
            {
                return ModelOf(window)?.Codes?.Current?.Editor;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Whether the editor holds its debugger after a run (Rhino 8's DebuggingCode
        /// state: stopped at an error, waiting for Stop).</summary>
        public static bool HoldsDebugger(object window)
        {
            try
            {
                var codes = ModelOf(window)?.Codes;
                return codes != null && codes.HasDebugControls && codes.DebuggerState == Rhino.Runtime.Code.Execution.TracerState.Attached;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The Terminal's text (prints, errors).</summary>
        public static string TerminalText(object window)
        {
            try
            {
                return ModelOf(window)?.Terminal?.Text ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>Adds <paramref name="text"/> at the end of the Terminal (UI thread).</summary>
        public static void AppendTerminal(object window, string text)
        {
            var terminal = ModelOf(window)?.Terminal;
            if (terminal == null)
                return;
            var stream = terminal.GetStdout(false); // the Terminal's own: not disposed here
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>The model's codes aspect type that declares the static run events
        /// (<c>CodesAspect&lt;,,&gt;.GroupBeginExecute</c>, <c>GroupEndExecute</c>): a closed generic,
        /// one per kind of editor.</summary>
        public static Type CodesAspectTypeOf(object window)
        {
            var codes = ModelOf(window)?.Codes;
            for (var type = codes?.GetType(); type != null; type = type.BaseType)
            {
                if (type.GetEvent("GroupBeginExecute", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly) != null)
                    return type;
            }
            return null;
        }

        /// <summary>Whether Rhino 9's Script Editor assembly is loaded (an editor was opened).</summary>
        public static bool EditorLoaded() =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "RhinoCodeEditor");
    }
}
#endif
