using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// Claude's tools for the debugging process (the script-editor MCP server, next to
    /// ScriptEditorTools): what the editor knows -- problems, breakpoints, the V Watch
    /// tab, the plug-in's log -- and, with the Options page's "Claude may control the
    /// debugger" (on by default), breakpoints and stepping. Stepping runs the editor's own
    /// commands (as its buttons do), waits for the next pause or the end, and answers with
    /// where it stopped and which variables changed. Every step is shown in the Claude tab.
    /// </summary>
    internal static class DebugTools
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const string NoArguments = "{\"type\":\"object\",\"properties\":{}}";
        private const string BreakpointArguments = "{\"type\":\"object\",\"properties\":{\"line\":{\"type\":\"integer\",\"description\":\"1-based line number in the open script.\"}," +
            "\"condition\":{\"type\":\"string\"},\"hit_count\":{\"type\":\"string\"},\"log_message\":{\"type\":\"string\"}},\"required\":[\"line\"]}";
        private const string LineArgument = "{\"type\":\"object\",\"properties\":{\"line\":{\"type\":\"integer\",\"description\":\"1-based line number in the open script.\"}},\"required\":[\"line\"]}";

        public static IEnumerable<McpServer.Tool> For(ScriptEditorAccess access, ScriptEditorHook hook, EditGate gate, Action<string> shown)
        {
            yield return new McpServer.Tool(
                "get_problems",
                "The Script Editor's Problems: syntax errors and warnings of the open script, with line numbers.",
                NoArguments,
                _ => ScriptEditorAccess.OnUiThread(() =>
                {
                    var code = access.Code;
                    if (code == null)
                        return Task.FromResult("No script is open.");
                    var lines = code.Diagnostics.Select(d => string.Format("line {0}: {1}: {2}",
                        d.HasReference ? d.Reference.Position.LineNumber.ToString() : "?", d.Severity, d.Message)).ToList();
                    return Task.FromResult(lines.Count == 0 ? "No problems." : string.Join("\n", lines));
                }));

            yield return new McpServer.Tool(
                "get_breakpoints",
                "The breakpoints of the open script: line, inactive, and condition / hit count / log message with this run's hits.",
                NoArguments,
                _ => ScriptEditorAccess.OnUiThread(() =>
                {
                    var code = access.Code;
                    var controls = access.DebugControls;
                    if (code == null || controls == null)
                        return Task.FromResult("No script is open.");
                    var lines = BreakpointsOf(controls, code).Select(b =>
                    {
                        var line = b.Reference.Position.LineNumber;
                        var spec = BreakpointSpecs.Find(code.Uri, line);
                        return "line " + line + (b.IsActive ? "" : " (inactive)") + (spec == null ? "" : ": " + spec.Summary + ", " + spec.Hits + " hit(s) this run" +
                            (spec.LastError != null ? ", condition error: " + spec.LastError : ""));
                    }).ToList();
                    if (lines.Count > 0 && !MiniVivisectionSettings.ConditionalBreakpoints)
                        lines.Add("(conditional breakpoints are off in the Options: every breakpoint pauses)");
                    return Task.FromResult(lines.Count == 0 ? "No breakpoints." : string.Join("\n", lines));
                }));

            yield return new McpServer.Tool(
                "get_watches",
                "The V Watch tab: the expressions drawn in Rhino while paused, with value, type, how they're drawn (Interpret as) and what the last pause found.",
                NoArguments,
                _ => ScriptEditorAccess.OnUiThread(() =>
                {
                    var list = hook.WatchListFor(access.Editor);
                    var lines = list.Items.Select(w => string.Format("{0}{1}: {2} [{3}] -- {4}{5}", w.Expression, w.Visible ? "" : " (hidden)",
                        w.Value.Length > 0 ? w.Value : "?", w.Type, w.Status, w.Interpretation != "auto" ? ", as " + w.Interpretation : "")).ToList();
                    return Task.FromResult(lines.Count == 0 ? "The V Watch tab is empty." : string.Join("\n", lines));
                }));

            yield return new McpServer.Tool(
                "remove_watch",
                "Removes an expression from the V Watch tab.",
                "{\"type\":\"object\",\"properties\":{\"expression\":{\"type\":\"string\"}},\"required\":[\"expression\"]}",
                arguments => ScriptEditorAccess.OnUiThread(() =>
                {
                    var expression = arguments.GetProperty("expression").GetString() ?? "";
                    var list = hook.WatchListFor(access.Editor);
                    var found = list.Items.Where(w => w.Expression == expression).ToList();
                    list.Remove(found);
                    return Task.FromResult(found.Count > 0 ? "Removed " + expression + "." : expression + " isn't in the V Watch tab.");
                }));

            yield return new McpServer.Tool(
                "interpret_as",
                "Sets how a V Watch expression's raw data is drawn: auto, point, vector, points, polyline, polyline_closed, curve, lines, anchored_vectors, triangles, group or raw (not drawn). Adds the watch if it isn't there.",
                "{\"type\":\"object\",\"properties\":{\"expression\":{\"type\":\"string\"},\"interpretation\":{\"type\":\"string\"}},\"required\":[\"expression\",\"interpretation\"]}",
                async arguments =>
                {
                    var expression = arguments.GetProperty("expression").GetString() ?? "";
                    var refused = ScriptEditorTools.IsPlain(expression) ? null : await ScriptEditorTools.MayRun(gate, "a V Watch expression (evaluated at every pause)", expression);
                    return refused ?? await InterpretAs(arguments, expression);
                });

            Task<string> InterpretAs(JsonElement arguments, string expression) => ScriptEditorAccess.OnUiThread(() =>
                {
                    var interpretation = arguments.GetProperty("interpretation").GetString() ?? "auto";
                    hook.WatchListFor(access.Editor).Add(expression, interpretation);
                    return Task.FromResult(string.Format("{0} is drawn as {1} from the next drawing on (a choice that doesn't fit the value falls back to auto).", expression, interpretation));
                });

            yield return new McpServer.Tool(
                "register_visualizer",
                "Runs Python in the paused frame to register a visualizer (and inverse) for the user's own classes: @rhinoviv.visualize(MyType) on f(obj) returning Rhino geometry, " +
                "points or raw data; @rhinoviv.inverse(MyType) on f(obj, edited_geometry) updating obj. The watches are drawn again right after. Only while paused.",
                "{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\",\"description\":\"Python, e.g. import rhinoviv / @rhinoviv.visualize(Node) / def show(n): return Rhino.Geometry.Point3d(n.x, n.y, 0)\"}},\"required\":[\"code\"]}",
                async arguments =>
                {
                    var code = arguments.GetProperty("code").GetString() ?? "";
                    var refused = await ScriptEditorTools.MayRun(gate, "register_visualizer", code);
                    return refused ?? await RegisterVisualizer(code);
                });

            Task<string> RegisterVisualizer(string code)
            {
                {
                    var controls = access.DebugControls;
                    if (controls == null || controls.State != DebugControlsState.Paused)
                        return Task.FromResult("The debugger isn't paused.");
                    var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!hook.RunOnPausedThread(controls, () =>
                    {
                        try
                        {
                            var output = PythonBridge.Exec(code, inPausedFrame: true);
                            hook.Refresh(controls);
                            UserLog.Info("Claude registered a visualizer.");
                            done.SetResult(output.Length > 0 ? output : "Registered; the watches are drawn again.");
                        }
                        catch (Exception ex)
                        {
                            done.SetResult(ScriptEditorHook.FirstLine(ex));
                        }
                    }))
                        return Task.FromResult(WatchActions.NotReachable);
                    return ScriptEditorTools.Within(done.Task, "register_visualizer");
                }
            }

            yield return new McpServer.Tool(
                "get_plugin_log",
                "The miniVivisection plug-in's own detailed log (newest last), to find out why the plug-in did something. level: Error, Warning, Info, Debug or Trace (default Info); area: e.g. Pause, Watch, Edit, Python, Claude (default all).",
                "{\"type\":\"object\",\"properties\":{\"lines\":{\"type\":\"integer\"},\"level\":{\"type\":\"string\"},\"area\":{\"type\":\"string\"}}}",
                arguments =>
                {
                    var count = arguments.TryGetProperty("lines", out var n) && n.TryGetInt32(out var lines) && lines > 0 ? Math.Min(lines, 500) : 100;
                    var level = arguments.TryGetProperty("level", out var l) && Enum.TryParse(l.GetString(), true, out LogLevel parsed) ? parsed : LogLevel.Info;
                    LogArea? area = arguments.TryGetProperty("area", out var a) && Enum.TryParse(a.GetString(), true, out LogArea parsedArea) ? parsedArea : (LogArea?)null;
                    var found = Log.Recent(count, level, area);
                    return Task.FromResult(found.Count == 0 ? "Nothing logged at that level (the Options page sets what's logged)." : string.Join("\n", found));
                });

            // --- steering the debugger ----------------------------------------------------------

            yield return new McpServer.Tool(
                "set_breakpoint",
                "Sets a breakpoint at a line of the open script (shown in the editor), optionally with a condition (an expression in the script's language: " +
                "pauses only when true), a hit count (5: the 5th hit; >= 5; % 10: every 10th) or a log message (printed to the Terminal instead of pausing; " +
                "{expressions} filled in). Setting them on an existing breakpoint changes them; empty strings clear them. Needs the Options page's \"Claude may control the debugger\".",
                BreakpointArguments,
                async arguments =>
                {
                    // A condition or log message runs at every hit: one that calls something is code to approve.
                    string Text(string name) => arguments.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var runs = new[] { Text("condition") }.Concat(ScriptEditorTools.LogExpressions(Text("log_message")))
                        .Where(e => !string.IsNullOrWhiteSpace(e) && !ScriptEditorTools.IsPlain(e)).ToList();
                    var refused = runs.Count == 0 ? null : await ScriptEditorTools.MayRun(gate, "a breakpoint condition or log message (evaluated at every hit)", string.Join("\n", runs));
                    return refused ?? await SetBreakpoint(arguments);
                });

            Task<string> SetBreakpoint(JsonElement arguments) => Controlled(shown, "set a breakpoint", () => ScriptEditorAccess.OnUiThread(() =>
                {
                    var line = arguments.GetProperty("line").GetInt32();
                    var code = access.Code;
                    var controls = access.DebugControls;
                    if (code == null || controls == null)
                        return Task.FromResult("No script is open.");
                    string Optional(string name) => arguments.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var condition = Optional("condition");
                    var hitCount = Optional("hit_count");
                    var logMessage = Optional("log_message");
                    var python = BreakpointUi.IsPython(code.Uri, access.Language);
                    var problem = (condition != null ? BreakpointUi.Validate(SpecField.Condition, python, condition) : null)
                        ?? (hitCount != null ? BreakpointUi.Validate(SpecField.HitCount, python, hitCount) : null)
                        ?? (logMessage != null ? BreakpointUi.Validate(SpecField.LogMessage, python, logMessage) : null);
                    if (problem != null)
                        return Task.FromResult("Not set: " + problem);
                    var existed = BreakpointsOf(controls, code).Any(b => b.Reference.Position.LineNumber == line);
                    if (!existed)
                        controls.Breakpoints.Add(new CodeReferenceBreakpoint(code, line));
                    if (condition != null || hitCount != null || logMessage != null)
                    {
                        BreakpointSpecs.Set(code.Uri, line, s =>
                        {
                            s.Condition = condition ?? s.Condition;
                            s.HitCondition = hitCount ?? s.HitCondition;
                            s.LogMessage = logMessage ?? s.LogMessage;
                        });
                    }
                    var spec = BreakpointSpecs.Find(code.Uri, line);
                    var what = "breakpoint at line " + line + (spec != null ? " (" + spec.Summary + ")" : "");
                    shown((existed ? "changed " : "set ") + what);
                    return Task.FromResult((existed ? "Changed the " : "Set a ") + what + "." +
                        (spec != null && !MiniVivisectionSettings.ConditionalBreakpoints ? " Conditional breakpoints are off in the Options, so it pauses every time." : ""));
                }));

            yield return new McpServer.Tool(
                "clear_breakpoint",
                "Removes the breakpoint at a line of the open script; line 0 removes all of them. Needs \"Claude may control the debugger\".",
                LineArgument,
                arguments => Controlled(shown, "clear breakpoints", () => ScriptEditorAccess.OnUiThread(() =>
                {
                    var line = arguments.GetProperty("line").GetInt32();
                    var code = access.Code;
                    var controls = access.DebugControls;
                    if (code == null || controls == null)
                        return Task.FromResult("No script is open.");
                    var removed = BreakpointsOf(controls, code).Where(b => line == 0 || b.Reference.Position.LineNumber == line).ToList();
                    controls.Breakpoints.Remove(removed.Cast<DebugBreakpoint>());
                    if (removed.Count > 0)
                        shown(line == 0 ? "all breakpoints removed" : "breakpoint at line " + line + " removed");
                    return Task.FromResult(removed.Count == 0 ? "No breakpoint there." : "Removed " + removed.Count + " breakpoint(s).");
                })));

            foreach (var (name, command, what) in new[]
            {
                ("debug_continue", "DebugContinueCommand", "continue to the next breakpoint"),
                ("debug_step_over", "DebugStepOverCommand", "step over the current line"),
                ("debug_step_in", "DebugStepIntoCommand", "step into the call on the current line"),
                ("debug_step_out", "DebugStepOutCommand", "step out of the current function"),
            })
            {
                yield return new McpServer.Tool(
                    name,
                    "While paused: " + what + ", as the editor's button does. Waits for the next pause (up to the Options page's step time) " +
                    "and answers where it stopped and which variables changed -- or that the run ended. Needs \"Claude may control the debugger\".",
                    NoArguments,
                    _ => Controlled(shown, what, () => Step(access, hook, shown, command, what, requirePaused: true)));
            }

            yield return new McpServer.Tool(
                "debug_stop",
                "Stops the debug run, as the editor's Stop button does. Needs \"Claude may control the debugger\".",
                NoArguments,
                _ => Controlled(shown, "stop the run", () => Step(access, hook, shown, "DebugStopCommand", "stop the run", requirePaused: false)));

            yield return new McpServer.Tool(
                "start_debugging",
                "Starts the open script under the debugger (as the editor's Debug button does) and waits for the first pause or the end. " +
                "Its edits take effect in this run. Needs \"Claude may control the debugger\".",
                NoArguments,
                _ => Controlled(shown, "start debugging", () => Step(access, hook, shown, "CodeRunDebugCommand", "start debugging", requirePaused: false, mustNotRun: true)));
        }

        private static List<CodeReferenceBreakpoint> BreakpointsOf(IDebugControls controls, Code code) =>
            controls.Breakpoints.OfType<CodeReferenceBreakpoint>().Where(b => b.Reference.Uri == code.Uri).ToList();

        /// <summary>Only with the Options page's "Claude may control the debugger".</summary>
        private static Task<string> Controlled(Action<string> shown, string what, Func<Task<string>> action)
        {
            if (!MiniVivisectionSettings.ClaudeDebuggerControl)
                return Task.FromResult("Not allowed: the user turned off \"Claude may control the debugger\" (Options page). Ask the user to " + what + ".");
            return action();
        }

        /// <summary>Runs an editor debug command and waits for what follows: the next pause
        /// (where, which variables changed) or the end of the run.</summary>
        private static async Task<string> Step(ScriptEditorAccess access, ScriptEditorHook hook, Action<string> shown, string command, string what,
            bool requirePaused, bool mustNotRun = false)
        {
            var controls = access.DebugControls;
            if (controls == null)
                return "No script is open.";
            if (requirePaused && controls.State != DebugControlsState.Paused)
                return "The debugger isn't paused.";
            if (mustNotRun && (controls.State == DebugControlsState.Debugging || controls.State == DebugControlsState.Paused))
                return "A run is going on already (" + controls.State + ").";
            var before = requirePaused ? await Variables(hook, controls) : new Dictionary<string, string>();
            var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<IDebugControls> onPaused = paused =>
            {
                if (paused == controls)
                    done.TrySetResult(DescribePause(paused, before)); // on the paused thread: the variables can be read
            };
            Action<IDebugControls> onEnded = ended =>
            {
                if (ended == controls)
                    done.TrySetResult("The run ended. (get_run_errors has its output and any exception.)");
            };
            hook.PausedForTools += onPaused;
            hook.EndedForTools += onEnded;
            try
            {
                shown(what);
                var timeout = TimeSpan.FromSeconds(Math.Max(1, MiniVivisectionSettings.ClaudeStepTimeout));
                // Rhino's UI thread may be busy (a script running on it): never wait for it forever.
                var execute = ScriptEditorAccess.OnUiThread(() => Task.FromResult(EditorCommands.Execute(access.Editor, command)));
                if (await Task.WhenAny(execute, Task.Delay(timeout)) != execute)
                    return string.Format("Rhino's UI didn't take the command within {0:0} s -- a script may be running on it; the command may still come.", timeout.TotalSeconds);
                if (!await execute)
                    return "The editor's command (" + command + ") isn't available.";
                var finished = await Task.WhenAny(done.Task, Task.Delay(timeout));
                return finished == done.Task
                    ? done.Task.Result
                    : string.Format("Still running after {0:0} s (no pause, no end) -- it may be waiting for input, or long; call debug_stop to end it.", timeout.TotalSeconds);
            }
            finally
            {
                hook.PausedForTools -= onPaused;
                hook.EndedForTools -= onEnded;
            }
        }

        private static async Task<Dictionary<string, string>> Variables(ScriptEditorHook hook, IDebugControls controls)
        {
            var done = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!hook.RunOnPausedThread(controls, () => done.TrySetResult(ReadVariables(controls))))
                return new Dictionary<string, string>();
            var finished = await Task.WhenAny(done.Task, Task.Delay(MiniVivisectionSettings.PausedThreadTimeout));
            return finished == done.Task ? done.Task.Result : new Dictionary<string, string>();
        }

        /// <summary>The paused frame's variables, name to value text (on the paused thread).</summary>
        private static Dictionary<string, string> ReadVariables(IDebugControls controls)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var variable in controls.Results?.CurrentThread?.CurrentFrame?.Evaluate().Select(r => r.Result).OfType<ExecVariable>() ?? Enumerable.Empty<ExecVariable>())
                    result[variable.Id.Identifier] = Shorten(variable.GetValueRepr());
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Claude, "variables: {0}", Log.Describe(ex));
            }
            return result;
        }

        private static string DescribePause(IDebugControls controls, Dictionary<string, string> before)
        {
            var frame = controls.Results?.CurrentThread?.CurrentFrame;
            var text = new StringBuilder();
            text.AppendFormat("Paused at line {0} of {1}{2}.\n", frame?.Reference?.Position.LineNumber, frame?.Reference?.Uri?.Segments.LastOrDefault(),
                frame != null && frame.Event == ExecEvent.Exception ? string.Format(" -- at an exception: {0}: {1}", frame.Exception.Id, frame.Exception.Message) : "");
            var after = ReadVariables(controls);
            var changed = after.Where(v => !before.TryGetValue(v.Key, out var old) || old != v.Value).ToList();
            if (before.Count == 0)
                text.Append("Variables: ").Append(string.Join(", ", after.Select(v => v.Key + " = " + v.Value)));
            else if (changed.Count == 0)
                text.Append("No variable changed.");
            else
                text.Append("Changed: ").Append(string.Join(", ", changed.Select(v => v.Key + " = " + v.Value)));
            var gone = before.Keys.Where(k => !after.ContainsKey(k)).ToList();
            if (before.Count > 0 && gone.Count > 0)
                text.Append("\nNot in this frame any more: ").Append(string.Join(", ", gone));
            return text.ToString();
        }

        private static string Shorten(string text)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 160 ? text : text.Substring(0, 160) + "...";
        }
    }

    /// <summary>The Script Editor's own commands (its toolbar buttons and keys), run by
    /// name: <c>RCE.ActiveCommands.Find&lt;T&gt;()</c>, an Eto Command. UI thread.</summary>
    internal static class EditorCommands
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static bool Execute(object editor, string name)
        {
            try
            {
                var type = editor.GetType().Assembly.GetType("RhinoCodeEditor.Editor.Commands." + name);
                var commands = editor.GetType().GetProperty("ActiveCommands", Any)?.GetValue(editor);
                var find = commands?.GetType().GetMethods(Any).FirstOrDefault(m => m.Name == "Find" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (type == null || find == null)
                    return false;
                if (!(find.MakeGenericMethod(type).Invoke(commands, null) is Eto.Forms.Command command))
                    return false;
                Log.Debug(LogArea.Claude, "editor command {0}", name);
                command.Execute();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Claude, "editor command {0}: {1}", name, Log.Describe(ex));
                return false;
            }
        }
    }
}
