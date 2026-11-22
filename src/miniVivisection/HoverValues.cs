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

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_editorsSeen =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

#if RHINO9
        // Rhino 9: Ed.Eto.Ed.RegisterHoverInfoProviderAsync(HoverInfoProvider provider), where
        // HoverInfoProvider is Task<string[]>(int offset) -- only the offset; the text is
        // Monaco's (IEd.GetTextAsync, the text getOffsetAt counts in). Called by RhinoCodeEditor's
        // MonacoEditor.BeginSupport for languages with hover support (CPython, C#; not IronPython 2).
        public static void TryInstall(ScriptEditorHook hook)
        {
            s_hook = hook;
            if (s_harmony != null || s_failed)
                return;
            try
            {
                var method = typeof(global::Ed.Eto.Ed).GetMethod("RegisterHoverInfoProviderAsync", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(global::Ed.Core.HoverInfoProvider) }, null)
                    ?? throw new MissingMethodException("Ed.Eto.Ed", "RegisterHoverInfoProviderAsync");
                var harmony = new Harmony("minivivisection.hovervalues");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(HoverValues), nameof(RegisterPrefix9)));
                var remove = typeof(global::Ed.Eto.Ed).GetMethod("RemoveHoverInfoProviderAsync", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (remove != null) // phase 2 (1.4): forget a provider the plug-in registered itself
                    harmony.Patch(remove, postfix: new HarmonyMethod(typeof(HoverValues), nameof(RemovePostfix9)));
                s_harmony = harmony;
                Log.Debug(LogArea.Hover, "Ed.RegisterHoverInfoProviderAsync patched: values in the code's tooltips (Rhino 9)");
            }
            catch (Exception ex)
            {
                s_failed = true;
                UserLog.Warning("Values in the code's tooltips aren't available: {0}", ex.Message);
            }
        }

        /// <summary>The providers made here (Wrap9): never wrapped twice.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Delegate, object> s_wrappers =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Delegate, object>();

        /// <summary>For the live test: whether <paramref name="provider"/> is the plug-in's wrapper.</summary>
        internal static bool IsWrapped(Delegate provider) => provider != null && s_wrappers.TryGetValue(provider, out _);

        /// <summary>Prefix of Ed.RegisterHoverInfoProviderAsync: the editor's provider wrapped. A
        /// code view where the plug-in registered its own provider (1.4) keeps that one Monaco
        /// provider: only the host's callback is replaced (no second ed.BeginHovers()).</summary>
        private static bool RegisterPrefix9(object __instance, ref global::Ed.Core.HoverInfoProvider __0, ref Task __result)
        {
            if (__0 != null && !IsWrapped(__0))
                __0 = Wrap9(__instance, __0);
            if (__instance is global::Ed.Eto.Ed ed && s_ownProvider.TryGetValue(ed, out _) && ed.Handler is global::Ed.Eto.Ed.IHandler handler)
            {
                handler.RegisterHoverInfoProvider(__0);
                __result = Task.CompletedTask;
                return false;
            }
            return true;
        }

        private static global::Ed.Core.HoverInfoProvider Wrap9(object ed, global::Ed.Core.HoverInfoProvider original)
        {
            global::Ed.Core.HoverInfoProvider wrapped = offset => Provide9(ed, original, offset);
            s_wrappers.Add(wrapped, null);
            return wrapped;
        }

        private static async Task<string[]> Provide9(object ed, global::Ed.Core.HoverInfoProvider original, int offset)
        {
            var own = Own9(original, offset); // the editor's own (type, docs) at the same time, below the value
            string mine = null;
            try
            {
                // The whole text only while paused here: the cheap checks first.
                if (PausedFor(ed) && ed is global::Ed.Core.IEd monaco)
                    mine = await ValueTip(ed, await monaco.GetTextAsync(), offset);
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
            return parts.ToArray();
        }

        private static async Task<string[]> Own9(global::Ed.Core.HoverInfoProvider original, int offset)
        {
            if (original == null)
                return Array.Empty<string>();
            try
            {
                return await original(offset) ?? Array.Empty<string>();
            }
            catch (Exception ex)
            {
                Log.Trace(LogArea.Hover, "the editor's own tooltip: {0}", ScriptEditorHook.FirstLine(ex));
                return Array.Empty<string>();
            }
        }

        /// <summary>Whether the editor showing <paramref name="ed"/> is paused (and the option is on).</summary>
        private static bool PausedFor(object ed)
        {
            var hook = s_hook;
            if (!MiniVivisectionSettings.HoverValues || hook == null)
                return false;
            var editor = hook.EditorOfMonaco(ed);
            return editor != null && ScriptEditorHook.DebugControlsOf(editor)?.State == DebugControlsState.Paused;
        }

        /// <summary>The hover provider a code view's host calls now: Ed.Eto.Wpf.EdHandler.m_host
        /// (EdHost) .Handler (Ed.Core.EdProxyHost) .m_hover. Null when none is registered.</summary>
        internal static global::Ed.Core.HoverInfoProvider CurrentHoverProvider(object handler)
        {
            if (handler == null)
                return null;
            var host = FindField(handler.GetType(), "m_host")?.GetValue(handler);
            var proxyHost = host?.GetType().GetProperty("Handler", BindingFlags.Public | BindingFlags.Instance)?.GetValue(host) as global::Ed.Core.EdProxyHost;
            return proxyHost == null ? null : FindField(typeof(global::Ed.Core.EdProxyHost), "m_hover")?.GetValue(proxyHost) as global::Ed.Core.HoverInfoProvider;
        }

        /// <summary>The scripts already open in <paramref name="editor"/> (a Rhino 9 window) when
        /// the plug-in first sees it: their provider was registered before the prefix could wrap
        /// it. Wrapped and given back to the host only (Ed.IHandler.RegisterHoverInfoProvider) --
        /// Ed.RegisterHoverInfoProviderAsync would run ed.BeginHovers() again, and Monaco would
        /// show every tooltip twice. Scripts opened later go through the prefix. Once per window.</summary>
        public static void RegisterOpenScripts(object editor)
        {
            if (s_harmony == null || s_editorsSeen.TryGetValue(editor, out _))
                return;
            s_editorsSeen.Add(editor, null);
            try
            {
                var codes = Rhino9Editor.ModelOf(editor)?.Codes;
                if (codes == null)
                    return;
                foreach (var item in codes.GetItems().OfType<Rhino.Runtime.Code.Editor.Aspects.ICodeEditItem>().ToList())
                {
                    if (!(item.Editor?.GetWidget<Eto.Forms.Control>() is global::Ed.Eto.Ed ed) || !(ed.Handler is global::Ed.Eto.Ed.IHandler handler))
                        continue;
                    var current = CurrentHoverProvider(handler);
                    if (current == null || IsWrapped(current))
                        continue; // none yet (the prefix wraps it when it comes), or wrapped already
                    handler.RegisterHoverInfoProvider(Wrap9(ed, current));
                    Log.Debug(LogArea.Hover, "tooltip values for a script that was open before the plug-in");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Hover, "tooltip values for the open scripts: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Code views where the plug-in registered the (only) Monaco hover provider itself.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_ownProvider =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

        /// <summary>Scan (UI thread): while <paramref name="editor"/> is paused, its shown code view
        /// gets a provider when the editor registered none (IronPython 2: no language hover in
        /// Rhino 9; or the editor's Completion option off). The editor's later registration then
        /// only replaces the host's callback (RegisterPrefix9); its Remove forgets ours (RemovePostfix9).</summary>
        public static async void EnsureProvider(object editor)
        {
            if (s_harmony == null || !MiniVivisectionSettings.HoverValues)
                return;
            var access = new ScriptEditorAccess(editor);
            if (!access.IsPaused || !(access.Monaco is global::Ed.Eto.Ed ed) || s_ownProvider.TryGetValue(ed, out _)
                || !(ed.Handler is global::Ed.Eto.Ed.IHandler handler) || CurrentHoverProvider(handler) != null)
                return;
            try
            {
                var registering = ed.RegisterHoverInfoProviderAsync(Wrap9(ed, null)); // through the prefix: a wrapper stays as it is
                s_ownProvider.Add(ed, null); // after the call: the prefix above must not skip ed.BeginHovers() for this one
                await registering;
                Log.Debug(LogArea.Hover, "a hover provider for a code view without one ({0})", access.Language);
            }
            catch (Exception ex)
            {
                s_ownProvider.Remove(ed);
                Log.Debug(LogArea.Hover, "own hover provider: {0}", Log.Describe(ex));
            }
        }

        private static void RemovePostfix9(object __instance) => s_ownProvider.Remove(__instance); // ed.EndHovers() disposed Monaco's provider
#else
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

#endif

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
#if RHINO9
            if (!MiniVivisectionSettings.HoverValues || hook == null || code == null || !(ed is global::Ed.Core.IEd monaco))
                return null;
#else
            if (!MiniVivisectionSettings.HoverValues || hook == null || code == null || !(ed is global::Ed.Eto.IEd monaco))
                return null;
#endif
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
#if RHINO9
            var lines = code.Split('\n'); // Rhino 9's IEd has no GetLineTextAsync: the code is here
            string lineText = line - 1 < lines.Length ? lines[line - 1].TrimEnd('\r') : "";
#else
            string lineText = await monaco.GetLineTextAsync(line);
#endif
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
            var ironPython = !python && ScriptEditorHook.IsIronPythonPause(controls);
            if (python)
            {
                tip = HoverText.InfoForPython(expression, line, filename);
                value = tip.Value ?? (MiniVivisectionSettings.TooltipActions ? PythonBridge.EvalAt(expression, line, filename) : null);
            }
            else
            {
                // C#, IronPython 2: the value in the paused frame (an IronPython expression through its engine)
                if (!ScriptEditorHook.TryEvaluate(controls, expression, out value, out _))
                    return null;
                tip = HoverText.InfoForValue(value, ironPython || ScriptKinds.Of(controls.Results?.CurrentThread?.CurrentFrame?.Reference?.Uri) == ScriptKind.IronPython ? "python" : "csharp");
            }
            var was = MiniVivisectionSettings.ShowChanges ? ValueHistory.WasFor(controls, expression, line, tip.Text) : null;
            var links = new List<(string Label, string Href)>();
            var whole = (tip.Text ?? "").StartsWith("def ") || (tip.Text ?? "").StartsWith("class ") || (tip.Text ?? "").StartsWith("import ");
            if (MiniVivisectionSettings.TooltipActions && !whole && !IsPlain(value)) // titles: HoverText.LinkTitle
            {
                links.Add(("Watch in Rhino", HoverText.LinkFor("watch", expression, line)));
                if (python && !EditSession.ModeOf(value, "auto").StartsWith("readonly", StringComparison.Ordinal) || ironPython && EditSession.IsGeometryToEdit(value))
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
