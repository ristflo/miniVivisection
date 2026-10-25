using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Eto.Forms;
using Rhino;
using Rhino.Runtime.Code.Execution.Debugging;
using Color = System.Drawing.Color;

namespace MiniVivisection
{
    /// <summary>
    /// While the Script Editor's debugger is paused, the geometry of the name under the
    /// mouse in the code (<c>crv</c>, <c>crv.PointAtStart</c>, <c>curves[0]</c>) is shown
    /// in the viewports until the mouse moves on.
    ///
    /// The code view is Monaco in a WebView (<c>Ed.Eto.Ed</c>, an Eto WebView): a small
    /// script registers Monaco's <c>onMouseMove</c> and keeps the name under the mouse;
    /// a UI timer reads it five times a second (<c>WebView.ExecuteScriptAsync</c>), and
    /// a new name is evaluated in the paused frame like a watch expression -- on the
    /// paused thread -- and drawn highlighted (WatchConduit's hover slot).
    /// MiniVivisection HoverPreview=On/Off.
    /// </summary>
    internal sealed class HoverPreview
    {
        // A function body: Script() runs it as a function expression (the editor's own
        // WebView handler, Ed.Eto.Wpf.EdHandler, hands scripts to WebView2 unchanged --
        // a bare `return` would be a syntax error there).
        private const string InstallScript = @"
if (window.__swvHover) return 'ok';
" + CodeViewScripts.FindEditor + @"var contentText = (window.monaco && monaco.editor.MouseTargetType) ? monaco.editor.MouseTargetType.CONTENT_TEXT : 6;
var state = { word: '', line: 0 };
window.__swvHover = state;
var part = /[A-Za-z0-9_.\[\]]/;
// The tooltip (values, links): the mouse on it keeps the name, so the geometry stays.
function inTooltip(element) { return !!(element && element.closest && element.closest('.monaco-hover')); }
function clear() { state.word = ''; state.line = 0; }
editor.onMouseMove(function (e) {
  if (inTooltip(e.target && e.target.element)) return;
  var word = '';
  var lineNumber = 0;
  var target = e.target;
  if (target && target.position && target.type === contentText) {
    var line = editor.getModel().getLineContent(target.position.lineNumber);
    var i = target.position.column - 1;
    if (i < line.length && /\w/.test(line[i])) {
      var start = i;
      while (start > 0 && part.test(line[start - 1])) start--;
      var end = i;
      while (end < line.length && /\w/.test(line[end])) end++;
      word = line.substring(start, end);
      lineNumber = target.position.lineNumber;
    }
  }
  state.word = word;
  state.line = lineNumber;
});
editor.onMouseLeave(function (e) {
  var to = e && e.event && e.event.browserEvent ? e.event.browserEvent.relatedTarget : null;
  if (!inTooltip(to)) clear();
});
// From the tooltip out of the code view (a tooltip can reach past the editor's edge).
document.addEventListener('mouseover', function (e) {
  var node = editor.getDomNode();
  if (!inTooltip(e.target) && node && !node.contains(e.target)) clear();
}, true);
document.documentElement.addEventListener('mouseleave', clear);
return 'ok';";

        // "line<TAB>word": the same name can mean different variables on different lines.
        private const string ReadScript = "return window.__swvHover ? window.__swvHover.line + '\\t' + window.__swvHover.word : '';";

        private static HashSet<string> Keywords => HoverText.Keywords;

        private static readonly ConditionalWeakTable<object, HoverPreview> s_previews = new ConditionalWeakTable<object, HoverPreview>();
        private static readonly Color HoverColor = Color.FromArgb(255, 235, 59);
        private static bool s_reportedInstall;

        private readonly ScriptEditorHook _hook;
        private readonly ScriptEditorAccess _access;
        private readonly UITimer _timer;
        private WebView _installedIn;
        private string _shown = "";
        private bool _busy;

        private HoverPreview(object editor, ScriptEditorHook hook)
        {
            _hook = hook;
            _access = new ScriptEditorAccess(editor);
            _timer = new UITimer { Interval = 0.2 };
            _timer.Elapsed += OnTick;
            _timer.Start();
            EditorWindows.WhenClosed(editor, () => _timer.Stop());
        }

        public static bool Enabled
        {
            get => MiniVivisectionSettings.HoverPreview;
            set => MiniVivisectionSettings.HoverPreview = value;
        }

        /// <summary>While the code view's context menu is open: the preview stays as it is
        /// -- the mouse leaves the code for the menu, which would clear it.</summary>
        public static bool Held { get; set; }

        public static void TryAdd(object editor, ScriptEditorHook hook)
        {
            if (!s_previews.TryGetValue(editor, out _))
                s_previews.Add(editor, new HoverPreview(editor, hook));
        }

