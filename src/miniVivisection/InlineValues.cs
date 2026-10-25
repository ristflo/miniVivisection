using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eto.Forms;
using Rhino;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Values at the ends of the lines while paused, as in VS Code (debug.inlineValues)
    /// and PyCharm: for each line of the paused function, from its start to the paused
    /// line, the paused frame's variables written on it, "count = 5, k = 2", in gray
    /// after the line; those changed since the last pause in another color (option
    /// "What changed since the last pause"). Monaco decorations with an <c>after</c>
    /// text, in the code view showing the paused file. Python scripts. At every pause it
    /// also gives ValueHistory the frame's variables, for "was" in the tooltips.
    /// Options: "Values at the ends of lines".
    /// </summary>
    internal static class InlineValues
    {
        // A function body (see HoverPreview.Script).
        private const string InstallScript = @"
if (window.__swvInline) return 'ok';
" + CodeViewScripts.FindEditor + @"if (!document.getElementById('swv-inline-style')) {
  var style = document.createElement('style');
  style.id = 'swv-inline-style';
  style.textContent = '.swv-inline{color:#8a8a8a;font-style:italic;opacity:.9}.swv-inline-changed{color:#d19a66;font-style:italic}';
  document.head.appendChild(style);
}
var state = { ids: [] };
state.set = function (list) {
  var model = editor.getModel();
  var decorations = list.map(function (m) {
    var line = Math.min(Math.max(1, m.line), model.getLineCount());
    var column = model.getLineMaxColumn(line);
    return { range: { startLineNumber: line, startColumn: Math.max(1, column - 1), endLineNumber: line, endColumn: column },
             options: { description: 'swv-inline-value', showIfCollapsed: true, after: { content: '   ' + m.text, inlineClassName: m.changed ? 'swv-inline-changed' : 'swv-inline' } } };
  });
  state.ids = editor.deltaDecorations(state.ids, decorations);
  return 'ok';
};
// What is drawn: the decorations kept, and the texts on screen (visible lines only).
state.check = function () {
  return state.ids.length + ' kept, ' + document.querySelectorAll('.swv-inline, .swv-inline-changed').length + ' on screen';
};
window.__swvInline = state;
return 'ok';";

        /// <summary>At a pause (paused thread): the frame's variables for ValueHistory, and
        /// the values shown at the line ends.</summary>
        /// <param name="newPause">False: the setting changed during this pause -- shown again,
        /// "was" compared with the previous pause as before.</param>
        public static void AtPause(IDebugControls controls, object editor, bool newPause = true)
        {
            if (!(MiniVivisectionSettings.InlineValues || MiniVivisectionSettings.ShowChanges))
                return;
            if (!PythonBridge.IsRunning || editor == null)
            {
                Once("not shown: {0}", !PythonBridge.IsRunning ? "Python isn't loaded" : "the Script Editor isn't known yet");
                return;
            }
            var frame = controls.Results?.CurrentThread?.CurrentFrame;
            var uri = frame?.Reference?.Uri;
            if (frame == null || frame.Event == Rhino.Runtime.Code.Execution.ExecEvent.Exception)
                return;
            if (!BreakpointUi.IsPython(uri, null))
            {
                Log.Debug(LogArea.Hover, "values at the line ends: {0} isn't a Python 3 script", uri); // C#, IronPython 2: quietly none
                return;
            }
            (string Key, int First, int Current, List<(string Name, string Text, object Value)> Items) found;
            try
            {
                found = PythonBridge.InlineValues();
            }
            catch (Exception ex)
            {
                Once("can't read the paused frame's variables: {0}", ScriptEditorHook.FirstLine(ex));
                return;
            }
            var (key, first, current, items) = found;
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, text, value) in items)
                texts[name] = text ?? HoverText.ShortValue(value, "python");
            if (newPause)
                ValueHistory.NewPause(controls, key, first, texts);
            if (!MiniVivisectionSettings.InlineValues)
                return;
            var changed = MiniVivisectionSettings.ShowChanges
                ? new HashSet<string>(texts.Where(t => ValueHistory.Changed(controls, t.Key, t.Value)).Select(t => t.Key), StringComparer.Ordinal)
                : new HashSet<string>();
            if (!newPause)
                Log.Info(LogArea.Hover, "values at the line ends drawn again (a setting changed): {0} variable(s), {1} marked changed (\"What changed\" {2})",
                    texts.Count, changed.Count, MiniVivisectionSettings.ShowChanges ? "on" : "off");
            RhinoApp.InvokeOnUiThread(new Action(() => Show(editor, uri, first, current, texts, changed, fresh: !newPause)));
        }

        /// <param name="fresh">After a setting change: the old values are removed first, so
        /// every line is drawn anew (Monaco may keep a line's drawing when only the color of
        /// the same text changes).</param>
        private static async void Show(object editor, Uri uri, int first, int current, Dictionary<string, string> texts, HashSet<string> changed, bool fresh = false)
        {
            try
            {
                var access = new ScriptEditorAccess(editor);
                if (!(access.Monaco is WebView web))
                {
                    Once("not shown: no code view");
                    return;
                }
                // The same file, however its address is written (the editor's script and the
                // debugger's frame needn't spell it alike).
                if (BreakpointSpecs.Key(access.Code?.Uri) != BreakpointSpecs.Key(uri))
                {
                    Log.Debug(LogArea.Hover, "inline values: the shown script ({0}) isn't the paused file ({1})", access.Code?.Uri, uri);
                    return;
                }
                var lines = InlineText.Lines(access.ScriptText, first, current, texts, changed);
                var installed = await HoverPreview.Script(web, InstallScript);
                if (installed != "ok")
                {
                    Once("not shown: {0}", installed ?? "no answer from the code view");
                    return;
                }
                var list = lines.Select(l => new Dictionary<string, object> { ["line"] = l.Line, ["text"] = l.Text, ["changed"] = l.Changed }).ToList();
                if (fresh)
                    await HoverPreview.Script(web, "return window.__swvInline.set([]);");
                var set = await HoverPreview.Script(web, "return window.__swvInline.set(JSON.parse(" + JsonSerializer.Serialize(JsonSerializer.Serialize(list)) + "));");
                if (set != "ok")
                    Once("not shown: the code view answered {0}", set ?? "nothing");
                else if (!s_shownOnce)
                {
                    s_shownOnce = true;
                    await System.Threading.Tasks.Task.Delay(300); // drawn on the next frame
                    var check = await HoverPreview.Script(web, "return window.__swvInline.check();");
                    Log.Info(LogArea.Hover, "values at the line ends shown: {0} line(s) of lines {1}-{2}, {3} variable(s); code view: {4}", lines.Count, first, current, texts.Count, check);
                }
                else
                    Log.Debug(LogArea.Hover, "inline values: {0} line(s), {1} variable(s), lines {2}-{3}", lines.Count, texts.Count, first, current);
            }
            catch (Exception ex)
            {
                Once("not shown: {0}", Log.Describe(ex));
            }
        }

        private static readonly HashSet<string> s_reported = new HashSet<string>(StringComparer.Ordinal);
        private static bool s_shownOnce;

        /// <summary>Why no values show, in the detailed log at Warning -- once per reason.</summary>
        private static void Once(string format, params object[] args)
        {
            var text = string.Format(format, args);
            lock (s_reported)
            {
                if (!s_reported.Add(text))
                    return;
            }
            Log.Warning(LogArea.Hover, "values at the line ends: {0}", text);
        }

        /// <summary>The script continued or ended: the values go (any thread).</summary>
        public static void Clear(object editor)
        {
            if (editor == null)
                return;
            RhinoApp.InvokeOnUiThread(new Action(async () =>
            {
                try
                {
                    if (new ScriptEditorAccess(editor).Monaco is WebView web)
                        await HoverPreview.Script(web, "return window.__swvInline ? window.__swvInline.set([]) : 'none';");
                }
                catch (Exception ex)
                {
                    Log.Trace(LogArea.Hover, "inline values cleared: {0}", Log.Describe(ex));
                }
            }));
        }
    }
}
