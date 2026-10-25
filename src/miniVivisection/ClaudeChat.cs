using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace MiniVivisection
{
    /// <summary>
    /// The Script Editor's "Claude" tab: a chat with Claude Code (ClaudeRunner) that
    /// sees the editor through the script-editor MCP tools (ScriptEditorTools: script,
    /// selection, Terminal, debugger, Python in the paused frame, editing) and Rhino
    /// through RhinoAI's MCP server ("rhino"), when it is installed.
    ///
    /// Console style (TerminalView): type after the prompt, Enter sends, Shift+Enter
    /// starts a new line, Ctrl+C stops an answer. Commands: /replace -- the text is an
    /// instruction for the selected code, Claude's answer replaces it (Ctrl+Z in the
    /// editor undoes it); /fix (the last error); /explain (the selection or the
    /// script); /debug (while paused); /new (a new chat); /undo (the script as it was
    /// before Claude's last changes); /allow on|off (RhinoAI's destructive tools);
    /// /clear; /help.
    /// </summary>
    internal sealed class ClaudeChat : Panel
    {
        private enum Mode
        {
            Chat,
            ReplaceSelection,
            FixError,
            Explain,
            DebugHelp,
            Why,
        }

        private const string EditorPrompt =
            "You are helping inside Rhino 8's Script Editor, in its Claude tab. The user works on the script open in the editor.\n" +
            "script-editor tools: get_script (the open script with line numbers, cursor, selection, debugger state), get_selection, " +
            "get_terminal_output (what the script printed, its errors), get_run_errors (the exceptions and Terminal output of recent runs, " +
            "kept after they ended), get_debug_state (paused or not, where, the variables), " +
            "run_python (Python in the paused frame -- it can read and change the script's variables -- or in a scratch namespace), " +
            "edit_script (exact text replacement in the open script), replace_selection, add_watch (draw a value in the Rhino viewports while paused), " +
            "evaluate (a read-only look at an expression's value in the paused frame).\n" +
            "Debugging: get_problems (syntax errors, warnings), get_breakpoints, set_breakpoint/clear_breakpoint, debug_continue, " +
            "debug_step_over/in/out, debug_stop, start_debugging (each step answers where it stopped and which variables changed), " +
            "get_watches, interpret_as, remove_watch, register_visualizer (@rhinoviv.visualize/inverse for the user's classes), " +
            "get_plugin_log (the plug-in's own log, if something about it looks wrong).\n" +
            "Read the script before changing it, and change it only with edit_script or replace_selection, never with file tools. " +
            "Edits take effect at the next run.\n" +
            "While a run holds the script -- running or paused in the debugger, or stopped at an error until the user presses Stop -- " +
            "edits are queued and applied when the run has stopped -- say so; the run keeps the code it started with. Look at the script's variables with get_debug_state and evaluate. Change the running script's " +
            "variables with run_python only when the user asks for it. When an answer says an edit was queued or proposed, don't repeat it. Be concise.";

        private const string Help =
            "Type a message and press Enter (Shift+Enter: a new line). Ctrl+C stops an answer. Tab completes a / command.\n" +
            "  /replace <instruction>  rewrite the selected code; the answer replaces it (Ctrl+Z in the editor undoes it)\n" +
            "  /fix [note]             fix the script after the last error (what the run left behind)\n" +
            "  /explain [question]     explain the selection, or the script\n" +
            "  /debug [question]       look at the paused frame and its variables (while paused)\n" +
            "  /why [question]         why the script stopped here -- the exception, or how it got to this line\n" +
            "  /pending                Claude's changes waiting (queued during a run, or for review)\n" +
            "  /apply, /reject         apply or drop the waiting changes\n" +
            "  /undo [force]           restore the script as it was before Claude's last changes (force: also after you changed it)\n" +
            "  /history                the messages of this chat\n" +
            "  /allow on|off           let Claude use RhinoAI's commands and scripts (run_command, run_python, ...), without asking\n" +
            "  /run, /deny             run or refuse the code Claude asks to run (\"Claude may run Python\": Ask)\n" +
            "  /new                    start a new chat\n" +
            "  /clear                  clear this view\n" +
            "  /help                   this list\n";

        private readonly RhinoToolPolicy _rhinoTools = RhinoToolPolicy.Load();
        private bool _allowDestructive = MiniVivisectionSettings.ClaudeAllowRhinoTools;

        private readonly object _editor;
        private readonly ScriptEditorHook _hook;
        private readonly ScriptEditorAccess _access;
        private readonly ClaudeRunner _runner = new ClaudeRunner();
        private readonly EditGate _gate = new EditGate();
        private readonly TerminalView _view;
        private string _mcpConfig;
        private string _mcpUrl;
        private bool _hasRhinoServer;
        private string _undoText;
        private string _afterText; // the script as Claude's last change left it: /undo only from there
        private string _pendingReplace;
        private Mode _mode;
        private DateTime _started;
        private readonly List<(string What, string Old, string New, bool WhileRunning, Uri Script)> _pending = new List<(string, string, string, bool, Uri)>();
        private UITimer _idleWait; // changes queued during a run: until the editor holds the script no more
        private TaskCompletionSource<bool> _approval; // code Claude asked to run, waiting for /run or /deny
        private readonly List<string> _history = new List<string>();
        private string _sessionKey;

        // The conversation of each script: Claude Code's session, by script (kept in the plug-in's settings).
        private static readonly Dictionary<string, string> s_sessions = LoadSessions();

        public ClaudeChat(object editor, ScriptEditorHook hook)
        {
            _editor = editor;
            _hook = hook;
            _access = new ScriptEditorAccess(editor);
            _gate.Changed = before => Ui(() => _undoText = _undoText ?? before);
            _gate.Applied = () => Ui(() => _afterText = _access.ScriptText);
            _gate.Queue = Queue; // the tools call it on the UI thread
            _gate.AskToRun = AskToRun;
            _view = new TerminalView(editor, wrap: true)
            {
                MultilineInput = true,
                ColorCodeBlocks = () => MiniVivisectionSettings.ColorsClaude,
                Complete = input => SlashCommands.Complete(input, Help),
            };
            _view.Submitted += Submit;
            _view.Interrupted += () =>
            {
                if (_runner.Busy)
                    _runner.Stop();
                else if (_view.IsPrompting)
                    _view.Input = "";
            };
            _runner.Connected += servers => Ui(() => Write("[MCP servers -- " + servers + "]\n"));
            _runner.Text += text => Ui(() => Write("\n" + text.Trim() + "\n"));
            _runner.ToolUse += (name, input) =>
            {
                if (name != "ToolSearch") // Claude Code loading the MCP tools
                    Ui(() => Write(string.Format("  [{0}] {1}\n", ShortToolName(name), input)));
            };
            _runner.Finished += (text, cost, isError) => Ui(() => OnFinished(text, cost, isError));
            Action<Rhino.Runtime.Code.Execution.Debugging.IDebugControls> ended = controls => Ui(() =>
            {
                if (controls == _access.DebugControls)
                    AfterRun();
            });
            hook.EndedForTools += ended;
            // The hook lives for the session: this tab, its tools and its editor go with the window.
            EditorWindows.WhenClosed(editor, () =>
            {
                hook.EndedForTools -= ended;
                if (_mcpUrl != null)
                    McpServer.Instance.Unregister(_mcpUrl);
                _approval?.TrySetResult(false);
                _idleWait?.Stop();
                if (_runner.Busy)
                    _runner.Stop();
            });
            var chat = Python2Notice.Above(_view, new Python2Notice(editor,
                "The script being debugged is IronPython 2 (#! python2). Claude's Python tools run Python 3 and can't read or change its variables; " +
                "Claude still sees the code, the breakpoints and the debugger's variable list (get_debug_state)."));
            Content = new TableLayout { Rows = { new TableRow(MakeAnswerBar()), new TableRow(chat) { ScaleHeight = true } } };
            Write("Claude Code in the Script Editor (experimental). It can read the script, the selection, the Terminal and the debugger, " +
                  "run Python in the paused frame, edit the script and -- with RhinoAI installed -- work in Rhino.\n" + Help + "\n");
            Prompt();
        }

        public void FocusInput() => _view.FocusInput();

        private void Prompt()
        {
            if (!_view.IsPrompting) // once: a question during the answer may have shown it already
                _view.Prompt("> ");
        }

        // Typed while Claude still answers: its question's answer, or what only shows something.
        private static readonly HashSet<string> s_whileAnswering = new HashSet<string> { "/run", "/deny", "/help", "/clear", "/pending", "/history" };

        /// <summary>A message, or one of the commands (Help).</summary>
        private void Submit(string input)
        {
            var text = input.Trim();
            var space = text.IndexOfAny(new[] { ' ', '\n' });
            var command = text.StartsWith("/") ? (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant() : null;
            var rest = command == null ? text : (space < 0 ? "" : text.Substring(space + 1).Trim());
            if (_runner.Busy && text.Length > 0 && (command == null || !s_whileAnswering.Contains(command)))
            {
                Write(_approval != null
                    ? "Claude waits for /run or /deny; the rest when it's done (Ctrl+C stops it).\n"
                    : "Claude is still answering -- the rest when it's done (Ctrl+C stops it).\n");
                Prompt();
                return;
            }
            switch (command)
            {
                case null when text.Length == 0:
                    break;
                case null:
                    Send(Mode.Chat, rest);
                    return;
                case "/replace":
                    Send(Mode.ReplaceSelection, rest);
                    return;
                case "/fix":
                    Send(Mode.FixError, rest);
                    return;
                case "/explain":
                    Send(Mode.Explain, rest);
                    return;
                case "/debug":
                    Send(Mode.DebugHelp, rest);
                    return;
                case "/why":
                    Send(Mode.Why, rest);
                    return;
                case "/pending":
                    ShowPending();
                    break;
                case "/apply":
                    ApplyPending();
                    break;
                case "/reject":
                    Write(_pending.Count == 0 ? "Nothing waiting.\n" : string.Format("Dropped {0} change(s).\n", _pending.Count));
                    _pending.Clear();
                    break;
                case "/history":
                    Write(_history.Count == 0 ? "No messages yet.\n" : string.Join("\n", _history) + "\n");
                    break;
                case "/undo":
                    UndoChanges(rest == "force");
                    break;
                case "/run":
                case "/deny":
                    Answer(command == "/run");
                    break;
                case "/allow":
                    if (rest == "on" || rest == "off")
                        _allowDestructive = rest == "on";
                    Write("RhinoAI's commands and scripts: " + (_allowDestructive ? "allowed, without asking" : "not allowed") + " (/allow on|off).\n");
                    break;
                case "/new":
                    _runner.NewSession();
                    if (_sessionKey != null)
                    {
                        s_sessions.Remove(_sessionKey);
                        SaveSessions();
                    }
                    _history.Clear();
                    _view.Clear();
                    Write("New chat.\n");
                    break;
                case "/clear":
                    _view.Clear();
                    break;
                case "/help":
                    Write(Help);
                    break;
                default:
                    Write("Unknown command " + command + " -- /help lists them.\n");
                    break;
            }
            Prompt();
        }

        private async void Send(Mode mode, string instruction)
        {
            if (_runner.Busy)
                return;
            try
            {
                _gate.Script = _access.Code?.Uri; // Claude's edits for this message go to this script only
                var selection = await _access.SelectionAsync();
                var cursor = await _access.CursorAsync();
                var text = _access.ScriptText;
                var fence = _access.FenceLanguage;
                var context = string.Format(
                    "[Script Editor: {0} ({1}), {2} lines, cursor at line {3}; selection: {4}; debugger: {5}]",
                    _access.ScriptTitle, _access.Language, text == null ? 0 : text.Split('\n').Length, cursor.Line,
                    selection.Length > 0 ? selection.Length + " characters" : "none",
                    _access.PausedAt != null ? "paused at " + _access.PausedAt : _access.RunState ?? "not running");

                string prompt;
                switch (mode)
                {
                    case Mode.ReplaceSelection:
                        if (selection.Length == 0 || instruction.Length == 0)
                        {
                            Write("Select code in the editor first, then: /replace <what to change>\n");
                            Prompt();
                            return;
                        }
                        _pendingReplace = selection;
                        prompt = context + "\n\nRewrite the selected code below as instructed. Reply with only the complete replacement " +
                                 "in one fenced code block -- no explanation, no other code blocks. Read more of the script with " +
                                 "get_script if you need context, but don't edit the script yourself.\n\nInstruction: " + instruction +
                                 "\n\nSelected code:\n```" + fence + "\n" + selection + "\n```";
                        break;
                    case Mode.FixError:
                        prompt = context + "\n\nThe last run of the script failed or misbehaved. Read what it left behind " +
                                 "(get_run_errors: the exception and the Terminal output, kept after the run ended) and the script (get_script), " +
                                 "find the cause and fix the script with edit_script. " +
                                 "Then say briefly what was wrong." + (instruction.Length > 0 ? "\n\nNote from the user: " + instruction : "");
                        break;
                    case Mode.Explain:
                        prompt = context + "\n\n" + (selection.Length > 0
                            ? "Explain this selected code briefly:\n```" + fence + "\n" + selection + "\n```"
                            : "Explain the open script briefly (get_script).") + (instruction.Length > 0 ? "\n\n" + instruction : "");
                        break;
                    case Mode.DebugHelp:
                        if (_access.PausedAt == null)
                        {
                            Write("/debug needs the debugger to be paused.\n");
                            Prompt();
                            return;
                        }
                        prompt = context + "\n\nThe debugger is paused. Look at the state with get_debug_state, get_script and run_python " +
                                 "and help me understand what is happening here." + (instruction.Length > 0 ? "\n\n" + instruction : "");
                        break;
                    case Mode.Why:
                        if (_access.PausedAt == null)
                        {
                            Write("/why needs the debugger to be paused (after a run, /fix reads what it left behind).\n");
                            Prompt();
                            return;
                        }
                        prompt = context + "\n\nThe debugger stopped here. Find out why: if it stopped at an exception, what caused it " +
                                 "(get_debug_state, evaluate, get_run_errors); else how the script got to this line with these values. " +
                                 "Say what to change; edits are queued until the run stops (Stop)." + (instruction.Length > 0 ? "\n\n" + instruction : "");
                        break;
                    default:
                        prompt = context + "\n\n" + instruction + (selection.Length > 0 && selection.Length < 4000
                            ? "\n\n(Selected in the editor:)\n```" + fence + "\n" + selection + "\n```"
                            : "");
                        break;
                }

                _mode = mode;
                _undoText = null;
                _started = DateTime.Now;
                UseSessionOfScript();
                _history.Add(string.Format("{0:HH:mm} {1}: {2}", DateTime.Now, _access.ScriptTitle, instruction.Length > 0 ? instruction : mode.ToString()));
                var config = McpConfig();
                if (_hasRhinoServer)
                {
                    // RhinoAI's server has no token: started for Claude only when the user said so.
                    var note = MiniVivisectionSettings.ClaudeStartRhinoAi ? RhinoAiServer.EnsureRunning(RhinoDoc.ActiveDoc) : RhinoAiServer.NotRunningNote(RhinoDoc.ActiveDoc);
                    if (note != null && note != _lastRhinoAiNote)
                        Write("RhinoAI: " + note + "\n");
                    _lastRhinoAiNote = note;
                }
                var allowDestructive = _allowDestructive;
                var allowed = new[] { "mcp__" + ScriptEditorTools.ServerName, "Read", "Glob", "Grep" }.AsEnumerable();
                var disallowed = Enumerable.Empty<string>();
                var systemPrompt = EditorPrompt;
                if (_hasRhinoServer)
                {
                    allowed = allowed.Concat(_rhinoTools.Allowed(allowDestructive));
                    disallowed = _rhinoTools.Disallowed(allowDestructive);
                    systemPrompt += "\n\n" + _rhinoTools.Prompt(allowDestructive);
                }
                _runner.Send(prompt, config, allowed, disallowed, systemPrompt, ClaudeFolder(), ScriptFolder());
            }
            catch (Exception ex)
            {
                Write("Couldn't ask Claude: " + ScriptEditorHook.FirstLine(ex) + "\n");
                Prompt();
            }
        }

        private async void OnFinished(string text, double cost, bool isError)
        {
            var seconds = (DateTime.Now - _started).TotalSeconds;
            if (isError)
                Write("Claude Code: " + text.Trim() + "\n");
            if (_undoText != null)
                Write("(/undo restores the script)\n");
            // Time and cost go to the user log (the Log page), not into the chat.
            var spent = string.Format("{0:0} s{1}", seconds, cost > 0 ? string.Format(", ${0:0.000}", cost) : "");
            if (isError)
                UserLog.Error("Claude failed after {0}: {1}", spent, ScriptEditorHook.FirstLine(new Exception(text ?? "")));
            else
                UserLog.Info("Claude answered in {0}{1}.", spent, _undoText != null ? ", changed the script" : "");
            if (_mode == Mode.ReplaceSelection && !isError)
                await ApplyReplacement(text);
            if (_sessionKey != null && _runner.SessionId != null)
            {
                s_sessions[_sessionKey] = _runner.SessionId;
                SaveSessions();
            }
            Prompt();
        }

        // --- one conversation per script ---------------------------------------------------------

        /// <summary>Continues the conversation of the open script (or starts one).</summary>
        private void UseSessionOfScript()
        {
            var key = _access.ScriptPath ?? _access.ScriptTitle;
            if (key == _sessionKey)
                return;
            if (_sessionKey != null && _runner.SessionId != null)
                s_sessions[_sessionKey] = _runner.SessionId;
            _sessionKey = key;
            _runner.SessionId = s_sessions.TryGetValue(key, out var session) ? session : null;
            if (_runner.SessionId != null)
                Write("(continuing the conversation about " + _access.ScriptTitle + ")\n");
        }

        private static Dictionary<string, string> LoadSessions()
        {
            var sessions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (MiniVivisectionPlugin.Instance?.Settings.TryGetStringList("ClaudeSessions", out var saved) == true)
            {
                foreach (var entry in saved)
                {
                    var tab = entry.LastIndexOf('\t');
                    if (tab > 0)
                        sessions[entry.Substring(0, tab)] = entry.Substring(tab + 1);
                }
            }
            return sessions;
        }

        private static readonly List<string> s_configs = new List<string>();

        /// <summary>Rhino shuts down: this session's MCP configs (they name its server's URLs) go.</summary>
        public static void DeleteConfigs()
        {
            lock (s_configs)
            {
                foreach (var path in s_configs)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception)
                    {
                        // in use or gone
                    }
                }
                s_configs.Clear();
            }
        }

        /// <summary>Configs left by earlier sessions (a crash, or before this was done).</summary>
        private static void DeleteOldConfigs(string dir)
        {
            try
            {
                foreach (var path in Directory.GetFiles(dir, "mcp-*.json"))
                {
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                        File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Claude, "old MCP configs: {0}", Log.Describe(ex));
            }
        }

        private static void SaveSessions() =>
            MiniVivisectionPlugin.Instance?.Settings.SetStringList("ClaudeSessions", s_sessions.Take(50).Select(p => p.Key + "\t" + p.Value).ToArray());

        // --- changes waiting: queued during a run, or for review ------------------------------------

        /// <summary>EditGate.Queue: a change waits here (UI thread); the answer tells Claude.</summary>
        private string Queue(string what, string oldText, string newText)
        {
            var state = _access.RunState;
            _pending.Add((what, oldText, newText, state != null, _gate.Script ?? _access.Code?.Uri));
            Write(string.Format("[change waiting: {0}]\n{1}", what, Diff(oldText, newText)));
            if (state != null)
            {
                WaitUntilIdle();
                return "Queued: the script is " + state + ", and the run keeps the code it started with. The change is applied " +
                       (MiniVivisectionSettings.ClaudeEditApproval == "Ask" ? "when the run has stopped and the user confirms it (/apply)." : "by itself when the run has stopped.");
            }
            return "Proposed to the user, who reviews Claude's changes: applied with /apply, dropped with /reject. Don't repeat it.";
        }

        private static string Diff(string oldText, string newText)
        {
            string Lines(string text, string mark)
            {
                var lines = (text ?? "").Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                var shown = lines.Take(12).Select(l => mark + l);
                return string.Join("\n", shown) + (lines.Length > 12 ? "\n" + mark + "... (" + (lines.Length - 12) + " more lines)" : "") + "\n";
            }
            return Lines(oldText, "- ") + Lines(newText, "+ ");
        }

        private void ShowPending()
        {
            if (_pending.Count == 0)
            {
                Write("Nothing waiting.\n");
                return;
            }
            foreach (var change in _pending)
                Write(string.Format("[{0}{1}]\n{2}", change.What, change.WhileRunning ? ", queued during a run" : "", Diff(change.Old, change.New)));
            Write("/apply applies them, /reject drops them.\n");
        }

        /// <summary>The waiting changes, in order, each where its old text still is exactly
        /// once. Not while a run holds the script.</summary>
        private void ApplyPending()
        {
            if (_pending.Count == 0)
            {
                Write("Nothing waiting.\n");
                return;
            }
            if (_access.RunState is string state)
            {
                Write("The script is " + state + ": changes are applied when the run has stopped.\n");
                return;
            }
            var applied = 0;
            foreach (var change in _pending.ToList())
            {
                if (change.Script != null && _access.Code?.Uri != change.Script)
                {
                    Write(string.Format("Not applied ({0}): it is for {1}, and another script is open now -- switch back to it and /apply.\n", change.What, change.Script.Segments.LastOrDefault()));
                    continue;
                }
                var text = _access.ScriptText ?? "";
                var eol = text.Contains("\r\n") ? "\r\n" : "\n";
                var find = Normalize(change.Old).Replace("\n", eol);
                var at = find.Length == 0 ? -1 : text.IndexOf(find, StringComparison.Ordinal);
                if (at < 0 || text.IndexOf(find, at + 1, StringComparison.Ordinal) >= 0)
                {
                    Write(string.Format("Not applied ({0}): the code it changes isn't there exactly once any more.\n", change.What));
                    continue;
                }
                _undoText = _undoText ?? text;
                if (_access.SetScriptText(text.Substring(0, at) + (change.New ?? "").Replace("\r\n", "\n").Replace("\n", eol) + text.Substring(at + find.Length)))
                    applied++;
            }
            _pending.Clear();
            if (applied > 0)
                _afterText = _access.ScriptText;
            Write(string.Format("Applied {0} change(s) (/undo restores the script).\n", applied));
            if (applied > 0)
                UserLog.Success("Applied {0} change(s) from Claude to {1}.", applied, _access.ScriptTitle);
        }

        /// <summary>A debug run ended: changes queued during it are applied now (automatic
        /// edits) or announced (review) -- once the editor holds the script no more (after an
        /// error it waits for Stop, and wouldn't show the changes).</summary>
        private void AfterRun()
        {
            if (!_pending.Any(p => p.WhileRunning))
                return;
            if (_access.IsBusy)
            {
                WaitUntilIdle();
                return;
            }
            if (MiniVivisectionSettings.ClaudeEditApproval == "Ask")
            {
                Write(string.Format("The run ended: {0} change(s) waiting -- /pending shows them, /apply applies them.\n", _pending.Count));
                return;
            }
            Write("The run ended: applying the changes queued during it.\n");
            ApplyPending();
        }

        /// <summary>AfterRun once the run holds the script no more. The debugger's end isn't
        /// enough: stopped at a runtime error, the editor keeps its debugging state until Stop.</summary>
        private void WaitUntilIdle()
        {
            if (_idleWait != null)
                return;
            _idleWait = new UITimer { Interval = 0.5 };
            _idleWait.Elapsed += (sender, e) =>
            {
                if (_access.IsBusy)
                    return;
                _idleWait.Stop();
                _idleWait = null;
                AfterRun();
            };
            _idleWait.Start();
        }

        /// <summary>Replace selection: the answer's code block replaces the selection --
        /// if it is still selected, through the editor (Ctrl+Z undoes it); otherwise where
        /// the selected code still is, if it is there exactly once.</summary>
        private async Task ApplyReplacement(string answer)
        {
            var blocks = Regex.Matches(answer ?? "", "```[^\\n]*\\n(.*?)\\n?```", RegexOptions.Singleline);
            var original = _pendingReplace;
            _pendingReplace = null;
            if (blocks.Count == 0 || original == null)
            {
                Write("No code block in the answer -- nothing replaced.\n");
                return;
            }
            var code = blocks[blocks.Count - 1].Groups[1].Value;
            if (_gate.MustQueue(_access))
            {
                Queue("replace the selected code", original, code);
                return;
            }
            var before = _access.ScriptText;
            if (Normalize(await _access.SelectionAsync()) == Normalize(original) && await _access.ReplaceSelectionAsync(code))
            {
                _undoText = before;
                _afterText = _access.ScriptText;
                Write("Replaced the selection (Ctrl+Z in the editor undoes it).\n");
                return;
            }
            var text = before ?? "";
            var eol = text.Contains("\r\n") ? "\r\n" : "\n";
            var find = Normalize(original).Replace("\n", eol);
            var at = text.IndexOf(find, StringComparison.Ordinal);
            if (at >= 0 && text.IndexOf(find, at + 1, StringComparison.Ordinal) < 0 &&
                _access.SetScriptText(text.Substring(0, at) + code.Replace("\r\n", "\n").Replace("\n", eol) + text.Substring(at + find.Length)))
            {
                _undoText = before;
                _afterText = _access.ScriptText;
                Write("Replaced the code that was selected (/undo restores it).\n");
                return;
            }
            Write("The selected code changed meanwhile -- nothing replaced; the code is above.\n");
        }

        /// <summary>/undo: the script as it was before Claude's last changes -- only while it is
        /// as Claude left it, so your own typing since isn't lost (/undo force: anyway).</summary>
        private void UndoChanges(bool force)
        {
            if (_undoText == null)
            {
                Write("Nothing to undo.\n");
                return;
            }
            if (_access.RunState is string state)
            {
                Write("The script is " + state + ": /undo when the run has stopped.\n");
                return;
            }
            if (!force && _afterText != null && Normalize(_access.ScriptText) != Normalize(_afterText))
            {
                Write("The script changed since Claude's last change: /undo would take your own edits back too. " +
                      "Use the editor's Ctrl+Z for single steps, or /undo force to restore the script as it was before Claude's changes.\n");
                return;
            }
            if (_access.SetScriptText(_undoText))
                Write("Restored the script as it was before Claude's last changes.\n");
            else
                Write("The script is read-only -- nothing restored.\n");
            _undoText = null;
            _afterText = null;
        }

        /// <summary>The MCP config for Claude Code, written once: this tab's
        /// script-editor server, and RhinoAI's router as "rhino" when it is installed.</summary>
        private string McpConfig()
        {
            if (_mcpConfig != null)
                return _mcpConfig;
            var url = _mcpUrl = McpServer.Instance.Register(ScriptEditorTools.ServerName, ScriptEditorTools.Instructions,
                ScriptEditorTools.For(_access, _hook, _gate)
                    .Concat(DebugTools.For(_access, _hook, _gate, what => Ui(() => Write("  [debugger] " + what + "\n")))));
            var servers = new Dictionary<string, object>
            {
                [ScriptEditorTools.ServerName] = new { type = "http", url },
            };
            var router = RhinoMcpRouter();
            if (router != null)
                servers["rhino"] = new { type = "stdio", command = router, args = new string[0] };
            _mcpConfig = WriteConfig(servers);
            _hasRhinoServer = router != null;
            if (router == null)
                Write("RhinoAI's MCP router wasn't found -- Claude gets the Script Editor tools only.\n");
            return _mcpConfig;
        }

        /// <summary>An MCP config file for Claude Code naming <paramref name="servers"/>; deleted
        /// when Rhino shuts down (DeleteConfigs).</summary>
        internal static string WriteConfig(Dictionary<string, object> servers)
        {
            var dir = Path.Combine(Path.GetTempPath(), "rhinovivisection", "minivivisection");
            Directory.CreateDirectory(dir);
            DeleteOldConfigs(dir);
            var path = Path.Combine(dir, "mcp-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { mcpServers = servers }));
            lock (s_configs)
                s_configs.Add(path);
            return path;
        }

        /// <summary>RhinoAI's stdio MCP router: the Options page's path, else
        /// SCRIPTEDITORWATCHVIZ_RHINO_MCP, else the copy RhinoAI stages under
        /// %APPDATA%\McNeel\Rhinoceros\ai\bin, else an installed package's.</summary>
        internal static string RhinoMcpRouter()
        {
            var set = MiniVivisectionSettings.RhinoMcpRouterPath;
            if (!string.IsNullOrEmpty(set) && File.Exists(set))
                return set;
            var configured = MiniVivisectionSettings.Environment("RHINO_MCP");
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                return configured;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var staged = Path.Combine(appData, "McNeel", "Rhinoceros", "ai", "bin", "rhino-mcp-router.exe");
            if (File.Exists(staged))
                return staged;
            foreach (var version in new[] { "9.0", "8.0" })
            {
                var packages = Path.Combine(appData, "McNeel", "Rhinoceros", "packages", version, "Rhino-MCP-Platform");
                if (!Directory.Exists(packages))
                    continue;
                var found = Directory.GetDirectories(packages)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "router", "win-x64", "rhino-mcp-router.exe"))
                    .FirstOrDefault(File.Exists);
                if (found != null)
                    return found;
            }
            return null;
        }

        /// <summary>Where Claude Code runs: a folder of the plug-in's own, the same for every
        /// script -- not the script's folder, whose .claude settings (hooks, env) and CLAUDE.md
        /// a downloaded script could bring along. Its sessions are kept per folder, so one folder
        /// keeps them resumable.</summary>
        internal static string ClaudeFolder()
        {
            var folder = Path.Combine(Log.Folder, "claude");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>The script's folder, for Claude's Read/Glob/Grep (--add-dir); null when unsaved.</summary>
        private string ScriptFolder()
        {
            var path = _access.ScriptPath;
            var dir = path != null ? Path.GetDirectoryName(path) : null;
            return dir != null && Directory.Exists(dir) ? dir : null;
        }

        private string _lastRhinoAiNote;

        /// <summary>EditGate.AskToRun: the code in the tab, and /run or /deny (2 minutes).</summary>
        private Task<bool> AskToRun(string what, string code)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui(() =>
            {
                // One question at a time: an earlier one still open is withdrawn -- said, so
                // that Run isn't read as an answer to the code shown above
                if (_approval != null && _approval.TrySetResult(false))
                    Write("[the question above is withdrawn: not run -- Claude asks again]\n");
                _approval = answer;
                var shown = (code ?? "").TrimEnd();
                Write(string.Format("[Claude asks to run code: {0}]\n```python\n{1}\n```\nRun or Deny above, or type /run or /deny (2 minutes).\n", what, shown));
                // The bar names the code too (its first line), so Run means this code and no other
                var firstLine = shown.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                ShowAnswerBar(what + (firstLine.Length > 0 ? ": " + (firstLine.Length > 60 ? firstLine.Substring(0, 60) + "…" : firstLine) : ""));
                Prompt(); // to answer now, while Claude's answer goes on above it
                _view.FocusInput();
                UserLog.Info("Claude asks to run code ({0}): /run or /deny in V Claude.", what);
            });
            Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ =>
            {
                if (answer.TrySetResult(false))
                    Ui(() =>
                    {
                        Write("[not run: no answer within 2 minutes]\n");
                        if (_approval == answer)
                        {
                            _approval = null;
                            _answerBar.Visible = false;
                        }
                    });
            }, TaskScheduler.Default);
            return answer.Task;
        }

        // --- the question's answer: buttons above the chat, or /run and /deny -------------------

        private Panel _answerBar;
        private Label _answerQuestion;

        /// <summary>Above the chat, shown only while Claude asks to run code: what it asks, and Run
        /// and Deny buttons -- the same as typing /run or /deny.</summary>
        private Control MakeAnswerBar()
        {
            _answerQuestion = new Label { VerticalAlignment = VerticalAlignment.Center, Wrap = WrapMode.Word };
            var run = new Button { Text = "Run", ToolTip = "Run the code Claude asks to run (/run)", MinimumSize = new Size(70, 0) };
            var deny = new Button { Text = "Deny", ToolTip = "Don't run it (/deny)", MinimumSize = new Size(70, 0) };
            run.Click += (s, e) => Answer(true);
            deny.Click += (s, e) => Answer(false);
            var row = new TableLayout { Spacing = new Size(8, 0), Rows = { new TableRow(new TableCell(_answerQuestion, true), run, deny) } };
            _answerBar = new Panel { Content = row, Padding = new Padding(8, 5), Visible = false, BackgroundColor = Color.FromArgb(255, 244, 206) };
            return _answerBar;
        }

        private void ShowAnswerBar(string what)
        {
            _answerQuestion.Text = "Claude asks to run code: " + what + ".";
            _answerQuestion.TextColor = Color.FromArgb(60, 45, 0);
            _answerBar.Visible = true;
        }

        /// <summary>The answer to Claude's question -- a button, or /run and /deny (UI thread).</summary>
        private void Answer(bool run)
        {
            var waiting = _approval;
            _approval = null;
            _answerBar.Visible = false;
            if (waiting == null || !waiting.TrySetResult(run))
                Write("No code is waiting to run.\n");
            else
                Write(run ? "Running it.\n" : "Refused.\n");
            _view.FocusInput();
        }

        private static string ShortToolName(string name)
        {
            var prefix = "mcp__" + ScriptEditorTools.ServerName + "__";
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length)
                : name.StartsWith("mcp__", StringComparison.Ordinal) ? name.Substring(5).Replace("__", ".") : name;
        }

        private static string Normalize(string text) => (text ?? "").Replace("\r\n", "\n").Trim();

        private void Write(string text) => _view.Write(text);

        private static void Ui(Action action) => RhinoApp.InvokeOnUiThread(action);
    }
}
