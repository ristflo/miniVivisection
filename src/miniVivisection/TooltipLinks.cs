using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Eto.Forms;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// The links under a value in the code's tooltip (HoverValues): "Watch in Rhino",
    /// "Edit in Rhino", "Bake", "Tree". They point to <see cref="Prefix"/> addresses,
    /// which are never loaded or shown (their title says what they do; once rendered,
    /// the script moves the address out of href, so WebView2's status bar has none to
    /// show): a small script in the code view catches clicks on them
    /// before Monaco opens them (a click listener on the document, capture phase) and
    /// keeps them; a UI timer reads them four times a second while paused and runs the
    /// action on the paused script. Options: "Links in the tooltips".
    /// </summary>
    internal sealed class TooltipLinks
    {
        public const string Prefix = HoverText.LinkPrefix;

        private const string InstallScript = @"
if (window.__swvLinks) return 'ok';
var state = { clicks: [] };
var prefix = 'https://minivivisection.invalid/';
function ours(a) {
  var href = a.getAttribute('data-swv') || a.getAttribute('data-href') || a.getAttribute('href') || '';
  return href.indexOf(prefix) === 0 ? href : null;
}
function linkOf(e) {
  var a = e.target && e.target.closest ? e.target.closest('a') : null;
  return a ? ours(a) : null;
}
// The address is never shown: rendered links keep it in data-swv only (no href: no
// address in WebView2's status bar), and keep their title (what they do).
function hide(root) {
  if (!root || !root.querySelectorAll) return;
  var links = root.tagName === 'A' ? [root] : root.querySelectorAll('a');
  for (var i = 0; i < links.length; i++) {
    var a = links[i];
    var href = ours(a);
    if (!href || a.getAttribute('data-swv')) continue;
    a.setAttribute('data-swv', href);
    a.removeAttribute('href');
    a.removeAttribute('data-href');
    if (!a.getAttribute('title') || a.getAttribute('title').indexOf(prefix) === 0) a.removeAttribute('title');
    a.style.cursor = 'pointer';
  }
}
new MutationObserver(function (changes) {
  changes.forEach(function (c) { for (var i = 0; i < c.addedNodes.length; i++) hide(c.addedNodes[i]); });
}).observe(document.body, { childList: true, subtree: true });
hide(document.body);
['mousedown', 'mouseup', 'click', 'auxclick'].forEach(function (type) {
  document.addEventListener(type, function (e) {
    var href = linkOf(e);
    if (!href) return;
    e.preventDefault();
    e.stopPropagation();
    if (e.stopImmediatePropagation) e.stopImmediatePropagation();
    if (type === 'click') state.clicks.push(href);
  }, true);
});
state.take = function () { var c = state.clicks; state.clicks = []; return JSON.stringify(c); };
window.__swvLinks = state;
return 'ok';";

        private const string TakeScript = "return window.__swvLinks ? window.__swvLinks.take() : '[]';";

        private static readonly ConditionalWeakTable<object, TooltipLinks> s_links = new ConditionalWeakTable<object, TooltipLinks>();

        private readonly object _editor;
        private readonly ScriptEditorHook _hook;
        private readonly ScriptEditorAccess _access;
        private readonly UITimer _timer;
        private WebView _installedIn;
        private bool _busy;

        private TooltipLinks(object editor, ScriptEditorHook hook)
        {
            _editor = editor;
            _hook = hook;
            _access = new ScriptEditorAccess(editor);
            _timer = new UITimer { Interval = 0.25 };
            _timer.Elapsed += OnTick;
            _timer.Start();
            EditorWindows.WhenClosed(editor, () => _timer.Stop());
        }

        public static void TryAdd(object editor, ScriptEditorHook hook)
        {
            if (!s_links.TryGetValue(editor, out _))
                s_links.Add(editor, new TooltipLinks(editor, hook));
        }

        private async void OnTick(object sender, EventArgs e)
        {
            if (_busy || !MiniVivisectionSettings.TooltipActions)
                return;
            var controls = _access.DebugControls;
            if (controls == null || controls.State != DebugControlsState.Paused || !(_access.Monaco is WebView web))
                return;
            _busy = true;
            try
            {
                if (!ReferenceEquals(web, _installedIn))
                {
                    if (await HoverPreview.Script(web, InstallScript) != "ok")
                        return;
                    _installedIn = web;
                    Log.Debug(LogArea.Hover, "tooltip links ready");
                }
                var taken = await HoverPreview.Script(web, TakeScript);
                if (string.IsNullOrEmpty(taken) || taken == "[]")
                    return;
                foreach (var href in JsonSerializer.Deserialize<List<string>>(taken))
                    Run(controls, href);
            }
            catch (Exception ex)
            {
                Log.Trace(LogArea.Hover, "tooltip links: {0}", Log.Describe(ex)); // the view is reloading; next tick
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>A clicked link's action (UI thread).</summary>
        private void Run(IDebugControls controls, string href)
        {
            var uri = new Uri(href);
            var action = uri.AbsolutePath.Trim('/');
            var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split(new[] { '=' }, 2))
                .Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            if (!query.TryGetValue("t", out var token) || token != HoverText.LinkToken)
            {
                Log.Warning(LogArea.Hover, "ignored a tooltip link without this session's token: {0}", href);
                return;
            }
            if (!query.TryGetValue("e", out var expression) || string.IsNullOrWhiteSpace(expression))
                return;
            int.TryParse(query.TryGetValue("l", out var l) ? l : "0", out var line);
            Log.Info(LogArea.Hover, "tooltip link: {0} {1} (line {2})", action, expression, line);
            try
            {
                switch (action)
                {
                    case "watch":
                        if (_hook.AddRhinoWatch(controls, expression))
                            UserLog.Info("{0} added to the V Watch tab.", expression);
                        break;
                    case "edit":
                        _hook.Edits.Start(controls, expression);
                        break;
                    case "bake":
                        WatchActions.Bake(_hook, controls, expression);
                        break;
                    case "tree":
                        ValueTreePopup.Open(_editor, _hook, expression, line);
                        break;
                }
            }
            catch (Exception ex)
            {
                UserLog.Error("{0} {1} failed: {2}", action, expression, ScriptEditorHook.FirstLine(ex));
            }
        }
    }
}
