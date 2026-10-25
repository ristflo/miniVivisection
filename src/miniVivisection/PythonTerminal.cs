using System;
using System.Linq;
using System.Reflection;
using Eto.Forms;
using Rhino;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// A Python prompt in the Script Editor's "Python" tab (ConsoleTabs), console style
    /// (TerminalView). While the editor's debugger is paused, input runs in the paused
    /// frame -- its variables, modules and functions; assignments change the script's
    /// variables -- and the watch drawing is refreshed. Otherwise input runs in the
    /// terminal's own namespace. Python prompt behavior: expression results are
    /// printed, blocks continue with "...", an empty line ends a block. Input of
    /// several lines (pasted, or Shift+Enter) runs at once, like a notebook cell.
    /// </summary>
    internal sealed class PythonTerminal : Panel
    {
        private readonly object _editor;
        private readonly ScriptEditorHook _hook;
        private readonly TerminalView _view;
        private string _source = "";
        private string _shownStatus; // the last status line written
        private bool _helpersDefined;

        /// <summary>The Grasshopper helpers, defined in the prompt's own namespace (Grasshopper's
        /// prompts window; Options: "V Python: Grasshopper helpers").</summary>
        internal const string GrasshopperHelpers =
            "def ghcanvas():\n" +
            "    \"\"\"Grasshopper's canvas (None while its window isn't open).\"\"\"\n" +
            "    import Grasshopper\n" +
            "    return Grasshopper.Instances.ActiveCanvas\n" +
            "def ghdoc():\n" +
            "    \"\"\"The definition open in Grasshopper (a GH_Document), or None.\"\"\"\n" +
            "    canvas = ghcanvas()\n" +
            "    return canvas.Document if canvas is not None else None\n" +
            "def ghselected():\n" +
            "    \"\"\"The objects selected on the canvas.\"\"\"\n" +
            "    doc = ghdoc()\n" +
            "    return list(doc.SelectedObjects()) if doc is not None else []\n" +
            "def ghfind(name):\n" +
            "    \"\"\"The objects whose nickname (or else name) is name.\"\"\"\n" +
            "    doc = ghdoc()\n" +
            "    if doc is None:\n" +
            "        return []\n" +
            "    return [o for o in doc.Objects if o.NickName == name] or [o for o in doc.Objects if o.Name == name]\n";

        /// <param name="editor">The Script Editor it is in; null in Grasshopper's prompts window,
        /// where input runs in the paused frame of whichever Python script is paused (a Grasshopper
        /// component's), else in the prompt's own namespace with the Grasshopper helpers.</param>
        public PythonTerminal(object editor, ScriptEditorHook hook)
        {
            _editor = editor;
            _hook = hook;
            _view = new TerminalView(editor, wrap: false) { TabText = "    ", MultilineInput = true, InputLanguage = "python", InputColorsOn = () => MiniVivisectionSettings.ColorsPython };
            _view.Submitted += Submit;
            _view.Interrupted += () =>
            {
                if (_view.IsPrompting)
                {
                    _source = "";
                    _view.Write("KeyboardInterrupt\n");
                    _view.Input = "";
                    Prompt();
                }
            };
            if (editor == null)
            {
                Content = _view;
                _view.Write("Python prompt for Grasshopper (experimental, miniVivisection). While a Grasshopper Python component is paused, code runs in its paused frame.\n" +
                            (MiniVivisectionSettings.GhPythonHelpers ? "ghdoc() is the open definition, ghselected() the selected objects, ghcanvas() the canvas, ghfind('name') objects by nickname.\n" : "") +
                            "show(value, ...) draws in Rhino, hide(name), clear(), bake(value); help(show) for more.\n");
            }
            else
            {
                Content = Python2Notice.Above(_view, new Python2Notice(editor,
                    "This tab runs Python 3. The script being debugged is IronPython 2 (#! python2): its variables can't be read or changed from here."));
                _view.Write("Python prompt (experimental, miniVivisection). While the debugger is paused, code runs in the paused frame.\n" +
                            "Commands: show(value, ...) draws in Rhino, hide(name), clear(), bake(value), watch('expr'), edit('expr'); help(show) for more.\n");
            }
            UpdateStatus();
            Prompt();
        }

        /// <summary>For the screenshots and tests: a line typed at the prompt and run.</summary>
        internal void Type(string line) => _view.Type(line);

        public void FocusInput()
        {
            UpdateStatus();
            _view.FocusInput();
        }

        /// <summary>Paused/continued: says where code runs now, once per change.</summary>
        public void UpdateStatus()
        {
            var controls = PausedControls();
            var state = controls == null ? 0 : ScriptEditorHook.IsPythonPause(controls) ? 1 : 2;
            var ghRun = state == 0 ? null : GrasshopperRuns.PausedRun();
            var status = state == 1
                ? "[paused" + (ghRun != null ? " in Grasshopper " + ghRun : "") + ": code runs in the paused frame and can change the script's variables]\n"
                : state == 2
                    ? "[paused in a script that isn't Python 3 (IronPython 2 or C#): code runs in the terminal's own namespace, without its variables]\n"
                    : "[not paused: code runs in the terminal's own namespace]\n";
            if (_shownStatus == status)
                return;
            _shownStatus = status;
            _view.Write(status);
        }

        private IDebugControls PausedControls()
        {
            if (_editor == null) // Grasshopper's window: whichever script is paused, Python first
                return _hook.PausedControls().FirstOrDefault(ScriptEditorHook.IsPythonPause) ?? _hook.PausedControls().FirstOrDefault();
            var controls = ScriptEditorHook.DebugControlsOf(_editor);
            return controls != null && controls.State == DebugControlsState.Paused ? controls : null;
        }

        private void Prompt() => _view.Prompt(_source.Length == 0 ? ">>> " : "... ");

        private void Submit(string line)
        {
            // Several lines at the prompt run as a cell: Python's prompt mode takes one
            // statement at a time.
            var cell = _source.Length == 0 && line.Contains("\n");
            _source = _source.Length == 0 ? line : _source + "\n" + line;
            var source = _source;
            UpdateStatus();

            var controls = PausedControls();
            if (controls == null)
            {
                Show(Run(source, cell, inPausedFrame: false, controls: null));
            }
            else if (!ScriptEditorHook.IsPythonPause(controls))
            {
                _view.Write("[the paused script isn't Python 3 (IronPython 2 or C#): this runs in the tab's own Python 3 namespace, without its variables]\n");
                Show(Run(source, cell, inPausedFrame: false, controls: null));
            }
            else if (_hook.IsPausedThread(controls))
            {
                Show(Run(source, cell, inPausedFrame: true, controls));
            }
            else if (!FreePause.TryRunOnPausedThread(controls, () =>
                     {
                         var result = Run(source, cell, inPausedFrame: true, controls);
                         RhinoApp.InvokeOnUiThread(new Action(() => Show(result)));
                     }))
            {
                _source = "";
                _view.Write("The paused script runs on its own thread -- turn FreePause on (miniVivisection FreePause=On) and continue to the next pause.\n");
                Prompt();
            }
            // else: the paused thread answers within a moment
        }

        private (bool More, string Output) Run(string source, bool cell, bool inPausedFrame, IDebugControls controls)
        {
            try
            {
                if (!inPausedFrame && !PythonBridge.IsRunning)
                    return (false, "Python isn't started in this Rhino yet -- run any Python script once" + (_editor == null ? " (or a Grasshopper Python component)" : "") + ".\n");
                TerminalHost.Instance.Use(new ScriptEditorAccess(_editor ?? (controls == null ? null : _hook.EditorOf(controls))), _hook);
                if (_editor == null && !inPausedFrame && !_helpersDefined && MiniVivisectionSettings.GhPythonHelpers)
                {
                    PythonBridge.Exec(GrasshopperHelpers, false);
                    _helpersDefined = true;
                }
                var result = cell ? (More: false, Output: PythonBridge.Exec(source, inPausedFrame)) : PythonBridge.Run(source, inPausedFrame);
                if (inPausedFrame && !result.More)
                    _hook.Refresh(controls); // the watch drawing shows what the input changed
                return result;
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                return (false, ex.Message + "\n");
            }
        }

        private void Show((bool More, string Output) result)
        {
            if (!result.More)
                _source = "";
            _view.Write(result.Output);
            Prompt();
        }
    }
}
