using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// The MCP tools the Claude tab gives Claude Code for its Script Editor: the open
    /// script, the selection, the Terminal, the debugger, Python in the paused frame,
    /// editing the script, drawing a value in Rhino.
    /// </summary>
    /// <summary>What Claude's script edits go through: applied now, or -- while a run holds
    /// the script (running or paused in the debugger, stopped at an error until Stop), or when
    /// the user reviews changes (Options page: Claude's edits "Ask") -- queued in the Claude
    /// tab and applied later (/apply, or by itself when the run ends and edits are automatic).</summary>
    internal sealed class EditGate
    {
        /// <summary>The script before an edit (the Claude tab's /undo).</summary>
        public Action<string> Changed = _ => { };

        /// <summary>After an edit went in (the Claude tab's /undo knows the text Claude left).</summary>
        public Action Applied = () => { };

        /// <summary>Queues (what, old text, new text); returns the answer for Claude.</summary>
        public Func<string, string, string, string> Queue = (what, oldText, newText) => "Not applied.";

        public bool MustQueue(ScriptEditorAccess access) => access.IsBusy || MiniVivisectionSettings.ClaudeEditApproval == "Ask";

        /// <summary>"Claude may run Python" = Ask: the code shown in the Claude tab; true once
        /// the user typed /run, false for /deny or no answer.</summary>
        public Func<string, string, Task<bool>> AskToRun = (what, code) => Task.FromResult(false);

        /// <summary>The script the message being answered was about (Send): Claude's edits go
        /// there only -- not into another tab the user switched to meanwhile.</summary>
        public Uri Script;

        /// <summary>Why an edit can't go to the script open now; null: it can.</summary>
        public string OtherScript(ScriptEditorAccess access) =>
            Script != null && access.Code?.Uri != Script
                ? "Not applied: the user switched to another script (" + access.ScriptTitle + ") since the message -- this edit was for " + Script.Segments.LastOrDefault() + "."
                : null;
    }

    internal static class ScriptEditorTools
    {
        public const string ServerName = "script-editor";

        public const string Instructions =
            "Rhino 8's Script Editor, where the user is working: the open script, its selection, the Terminal output, " +
            "the debugger (paused frame, Python in it), editing the script, drawing values in the Rhino viewports. " +
            "While a run holds the script (running or paused in the debugger, stopped at an error until Stop) edits are queued: " +
            "inspect it with get_debug_state and evaluate instead.";

        private const string NoArguments = "{\"type\":\"object\",\"properties\":{}}";

        public static IEnumerable<McpServer.Tool> For(ScriptEditorAccess access, ScriptEditorHook hook, EditGate gate)
        {
            yield return new McpServer.Tool(
                "get_script",
                "The script open in the Script Editor: its name, path, language, cursor, selection, debugger state, and its text with line numbers.",
                NoArguments,
                _ => ScriptEditorAccess.OnUiThread(async () =>
                {
                    var text = access.ScriptText;
                    if (text == null)
                        return "No script is open in the Script Editor.";
                    var selection = await access.SelectionAsync();
                    var cursor = await access.CursorAsync();
                    var lines = SplitLines(text);
                    var result = new StringBuilder()
                        .AppendLine("Script: " + access.ScriptTitle)
                        .AppendLine("Path: " + (access.ScriptPath ?? "(not saved to a file)"))
                        .AppendLine("Language: " + access.Language)
                        .AppendLine(string.Format("Lines: {0}; cursor at line {1}, column {2}", lines.Length, cursor.Line, cursor.Column))
                        .AppendLine(selection.Length > 0 ? string.Format("Selection: {0} characters (get_selection)", selection.Length) : "Selection: none")
                        .AppendLine("Debugger: " + (access.PausedAt != null ? "paused at " + access.PausedAt : access.RunState ?? "not running"))
                        .AppendLine();
                    for (var i = 0; i < lines.Length; i++)
                        result.Append((i + 1).ToString().PadLeft(5)).Append('\t').AppendLine(lines[i]);
                    return result.ToString();
                }));

            yield return new McpServer.Tool(
                "get_selection",
                "The text selected in the Script Editor (empty when nothing is selected) and the cursor position.",
                NoArguments,
                _ => ScriptEditorAccess.OnUiThread(async () =>
                {
                    var selection = await access.SelectionAsync();
                    var cursor = await access.CursorAsync();
                    return string.Format("Cursor: line {0}, column {1}\nSelection ({2} characters):\n{3}", cursor.Line, cursor.Column, selection.Length, selection);
                }));

            yield return new McpServer.Tool(
                "get_terminal_output",
                "The Script Editor's Terminal as it is now: what the script printed and the errors of its runs. The last lines, by default 200. " +
                "A debug run's exception isn't printed here, and the next run clears the Terminal -- get_run_errors keeps both.",
                "{\"type\":\"object\",\"properties\":{\"tail_lines\":{\"type\":\"integer\",\"description\":\"How many of the last lines (default 200).\"}}}",
                arguments => ScriptEditorAccess.OnUiThread(() =>
                {
                    var tail = arguments.TryGetProperty("tail_lines", out var t) && t.TryGetInt32(out var n) && n > 0 ? n : 200;
                    var lines = SplitLines(access.TerminalText);
                    var shown = lines.Skip(Math.Max(0, lines.Length - tail)).ToArray();
                    var text = string.Join("\n", shown).Trim();
                    return Task.FromResult(text.Length == 0 ? "The Terminal is empty." : text);
                }));

            yield return new McpServer.Tool(
                "get_run_errors",
                "What the script's recent runs left behind, kept after they ended, newest first: the exceptions the debugger stopped at " +
                "(type, message, line, traceback) and the Terminal output at the end of each run. The editor shows a debug run's " +
                "exception only while the debugger is stopped at it -- use this to find out why a run failed afterwards.",
                "{\"type\":\"object\",\"properties\":{\"runs\":{\"type\":\"integer\",\"description\":\"How many recent runs (default 1, at most 5).\"}}}",
                arguments => ScriptEditorAccess.OnUiThread(() =>
                {
                    var count = arguments.TryGetProperty("runs", out var r) && r.TryGetInt32(out var n) && n > 0 ? Math.Min(n, 5) : 1;
                    return Task.FromResult(hook.RunLogFor(access.Editor).Describe(count));
                }));

            yield return new McpServer.Tool(
                "get_debug_state",
                "Whether the Script Editor's debugger is paused, where, and the paused frame's variables (name, type, value).",
                NoArguments,
                _ => OnPausedThreadOrUi(hook, access, () =>
                {
                    var controls = access.DebugControls;
                    if (controls == null || controls.State != DebugControlsState.Paused)
                        return "The debugger isn't paused: no debug run, or the script is running.";
                    var frame = controls.Results?.CurrentThread?.CurrentFrame;
                    var result = new StringBuilder().AppendLine("Paused at " + access.PausedAt).AppendLine("Variables:");
                    foreach (var variable in frame?.Evaluate().Select(r => r.Result).OfType<ExecVariable>() ?? Enumerable.Empty<ExecVariable>())
                        result.AppendFormat("  {0}: {1} = {2}\n", variable.Id.Identifier, variable.Id.TypeIdentifier, Shorten(variable.GetValueRepr(), 300));
                    var editor = access.Editor;
                    var watches = editor == null ? new List<string>() : hook.WatchListFor(editor).Snapshot.Select(w => w.Visible ? w.Expression : w.Expression + " (hidden)").ToList();
                    result.AppendLine("V Watch tab (drawn in Rhino): " + (watches.Count == 0 ? "(empty)" : string.Join(", ", watches)));
                    return result.ToString();
                }));

            yield return new McpServer.Tool(
                "run_python",
                "Runs Python. While the debugger is paused it runs in the paused frame: it sees and can change the script's variables " +
                "(the script continues with the changes). Otherwise it runs in a scratch namespace that is kept between calls. " +
                "Statements like a notebook cell: the value of a final expression is printed. Returns the output. " +
                "Commands available inside: show(value, ..., name=None) draws values in the Rhino viewports until clear(); " +
                "hide(name); clear(); bake(value, ...) adds copies to the document; watch('expr') adds to the V Watch tab; " +
                "edit('expr') lets the user edit a value as Rhino objects, written back.",
                "{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\",\"description\":\"Python statements.\"}},\"required\":[\"code\"]}",
                async arguments =>
                {
                    var code = arguments.GetProperty("code").GetString() ?? "";
                    var refused = await MayRun(gate, "run_python", code);
                    if (refused != null)
                        return refused;
                    return await RunPython(code);
                });

            Task<string> RunPython(string code)
            {
                {
                    var controls = access.DebugControls;
                    if (controls != null && controls.State == DebugControlsState.Paused && ScriptEditorHook.IsPythonPause(controls))
                    {
                        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var reached = hook.RunOnPausedThread(controls, () =>
                        {
                            try
                            {
                                TerminalHost.Instance.Use(access, hook);
                                var output = PythonBridge.Exec(code, inPausedFrame: true);
                                hook.Refresh(controls);
                                done.SetResult(output);
                            }
                            catch (Exception ex)
                            {
                                done.SetException(ex);
                            }
                        });
                        if (!reached)
                            return Task.FromResult("The paused script can't be reached: " + WatchActions.NotReachable + ".");
                        return Output(Within(done.Task, "run_python"));
                    }
                    return ScriptEditorAccess.OnUiThread(() =>
                    {
                        if (!PythonBridge.IsRunning)
                            return Task.FromResult("Python isn't started in this Rhino yet (run any Python script once).");
                        TerminalHost.Instance.Use(access, hook);
                        return Output(Task.FromResult(PythonBridge.Exec(code, inPausedFrame: false)));
                    });
                }
            }

            yield return new McpServer.Tool(
                "evaluate",
                "Read-only look at the paused script: evaluates a Python expression in the paused frame and describes its value -- " +
                "type, value, for Rhino geometry its key data (point coordinates, curve ends and length, bounding boxes), lists item " +
                "by item. Only while the debugger is paused; get_debug_state lists the variables. Pass an expression without side " +
                "effects (no assignments, no methods that change objects, such as Translate).",
                "{\"type\":\"object\",\"properties\":{\"expression\":{\"type\":\"string\",\"description\":\"A Python expression, e.g. crv.PointAtEnd or [c.GetLength() for c in curves].\"}},\"required\":[\"expression\"]}",
                async arguments =>
                {
                    var expression = (arguments.GetProperty("expression").GetString() ?? "").Trim();
                    var refused = IsPlain(expression) ? null : await MayRun(gate, "evaluate", expression);
                    return refused ?? await Evaluate(expression);
                });

            Task<string> Evaluate(string expression) => OnPausedThreadOrUi(hook, access, () =>
                {
                    var controls = access.DebugControls;
                    if (controls == null || controls.State != DebugControlsState.Paused)
                        return "The debugger isn't paused -- there are no variables to look at.";
                    if (!ScriptEditorHook.IsPythonPause(controls))
                        return "The paused script isn't Python 3 (IronPython 2 or C#): evaluate reaches Python 3 scripts only. get_debug_state lists its variables.";
                    if (!hook.IsPausedThread(controls))
                        return "The paused script runs on its own thread and can't be reached (FreePause is off).";
                    try
                    {
                        return expression + " = " + ValueText.Describe(PythonBridge.Eval(expression));
                    }
                    catch (Exception ex)
                    {
                        return expression + ": " + ScriptEditorHook.FirstLine(ex);
                    }
                });

            yield return new McpServer.Tool(
                "edit_script",
                "Edits the open script: replaces old_text, which must occur exactly once, with new_text (like the Edit tool). " +
                "The editor shows the change at once; it takes effect at the next run. While a run holds the script (running, paused, " +
                "stopped at an error until Stop) -- or when the user " +
                "reviews changes (Options page) -- the change is queued instead and applied later; the answer says which.",
                "{\"type\":\"object\",\"properties\":{\"old_text\":{\"type\":\"string\"},\"new_text\":{\"type\":\"string\"}},\"required\":[\"old_text\",\"new_text\"]}",
                arguments => ScriptEditorAccess.OnUiThread(() =>
                {
                    if (gate.OtherScript(access) is string elsewhere)
                        return Task.FromResult(elsewhere);
                    var oldText = arguments.GetProperty("old_text").GetString() ?? "";
                    var newText = arguments.GetProperty("new_text").GetString() ?? "";
                    var text = access.ScriptText;
                    if (text == null)
                        return Task.FromResult("No script is open.");
                    var eol = text.Contains("\r\n") ? "\r\n" : "\n";
                    oldText = oldText.Replace("\r\n", "\n").Replace("\n", eol);
                    newText = newText.Replace("\r\n", "\n").Replace("\n", eol);
                    if (oldText.Length == 0)
                        return Task.FromResult("old_text is empty.");
                    var first = text.IndexOf(oldText, StringComparison.Ordinal);
                    if (first < 0)
                        return Task.FromResult("old_text isn't in the script -- read it again with get_script.");
                    if (text.IndexOf(oldText, first + 1, StringComparison.Ordinal) >= 0)
                        return Task.FromResult("old_text occurs more than once -- include more context.");
                    var line = text.Substring(0, first).Count(c => c == '\n') + 1;
                    if (gate.MustQueue(access))
                        return Task.FromResult(gate.Queue("edit at line " + line, oldText, newText));
                    gate.Changed(text);
                    if (!access.SetScriptText(text.Substring(0, first) + newText + text.Substring(first + oldText.Length)))
                        return Task.FromResult("The script is read-only.");
                    gate.Applied();
                    return Task.FromResult(string.Format("Edited the script at line {0}.", line));
                }));

            yield return new McpServer.Tool(
                "replace_selection",
                "Replaces the text selected in the Script Editor with text (the user can undo it in the editor). Queued like edit_script while a run holds the script or under review.",
                "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}",
                arguments => ScriptEditorAccess.OnUiThread(async () =>
                {
                    if (gate.OtherScript(access) is string elsewhere)
                        return elsewhere;
                    var selection = await access.SelectionAsync();
                    if (selection.Length == 0)
                        return "Nothing is selected in the Script Editor.";
                    if (gate.MustQueue(access))
                        return gate.Queue("replace the selection", selection, arguments.GetProperty("text").GetString() ?? "");
                    gate.Changed(access.ScriptText);
                    if (!await access.ReplaceSelectionAsync(arguments.GetProperty("text").GetString() ?? ""))
                        return "The selection can't be replaced in this editor.";
                    gate.Applied();
                    return "Replaced the selection.";
                }));

            yield return new McpServer.Tool(
                "add_watch",
                "Adds a Python expression to the Script Editor's V Watch tab while the debugger is paused; geometry it holds " +
                "(curves, points, breps, lists of them, ...) is drawn in the Rhino viewports at every pause.",
                "{\"type\":\"object\",\"properties\":{\"expression\":{\"type\":\"string\"}},\"required\":[\"expression\"]}",
                async arguments =>
                {
                    var expression = arguments.GetProperty("expression").GetString() ?? "";
                    var refused = IsPlain(expression) ? null : await MayRun(gate, "a V Watch expression (evaluated at every pause)", expression);
                    return refused ?? await AddWatch(expression);
                });

            Task<string> AddWatch(string expression) => ScriptEditorAccess.OnUiThread(() =>
                {
                    var controls = access.DebugControls;
                    if (controls == null || controls.State != DebugControlsState.Paused)
                        return Task.FromResult("The debugger isn't paused.");
                    return Task.FromResult(hook.AddRhinoWatch(controls, expression)
                        ? "Added " + expression + " to the V Watch tab; it is drawn at every pause."
                        : "This Script Editor isn't known yet.");
                });
        }

        private static async Task<string> Output(Task<string> output)
        {
            var text = (await output).TrimEnd();
            return text.Length == 0 ? "(no output)" : text;
        }

        /// <summary>On the paused thread when there is one (the paused frame's variables
        /// may need its Python), else on the UI thread.</summary>
        private static Task<string> OnPausedThreadOrUi(ScriptEditorHook hook, ScriptEditorAccess access, Func<string> work)
        {
            var controls = access.DebugControls;
            if (controls != null && controls.State == DebugControlsState.Paused)
            {
                var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (hook.RunOnPausedThread(controls, () =>
                {
                    try
                    {
                        done.SetResult(work());
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }))
                    return Within(done.Task, "the paused script");
            }
            return ScriptEditorAccess.OnUiThread(() => Task.FromResult(work()));
        }

        /// <summary>The answer when the Options page doesn't let Claude run Python.</summary>
        internal const string NoPython =
            "Not allowed: running Python is switched off (Options > miniVivisection > Claude > \"Claude may run Python\"). Read values with plain expressions in evaluate (names, attributes, indexes) or get_debug_state, and propose code edits instead.";

        /// <summary>Whether Claude may run <paramref name="code"/> now ("Claude may run Python":
        /// On, Off, or Ask the user in the Claude tab): null when it may, else the answer.</summary>
        internal static async Task<string> MayRun(EditGate gate, string what, string code)
        {
            switch (MiniVivisectionSettings.ClaudeRunCode)
            {
                case "On":
                    return null;
                case "Off":
                    return NoPython;
                default:
                    return await gate.AskToRun(what, code) ? null
                        : "Not run: the user declined it, or didn't answer within 2 minutes (\"Claude may run Python\" is Ask). Don't try it again unasked; read values with plain expressions, or explain what the code would do.";
            }
        }

        internal static bool IsPlain(string expression) => CodeApproval.IsPlain(expression);

        internal static IEnumerable<string> LogExpressions(string message) => CodeApproval.LogExpressions(message);

        /// <summary><paramref name="task"/>'s answer, or a note after Claude's step timeout
        /// (the Options page) -- a tool call never waits forever.</summary>
        internal static async Task<string> Within(Task<string> task, string what)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(5, MiniVivisectionSettings.ClaudeStepTimeout));
            if (await Task.WhenAny(task, Task.Delay(timeout)) == task)
                return await task;
            return string.Format("No answer from {0} within {1} s (it may still finish; the run may have continued).", what, (int)timeout.TotalSeconds);
        }

        private static string[] SplitLines(string text) => (text ?? "").Replace("\r\n", "\n").Split('\n');

        private static string Shorten(string text, int max)
        {
            text = (text ?? "").Replace("\r", " ").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