        private async void OnTick(object sender, EventArgs e)
        {
            if (_busy)
                return;
            var controls = _access.DebugControls;
            var web = _access.Monaco as WebView;
            if (!Enabled || controls == null || controls.State != DebugControlsState.Paused || web == null)
            {
                Hide();
                return;
            }
            if (Held)
                return;
            _busy = true;
            try
            {
                if (!ReferenceEquals(web, _installedIn))
                {
                    var installed = await Script(web, InstallScript);
                    if (installed != "ok")
                    {
                        if (!s_reportedInstall)
                        {
                            s_reportedInstall = true;
                            Log.Warning(LogArea.Hover, "hover preview unavailable: {0}", installed ?? "no answer from the code view");
                        }
                        return;
                    }
                    _installedIn = web;
                    Log.Info(LogArea.Hover, "hover preview ready: hover over a name in the code while paused.");
                }
                var read = await Script(web, ReadScript) ?? "";
                var tab = read.IndexOf('\t');
                var word = (tab < 0 ? read : read.Substring(tab + 1)).Trim().Trim('.');
                int.TryParse(tab < 0 ? "" : read.Substring(0, tab), out var line);
                var shown = line + ":" + word;
                if (shown == _shown)
                    return;
                if (word.Length == 0 || Keywords.Contains(word) || char.IsDigit(word[0]))
                {
                    Hide();
                    return;
                }
                _shown = shown;
                Show(controls, word, line, shown);
            }
            catch (Exception)
            {
                // the view is reloading; next tick
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>The file to scope a hovered name in (PythonBridge.EvalAt): null for the
        /// paused frame's own, the shown script's path for another file, "" when that
        /// can't be told (an unsaved script that isn't the paused one).</summary>
        internal static string FileFor(ScriptEditorAccess access, IDebugControls controls)
        {
            var codeUri = access.Code?.Uri;
            var pausedUri = controls.Results?.CurrentThread?.CurrentFrame?.Reference?.Uri;
            if (codeUri != null && pausedUri != null && BreakpointSpecs.Key(codeUri) == BreakpointSpecs.Key(pausedUri))
                return null; // the same file, however its address is written
            return codeUri?.IsFile == true ? codeUri.LocalPath : "";
        }

        /// <summary>Runs a function body in the code view and returns its string result:
        /// as a function expression, WebView2's JSON answer decoded; for a handler that
        /// wraps scripts in a function itself (Eto's own), with `return` in front.</summary>
        internal static async Task<string> Script(WebView web, string body)
        {
            var expression = "(function () {" + body + "\n})()";
            var result = Decode(await web.ExecuteScriptAsync(expression));
            if (result == null)
                result = Decode(await web.ExecuteScriptAsync("return " + expression));
            return result;
        }

        private static string Decode(string result)
        {
            if (string.IsNullOrEmpty(result) || result == "null" || result == "undefined")
                return null;
            if (result.StartsWith("\"", StringComparison.Ordinal))
            {
                try
                {
                    return JsonSerializer.Deserialize<string>(result);
                }
                catch (JsonException)
                {
                    return result.Trim('"');
                }
            }
            return result;
        }

        private void Show(IDebugControls controls, string expression, int line, string shown)
        {
            // Python: the name as it is at the hovered line (a global crv, not the paused
            // function's local crv); the paused frame's file, or the hovered one.
            var python = PythonBridge.IsRunning && BreakpointUi.IsPython(_access.Code?.Uri, _access.Language);
            var filename = FileFor(_access, controls);
            var reached = _hook.RunOnPausedThread(controls, () =>
            {
                List<WatchItem> items = null;
                object value = null;
                bool found;
                if (python && line > 0 && filename != "")
                {
                    try
                    {
                        value = PythonBridge.EvalAt(expression, line, filename);
                        found = true;
                    }
                    catch (Exception ex)
                    {
                        found = false;
                        Log.Trace(LogArea.Hover, "{0} at line {1}: {2}", expression, line, ScriptEditorHook.FirstLine(ex));
                    }
                }
                else
                    found = !python && ScriptEditorHook.TryEvaluate(controls, expression, out value, out _);
                if (found)
                    items = WatchGeometry.Drawable(value, "auto", true, out _, out _, out _);
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    if (_shown != shown)
                        return; // the mouse moved on meanwhile
                    if (_hook.Conduit.SetHover(items != null && items.Count > 0 ? new WatchedValue(expression, HoverColor, items) : null))
                        RhinoDoc.ActiveDoc?.Views.Redraw(); // only when something changed
                }));
            });
            if (!reached && _hook.Conduit.SetHover(null))
                RhinoApp.InvokeOnUiThread(new Action(() => RhinoDoc.ActiveDoc?.Views.Redraw())); // the earlier hover goes
        }

        private void Hide()
        {
            if (_shown.Length == 0)
                return;
            _shown = "";
            if (_hook.Conduit.SetHover(null))
                RhinoDoc.ActiveDoc?.Views.Redraw();
        }
    }
}
