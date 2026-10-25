using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// The code view's own tooltips, while paused: the value instead of the type alone.
    /// The editor gives Monaco a hover callback, <c>Ed.RegisterProvideHoverInfoAsync(Func&lt;
    /// string code, int offset, Task&lt;List&lt;string&gt;&gt;&gt;)</c>; Monaco calls it through
    /// WebView2 (<c>csHoverInfo.ProvideHoverInfo</c>) and shows each string as Markdown.
    /// For Python, <c>code</c> is the text up to the mouse; for C# the whole text. A
    /// Harmony prefix wraps the callback: while the editor's debugger is paused, the name
    /// under the mouse is evaluated on the paused thread -- scoped as at that line
    /// (PythonBridge.EvalAt) -- and its tooltip is HoverText's; otherwise, or when the
    /// name has no value there, the editor's own text. Options: "Values in tooltips".
    /// </summary>
    internal static class HoverValues
    {
        private const int WaitMilliseconds = 1500;

        private static Harmony s_harmony;
        private static bool s_failed;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_failed ? "the editor's tooltip method is missing" : "not installed";
        private static ScriptEditorHook s_hook;

        public static void TryInstall(ScriptEditorHook hook)
        {
            s_hook = hook;
            if (s_harmony != null || s_failed)
                return;
            try
            {
                var method = typeof(global::Ed.Eto.Ed).GetMethod("RegisterProvideHoverInfoAsync", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(Func<string, int, Task<List<string>>>) }, null)
                    ?? throw new MissingMethodException("Ed.Eto.Ed", "RegisterProvideHoverInfoAsync");
                var harmony = new Harmony("minivivisection.hovervalues");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(HoverValues), nameof(RegisterPrefix)));
                s_harmony = harmony;
                Log.Debug(LogArea.Hover, "Ed.RegisterProvideHoverInfoAsync patched: values in the code's tooltips");
            }
            catch (Exception ex)
            {
                s_failed = true;
                UserLog.Warning("Values in the code's tooltips aren't available: {0}", ex.Message);
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_editorsSeen =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

        /// <summary>The scripts already open in <paramref name="editor"/> when the plug-in
        /// first sees it (each a MonacoEditContext): their tooltip callback was registered
        /// before the prefix could wrap it (the plug-in was loaded with the Script Editor
        /// open). Their callback is swapped for the wrapped one in the WPF handler only
        /// (<c>RegisterProvideHoverInfo</c>, a new WebView2 host object) -- not through
        /// <c>Ed.RegisterProvideHoverInfoAsync</c>, which also runs the editor's script
        /// <c>ed.HookupHoverInfoProvider()</c> again: Monaco would get a second hover
        /// provider and show every tooltip twice. Scripts opened later register through
        /// the prefix. Once per editor (UI thread, Scan).</summary>
        public static void RegisterOpenScripts(object editor)
        {
            if (s_harmony == null || s_editorsSeen.TryGetValue(editor, out _))
                return;
            s_editorsSeen.Add(editor, null);
            try
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var codes = editor.GetType().GetProperty("Codes", any)?.GetValue(editor);
                var editors = codes?.GetType().GetProperty("Editors", any)?.GetValue(codes);
                var contexts = FindField(editors?.GetType(), "_editContexts")?.GetValue(editors) as System.Collections.IEnumerable;
                if (contexts == null)
                    return;
                foreach (var context in contexts.Cast<object>().ToList())
                {
                    var ed = context == null ? null : FindField(context.GetType(), "_monaco")?.GetValue(context) as global::Ed.Eto.Ed;
                    var hover = context?.GetType().GetMethod("GetCodeHoverAsync", any, null, new[] { typeof(string), typeof(int) }, null);
                    var handler = ed?.Handler;
                    var register = handler?.GetType().GetMethod("RegisterProvideHoverInfo", any, null, new[] { typeof(Func<string, int, Task<List<string>>>) }, null);
                    if (hover == null || register == null)
                        continue;
                    var callback = (Func<string, int, Task<List<string>>>)Delegate.CreateDelegate(typeof(Func<string, int, Task<List<string>>>), context, hover);
                    var wrapped = Wrap(ed, callback);
                    register.Invoke(handler, new object[] { wrapped });
                    Log.Debug(LogArea.Hover, "tooltip values for a script that was open before the plug-in");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Hover, "tooltip values for the open scripts: {0}", Log.Describe(ex));
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            return null;
        }

        private static void RegisterPrefix(object __instance, ref Func<string, int, Task<List<string>>> provideHoverInfo)
        {
            provideHoverInfo = Wrap(__instance, provideHoverInfo);
        }

        private static async Task<List<string>> Provide(object ed, Func<string, int, Task<List<string>>> original, string code, int offset)
        {
            // The editor's own (its language server's type and docs) at the same time: it
            // stays, below the value -- Monaco separates the parts with a line.
            var own = Own(original, code, offset);
            string mine = null;
            try
            {
                mine = await ValueTip(ed, code, offset);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Hover, "tooltip value: {0}", Log.Describe(ex));
            }
            if (mine == null)
                return await own;
            var parts = new List<string> { mine };
            if (await Task.WhenAny(own, Task.Delay(WaitMilliseconds)) == own && own.Status == TaskStatus.RanToCompletion && own.Result != null)
                parts.AddRange(own.Result.Where(p => !string.IsNullOrWhiteSpace(p)));
            return parts;
        }

        private static Func<string, int, Task<List<string>>> Wrap(object ed, Func<string, int, Task<List<string>>> original) =>
            (code, offset) => Provide(ed, original, code, offset);

        private static async Task<List<string>> Own(Func<string, int, Task<List<string>>> original, string code, int offset)
        {
            if (original == null)
                return new List<string>();
            try
            {
                return await original(code, offset) ?? new List<string>();
            }
            catch (Exception ex)
            {
                Log.Trace(LogArea.Hover, "the editor's own tooltip: {0}", ScriptEditorHook.FirstLine(ex));
                return new List<string>();
            }
        }

        private static async Task<string> ValueTip(object ed, string code, int offset)
        {
            var hook = s_hook;
            if (!MiniVivisectionSettings.HoverValues || hook == null || code == null || !(ed is global::Ed.Eto.IEd monaco))
                return null;
            var editor = hook.EditorOfMonaco(ed);
            if (editor == null)
                return null;
            var access = new ScriptEditorAccess(editor);
            var controls = access.DebugControls;
            if (controls == null || controls.State != DebugControlsState.Paused)
                return null;

            var upTo = code.Substring(0, Math.Max(0, Math.Min(offset, code.Length)));
            var line = 1;
            foreach (var c in upTo)
            {
                if (c == '\n')
                    line++;
            }
            var index = upTo.Length - (upTo.LastIndexOf('\n') + 1);
            string lineText = await monaco.GetLineTextAsync(line);
            var expression = HoverText.ExpressionAt(lineText, index);
            if (expression == null)
                return null;
            var call = HoverText.CallAt(lineText, index); // rg.Point3d(...) around the name

            var python = PythonBridge.IsRunning && BreakpointUi.IsPython(access.Code?.Uri, access.Language);
            var filename = HoverPreview.FileFor(access, controls);
            if (python && filename == "")
                return null; // a file that isn't on the paused call stack
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!hook.RunOnPausedThread(controls, () =>
            {
                try
                {
                    done.TrySetResult(BuildTip(controls, expression, line, filename, python, call));
                }
                catch (Exception ex)
                {
                    Log.Trace(LogArea.Hover, "{0} at line {1}: {2}", expression, line, ScriptEditorHook.FirstLine(ex));
                    done.TrySetResult(null);
                }
            }))
                return null;
            var finished = await Task.WhenAny(done.Task, Task.Delay(WaitMilliseconds));
            return finished == done.Task ? done.Task.Result : null;
        }

        /// <summary>The tooltip (paused thread): the value, its facts, what it was at the
        /// previous pause (option), and links -- Watch in Rhino for anything but plain
        /// numbers and text, Edit in Rhino and Bake for editable geometry, Tree (options).
        /// Null when the name has no value there.</summary>
        private static string BuildTip(IDebugControls controls, string expression, int line, string filename, bool python, string call = null)
        {
            HoverText.Tip tip;
            object value;
            if (python)
            {
                tip = HoverText.InfoForPython(expression, line, filename);
                value = tip.Value ?? (MiniVivisectionSettings.TooltipActions ? PythonBridge.EvalAt(expression, line, filename) : null);
            }
            else
            {
                if (!ScriptEditorHook.TryEvaluate(controls, expression, out value, out _))
                    return null;
                tip = HoverText.InfoForValue(value, ScriptKinds.Of(controls.Results?.CurrentThread?.CurrentFrame?.Reference?.Uri) == ScriptKind.IronPython ? "python" : "csharp");
            }
            var was = MiniVivisectionSettings.ShowChanges ? ValueHistory.WasFor(controls, expression, line, tip.Text) : null;
            var links = new List<(string Label, string Href)>();
            var whole = (tip.Text ?? "").StartsWith("def ") || (tip.Text ?? "").StartsWith("class ") || (tip.Text ?? "").StartsWith("import ");
            if (MiniVivisectionSettings.TooltipActions && !whole && !IsPlain(value)) // titles: HoverText.LinkTitle
            {
                links.Add(("Watch in Rhino", HoverText.LinkFor("watch", expression, line)));
                if (python && !EditSession.ModeOf(value, "auto").StartsWith("readonly", StringComparison.Ordinal))
                {
                    links.Add(("Edit in Rhino", HoverText.LinkFor("edit", expression, line)));
                    links.Add(("Bake", HoverText.LinkFor("bake", expression, line)));
                }
                if (MiniVivisectionSettings.TooltipTree)
                    links.Add(("Tree", HoverText.LinkFor("tree", expression, line)));
            }
            // The name is called here: the call's value -- rg.Point3d(...) -- to watch, when what's
            // called is a class (a constructor: makes a value, changes nothing; a watch is computed
            // again at every pause, so pts.append(...) or doc.Objects.AddPoint(...) get no link).
            if (python && call != null && MiniVivisectionSettings.TooltipActions && IsConstructor(call, line, filename))
                links.Add(("Watch " + HoverText.CallLabel(call), HoverText.LinkFor("watch", call, line)));
            return HoverText.Markdown(expression, tip, was, links);
        }

        /// <summary>Whether a call's callee is a class -- isinstance(callee, type) in the paused
        /// frame (paused thread).</summary>
        private static bool IsConstructor(string call, int line, string filename)
        {
            try
            {
                var callee = call.Substring(0, call.IndexOf('(')).TrimEnd();
                return PythonBridge.IsClassAt(callee, line, filename);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsPlain(object value) =>
            value == null || value is string || value is bool || value is char || value is decimal || (value.GetType().IsPrimitive);
    }
}
