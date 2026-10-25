using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Ed.Eto;
using Rhino;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Editing;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// What a Script Editor window shows, for the Claude tab and its tools: the open
    /// script, its selection and cursor, the Terminal's text, the debugger. Internal
    /// editor UI (RhinoCode 8.36, by reflection): <c>RCE.Codes.Editors.ActiveEditContext</c>
    /// (public <see cref="ICodeEditContext"/>), its Monaco view (<c>_monaco</c>, a public
    /// <see cref="IEd"/>), <c>RCE.Console.Terminal.TextArea.Text</c>. Call on the UI
    /// thread (<see cref="OnUiThread{T}"/>).
    /// </summary>
    internal sealed class ScriptEditorAccess
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public ScriptEditorAccess(object editor)
        {
            Editor = editor;
        }

        public object Editor { get; }

        public IDebugControls DebugControls => ScriptEditorHook.DebugControlsOf(Editor);

        public bool IsPaused => DebugControls?.State == DebugControlsState.Paused;

        /// <summary>What holds the script now, or null when it can be edited: the debugger
        /// runs it or is paused (a breakpoint, an exception), or the editor is still in its
        /// debugging state -- after a run stopped at a runtime error, until Stop. The editor
        /// doesn't show changes to the script until then.</summary>
        public string RunState
        {
            get
            {
                switch (DebugControls?.State)
                {
                    case DebugControlsState.Paused:
                        return "paused in the debugger";
                    case DebugControlsState.Debugging:
                        return "running in the debugger";
                }
                return EditorState == "DebuggingCode" ? "stopped (an error or the end of a debug run) -- the editor waits for Stop" : null;
            }
        }

        public bool IsBusy => RunState != null;

        /// <summary>The editor's ActiveState.CurrentState: EditingCode, DebuggingCode, ...</summary>
        private string EditorState
        {
            get
            {
                try
                {
                    var state = Editor?.GetType().GetProperty("ActiveState", Any)?.GetValue(Editor);
                    return state?.GetType().GetProperty("CurrentState", Any)?.GetValue(state)?.ToString();
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Claude, "editor state: {0}", Log.Describe(ex));
                    return null;
                }
            }
        }

        public ICodeEditContext EditContext
        {
            get
            {
                var codes = Editor.GetType().GetProperty("Codes", Any)?.GetValue(Editor);
                var editors = codes?.GetType().GetProperty("Editors", Any)?.GetValue(codes);
                return editors?.GetType().GetProperty("ActiveEditContext", Any)?.GetValue(editors) as ICodeEditContext;
            }
        }

        public Code Code => EditContext?.Code;

        /// <summary>The Monaco view of the active script, when it is one.</summary>
        public IEd Monaco
        {
            get
            {
                var context = EditContext;
                for (var type = context?.GetType(); type != null; type = type.BaseType)
                {
                    var field = type.GetField("_monaco", Any | BindingFlags.DeclaredOnly);
                    if (field != null)
                        return field.GetValue(context) as IEd;
                }
                return null;
            }
        }

        public string ScriptText => Code == null ? null : (string)Code.Text;

        public string ScriptTitle => Code?.Title ?? "(no script)";

        public string ScriptPath => Code?.Uri?.IsFile == true ? Code.Uri.LocalPath : null;

        public string Language => Code?.Language?.Id?.ToString() ?? "unknown";

        /// <summary>The fence language for code blocks: python or csharp.</summary>
        public string FenceLanguage
        {
            get
            {
                var id = Language.ToLowerInvariant();
                return id.Contains("python") ? "python" : id.Contains("csharp") || id.Contains("c#") ? "csharp" : "";
            }
        }

        /// <summary>Adds text at the end of the Script Editor's Terminal (UI thread): a C#
        /// logpoint's line.</summary>
        public void AppendTerminal(string text)
        {
            try
            {
                var console = Editor.GetType().GetProperty("Console", Any)?.GetValue(Editor);
                var tray = console?.GetType().GetProperty("Terminal", Any)?.GetValue(console);
                if (tray?.GetType().GetProperty("TextArea", Any)?.GetValue(tray) is Eto.Forms.TextArea area)
                    area.Append(text, true);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "terminal: {0}", Log.Describe(ex));
            }
        }

        /// <summary>The Script Editor's Terminal output (prints, errors).</summary>
        public string TerminalText
        {
            get
            {
                var console = Editor.GetType().GetProperty("Console", Any)?.GetValue(Editor);
                var tray = console?.GetType().GetProperty("Terminal", Any)?.GetValue(console);
                var area = tray?.GetType().GetProperty("TextArea", Any)?.GetValue(tray);
                return area?.GetType().GetProperties(Any).FirstOrDefault(p => p.Name == "Text")?.GetValue(area) as string ?? "";
            }
        }

        public async Task<string> SelectionAsync()
        {
            var context = EditContext;
            return context == null ? "" : await context.GetSelected() ?? "";
        }

        public async Task<(int Line, int Column)> CursorAsync()
        {
            var context = EditContext;
            if (context == null)
                return (0, 0);
            var position = await context.GetPosition();
            return (position.LineNumber, position.ColumnNumber);
        }

        /// <summary>Replaces the selection through Monaco -- Ctrl+Z in the editor undoes it.</summary>
        public async Task<bool> ReplaceSelectionAsync(string text)
        {
            var monaco = Monaco;
            if (monaco == null)
                return false;
            await monaco.ReplaceSelectedTextAsync(text);
            return true;
        }

        /// <summary>Replaces the whole script text (the editor follows its model).</summary>
        public bool SetScriptText(string text)
        {
            var code = Code;
            if (code == null || code.Text.IsReadOnly)
                return false;
            code.Text.Set(text);
            return true;
        }

        /// <summary>Where the debugger is paused, e.g. "line 31 of watch_test.py".</summary>
        public string PausedAt
        {
            get
            {
                var frame = DebugControls?.Results?.CurrentThread?.CurrentFrame;
                if (!IsPaused || frame == null)
                    return null;
                var reference = frame.Reference;
                var file = reference?.Uri?.IsFile == true ? System.IO.Path.GetFileName(reference.Uri.LocalPath) : reference?.Uri?.ToString();
                return string.Format("line {0} of {1}", reference?.Position.LineNumber ?? 0, file ?? "the script");
            }
        }

        /// <summary>Runs <paramref name="work"/> on Rhino's UI thread and waits for it --
        /// from a thread that isn't it (the MCP server's).</summary>
        public static Task<T> OnUiThread<T>(Func<Task<T>> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            RhinoApp.InvokeOnUiThread(new Action(async () =>
            {
                try
                {
                    done.SetResult(await work());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }));
            return done.Task;
        }
    }
}
