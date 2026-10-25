using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eto.Forms;
using Rhino;

namespace MiniVivisection
{
    /// <summary>
    /// V°Claude in Grasshopper's prompts window: Claude Code working in the open definition.
    /// Its tools: RhinoAI's g1_* where they serve (the canvas graph, the component library,
    /// placing, wiring, deleting, solving; the Rhino view), and miniVivisection.GH's
    /// <c>grasshopper</c> server for the rest (GrasshopperBridge: the selection, a parameter's
    /// data with the geometry's facts, scripts, values, nicknames, enabled/preview, groups,
    /// removing wires, Markdown notes, an image of the canvas). Each message carries a context
    /// line -- the definition, the solver, the selection -- and everything one answer changed is
    /// one step in Grasshopper's undo list (ClaudeUndo). One conversation per definition.
    ///
    /// Console style (TerminalView), as the Script Editor's V°Claude: Enter sends, Shift+Enter a
    /// new line, Ctrl+C stops an answer, Tab completes a / command. Options: the tab
    /// Grasshopper, group "V Claude in Grasshopper" (read only, undo, selection, RhinoAI, items, capture), and the
    /// Claude group (Claude Code's path, the model, RhinoAI's server, /allow's default).
    /// </summary>
    internal sealed class GrasshopperChat : Panel
    {
        private enum Mode
        {
            Chat,
            Explain,
            Fix,
        }

        /// <summary>Undo records of V°Claude start with this (ClaudeUndo.UndoLast).</summary>
        public const string UndoPrefix = "V°Claude: ";

        private const string Prompt =
            "You are helping inside Grasshopper (Rhino 8), in miniVivisection's V°Claude window. The user works on the definition open in " +
            "Grasshopper's window. Each message starts with a context line: the definition, the solver, and the objects selected on the canvas " +
            "(what 'this' and 'these' mean).\n" +
            "grasshopper tools (miniVivisection): gh_get_selection (the selected objects in detail), gh_get_data (a parameter's data in full, " +
            "with each item's facts: coordinates, length, area, volume, closed), gh_get_script, gh_set_value (sliders, Panels, toggles, value lists, " +
            "stored values, Rhino objects referenced), gh_set_script (a script component's code and inputs/outputs), gh_edit_objects (nickname, " +
            "enabled, preview, move, group), gh_disconnect (remove wires), gh_add_note (a Markdown Panel), gh_capture_canvas (an image of the canvas; " +
            "Read the file it names to see it).\n";

        private const string RhinoAiPrompt =
            "Grasshopper tools of RhinoAI (the rhino server): g1_get_canvas_graph (the whole canvas: objects, wires, messages, data samples), " +
            "g1_search_components and g1_describe_component (before placing), g1_place_component, g1_place_slider, g1_apply_graph (several at once), " +
            "g1_connect, g1_connect_many (replace: true rewires an input), g1_delete_component, g1_solve_graph. g1 is Grasshopper 1, the one open " +
            "here: don't use the g2 tools. get_viewport_image shows the Rhino view with Grasshopper's preview.\n" +
            "Sliders: don't use g1_place_slider (in this RhinoAI it fails on its number arguments): place 'Number Slider' with " +
            "g1_place_component and set its range, value and decimals with gh_set_value and its nickname with gh_edit_objects.\n" +
            "g1_delete_component takes the object's instance id as 'selector' (not 'id'). get_viewport_image: keep its default size " +
            "(480 x 270) -- larger images are too big to come back; frame the geometry with boxMin and boxMax instead.\n";

        private const string WorkPrompt =
            "Work the loop: read before changing (gh_get_selection for what the user means, g1_get_canvas_graph for the whole); search and " +
            "describe a component before placing it; place new components near what they connect to, left to right, about 200 px per step; " +
            "wire them; set values; then check the result -- the messages in the tools' answers, gh_get_data for counts, lengths, areas, " +
            "closed, gh_capture_canvas for the layout, get_viewport_image for the geometry -- and fix what's wrong before you answer.\n" +
            "Script components: place 'Python 3 Script' (or 'C# Script'), then gh_set_script with the code and its inputs and outputs. In Python 3 " +
            "the inputs are variables named like the inputs, and outputs are set by assigning to their names.\n" +
            "Everything one answer changes is one undo step for the user (Ctrl+Z in Grasshopper, /undo here). Say in a few lines what you did; " +
            "leave a note on the canvas (gh_add_note) when the user asks or you built more than a few components. Be concise.";

        private const string ReadOnlyPrompt =
            "Right now you may only read the definition (the user's setting 'Claude may change the definition' is Read only): explain, find " +
            "causes, and propose changes in words -- don't try to change anything.";

        private const string Help =
            "Type a message and press Enter (Shift+Enter: a new line). Ctrl+C stops an answer. Tab completes a / command.\n" +
            "Each message names the definition and the selected components: select what you mean, then ask.\n" +
            "  /explain [question]     explain the selected components (or the whole definition)\n" +
            "  /fix [note]             find and fix the components with errors or warnings\n" +
            "  /undo                   take back Claude's last change (also Ctrl+Z in Grasshopper)\n" +
            "  /history                the messages of this chat\n" +
            "  /allow on|off           let Claude use RhinoAI's commands and scripts (run_command, run_python, ...), without asking\n" +
            "  /new                    start a new chat (about this definition)\n" +
            "  /clear                  clear this view\n" +
            "  /help                   this list\n";

        private static readonly HashSet<string> s_whileAnswering = new HashSet<string> { "/help", "/clear", "/history" };

        private readonly RhinoToolPolicy _rhinoTools = RhinoToolPolicy.Load();
        private readonly ClaudeRunner _runner = new ClaudeRunner();
        private readonly TerminalView _view;
        private readonly List<string> _history = new List<string>();
        private bool _allowDestructive = MiniVivisectionSettings.ClaudeAllowRhinoTools;
        private string _mcpConfig;
        private string _mcpUrl;
        private string _configFor; // the settings the config was made for
        private bool _hasRhinoServer;
        private string _lastRhinoAiNote;
        private object _before;    // the definition before the answer (ClaudeUndo)
        private string _instruction;
        private DateTime _started;
        private string _sessionKey;
        private string _answerFor;   // the definition the answer being written is about (DocumentKey)
        private UITimer _watch;      // while answering: the canvas still shows that definition?

        private static readonly Dictionary<string, string> s_sessions = LoadSessions();

        public GrasshopperChat()
        {
            _view = new TerminalView(null, wrap: true)
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
                Log.Now(LogArea.Claude, "Grasshopper: tool {0} {1}", name, Short(input, 200)); // on disk at once: the last step before a crash
                if (name != "ToolSearch")
                    Ui(() => Write(string.Format("  [{0}] {1}\n", ShortToolName(name), Short(input, 300))));
            };
            _runner.Finished += (text, cost, isError) => Ui(() => OnFinished(text, cost, isError));
            Content = _view;
            Write("Claude Code in Grasshopper (experimental, miniVivisection). It reads the definition -- the selection, the data, the scripts -- " +
                  "and, with RhinoAI, places, wires and sets components; it writes scripts and notes. Each answer is one undo step.\n" + Help + "\n");
            ShowPrompt();
        }

        public void FocusInput() => _view.FocusInput();

        /// <summary>Rhino shuts down, or the window goes: the answer stops, the tools go.</summary>
        public void Shutdown()
        {
            if (_runner.Busy)
                _runner.Stop();
            if (_mcpUrl != null)
                McpServer.Instance.Unregister(_mcpUrl);
            _mcpUrl = null;
        }

        /// <summary>For tests: a message typed at the prompt and sent.</summary>
        internal void Type(string line) => _view.Type(line);

        /// <summary>For tests: what the chat shows.</summary>
        internal string Shown => _view.TextNow;

        internal bool Busy => _runner.Busy;

        private void ShowPrompt()
        {
            if (!_view.IsPrompting)
                _view.Prompt("> ");
        }

        private void Submit(string input)
        {
            var text = input.Trim();
            var space = text.IndexOfAny(new[] { ' ', '\n' });
            var command = text.StartsWith("/") ? (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant() : null;
            var rest = command == null ? text : (space < 0 ? "" : text.Substring(space + 1).Trim());
            if (_runner.Busy && text.Length > 0 && (command == null || !s_whileAnswering.Contains(command)))
            {
                Write("Claude is still answering -- the rest when it's done (Ctrl+C stops it).\n");
                ShowPrompt();
                return;
            }
            switch (command)
            {
                case null when text.Length == 0:
                    break;
                case null:
                    Send(Mode.Chat, rest);
                    return;
                case "/explain":
                    Send(Mode.Explain, rest);
                    return;
                case "/fix":
                    Send(Mode.Fix, rest);
                    return;
                case "/undo":
                    Write(Safe(() => GrasshopperBridge.UndoLast(UndoPrefix)) + "\n");
                    break;
                case "/history":
                    Write(_history.Count == 0 ? "No messages yet.\n" : string.Join("\n", _history) + "\n");
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
            ShowPrompt();
        }

        private void Send(Mode mode, string instruction)
        {
            if (_runner.Busy)
                return;
            try
            {
                if (!GrasshopperBridge.Loaded)
                {
                    Write("Grasshopper isn't started -- open it, then ask again.\n");
                    ShowPrompt();
                    return;
                }
                var context = GrasshopperBridge.Context(MiniVivisectionSettings.GhClaudeSelection) ?? "[Grasshopper: no definition is open]";
                var readOnly = MiniVivisectionSettings.GhClaudeReadOnly;
                string prompt;
                switch (mode)
                {
                    case Mode.Explain:
                        prompt = context + "\n\nExplain briefly what the selected components do -- or, with nothing selected, the whole definition: " +
                                 "read them first (gh_get_selection, g1_get_canvas_graph, gh_get_script)." + (instruction.Length > 0 ? "\n\n" + instruction : "");
                        break;
                    case Mode.Fix:
                        prompt = context + "\n\nSome components show errors or warnings. Find them (g1_get_canvas_graph has every object's messages, " +
                                 "gh_get_selection the selected ones'), find the cause (gh_get_data, gh_get_script) and " +
                                 (readOnly ? "say what to change." : "fix it; then check that the messages are gone.") +
                                 (instruction.Length > 0 ? "\n\nNote from the user: " + instruction : "");
                        break;
                    default:
                        prompt = context + "\n\n" + instruction;
                        break;
                }
                _instruction = instruction.Length > 0 ? instruction : mode.ToString().ToLowerInvariant();
                _started = DateTime.Now;
                UseSessionOfDefinition();
                _history.Add(string.Format("{0:HH:mm} {1}", DateTime.Now, _instruction));
                var config = McpConfig(readOnly);
                if (_hasRhinoServer)
                {
                    var note = MiniVivisectionSettings.ClaudeStartRhinoAi ? RhinoAiServer.EnsureRunning(RhinoDoc.ActiveDoc) : RhinoAiServer.NotRunningNote(RhinoDoc.ActiveDoc);
                    if (note != null && note != _lastRhinoAiNote)
                        Write("RhinoAI: " + note + "\n");
                    _lastRhinoAiNote = note;
                }
                var (allowed, disallowed, systemPrompt) = Permissions(readOnly, _allowDestructive);
                _answerFor = GrasshopperBridge.DocumentKey();
                GrasshopperBridge.AnswerFor = _answerFor;
                Log.Now(LogArea.Claude, "Grasshopper: message about {0} ({1}), undo step {2}", _answerFor ?? "no definition", mode,
                    MiniVivisectionSettings.GhClaudeUndo && !readOnly);
                _before = MiniVivisectionSettings.GhClaudeUndo && !readOnly ? GrasshopperBridge.BeginUndo() : null;
                WatchDefinition();
                Directory.CreateDirectory(GrasshopperBridge.CaptureFolder);
                _runner.Send(prompt, config, allowed, disallowed, systemPrompt, ClaudeChat.ClaudeFolder(), GrasshopperBridge.CaptureFolder);
            }
            catch (Exception ex)
            {
                _before = null;
                StopWatching();
                Write("Couldn't ask Claude: " + ScriptEditorHook.FirstLine(ex) + "\n");
                ShowPrompt();
            }
        }

        /// <summary>What Claude may use, and what it's told: our tools (the changing ones not when
        /// read only), Read (the canvas images), RhinoAI's per its policy -- its g1 tools only with
        /// "Use RhinoAI's Grasshopper tools", the changing ones not when read only, never g2's.</summary>
        internal (List<string> Allowed, List<string> Disallowed, string SystemPrompt) Permissions(bool readOnly, bool allowDestructive)
        {
            var allowed = new List<string> { "mcp__" + GrasshopperBridge.ServerName, "Read" };
            var disallowed = new List<string>();
            var system = Prompt;
            if (_hasRhinoServer)
            {
                const string prefix = "mcp__rhino__";
                bool Grasshopper2(string tool) => tool.StartsWith("g2_", StringComparison.Ordinal);
                bool Grasshopper1(string tool) => tool.StartsWith("g1_", StringComparison.Ordinal);
                var useG1 = MiniVivisectionSettings.GhClaudeRhinoAi;
                var mayUse = _rhinoTools.ReadOnly.Concat(readOnly ? new[] { "get_viewport_image", "zoom_to_object", "set_camera" }.Where(_rhinoTools.Modify.Contains) : _rhinoTools.Modify)
                    .Concat(allowDestructive && !readOnly ? _rhinoTools.Destructive : new string[0])
                    .Where(t => !Grasshopper2(t) && (useG1 || !Grasshopper1(t)))
                    .Distinct().ToList();
                var all = _rhinoTools.ReadOnly.Concat(_rhinoTools.Modify).Concat(_rhinoTools.Destructive).Concat(_rhinoTools.Excluded.Keys).Distinct();
                allowed.AddRange(mayUse.Select(t => prefix + t));
                disallowed.AddRange(all.Where(t => !mayUse.Contains(t)).Select(t => prefix + t));
                if (useG1)
                    system += RhinoAiPrompt;
                else
                    system += "RhinoAI's Grasshopper tools are off (the user's setting): you can't place, wire or delete components -- say what to place and wire.\n";
                system += WorkPrompt + "\n\n" + _rhinoTools.Prompt(allowDestructive && !readOnly, g => !g.Contains("script-editor") && !g.StartsWith("Grasshopper:", StringComparison.Ordinal));
            }
            else
            {
                system += "RhinoAI isn't installed: you can't place, wire or delete components -- say what to place and wire, and use the grasshopper tools for the rest.\n" + WorkPrompt;
            }
            if (readOnly)
                system += "\n\n" + ReadOnlyPrompt;
            return (allowed, disallowed, system);
        }

        /// <summary>While Claude answers, the canvas must go on showing the definition the message was
        /// about: RhinoAI's tools and ours work on the one shown. Another one shown (the user switched
        /// definitions) stops the answer -- before any tool works on the wrong definition.</summary>
        private void WatchDefinition()
        {
            if (_watch == null)
            {
                _watch = new UITimer { Interval = 0.25 };
                _watch.Elapsed += (s, e) =>
                {
                    if (!_runner.Busy)
                    {
                        StopWatching();
                        return;
                    }
                    string now;
                    try
                    {
                        now = GrasshopperBridge.DocumentKey();
                    }
                    catch (Exception)
                    {
                        now = null;
                    }
                    if (now == _answerFor)
                        return;
                    StopWatching();
                    Log.Now(LogArea.Claude, "Grasshopper: the canvas shows {0} now, not {1}: the answer is stopped", now ?? "no definition", _answerFor);
                    Write("\n[Grasshopper shows another definition now: Claude's answer is stopped, so that nothing changes the wrong one. " +
                          "Go back to it and ask again, or ask about this one.]\n");
                    _runner.Stop();
                };
            }
            _watch.Start();
        }

        private void StopWatching()
        {
            _watch?.Stop();
            GrasshopperBridge.AnswerFor = null;
        }

        private void OnFinished(string text, double cost, bool isError)
        {
            StopWatching();
            Log.Now(LogArea.Claude, "Grasshopper: answer finished{0}", isError ? " with an error" : "");
            var seconds = (DateTime.Now - _started).TotalSeconds;
            if (isError)
                Write("Claude Code: " + text.Trim() + "\n");
            string changed = null;
            try
            {
                changed = GrasshopperBridge.CommitUndo(_before, UndoPrefix + Short(_instruction, 40));
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Claude, "Grasshopper undo step: {0}", Log.Describe(ex));
            }
            _before = null;
            if (changed != null)
                Write("[the definition changed: " + changed + " -- Ctrl+Z in Grasshopper or /undo takes it back]\n");
            var spent = string.Format("{0:0} s{1}", seconds, cost > 0 ? string.Format(", ${0:0.000}", cost) : "");
            if (isError)
                UserLog.Error("Claude (Grasshopper) failed after {0}: {1}", spent, ScriptEditorHook.FirstLine(new Exception(text ?? "")));
            else
                UserLog.Info("Claude (Grasshopper) answered in {0}{1}.", spent, changed != null ? ", changed the definition (" + changed + ")" : "");
            if (_sessionKey != null && _runner.SessionId != null)
            {
                s_sessions[_sessionKey] = _runner.SessionId;
                SaveSessions();
            }
            ShowPrompt();
        }

        // --- the tools ---------------------------------------------------------------------------

        /// <summary>The MCP config: the grasshopper server (its tools for the settings now) and
        /// RhinoAI's router as "rhino"; made again when the settings it depends on changed.</summary>
        private string McpConfig(bool readOnly)
        {
            var key = readOnly + "|" + MiniVivisectionSettings.GhClaudeCapture;
            if (_mcpConfig != null && key == _configFor)
                return _mcpConfig;
            if (_mcpUrl != null)
                McpServer.Instance.Unregister(_mcpUrl);
            _mcpUrl = McpServer.Instance.Register(GrasshopperBridge.ServerName, GrasshopperBridge.Instructions,
                GrasshopperBridge.Tools(readOnly, MiniVivisectionSettings.GhClaudeCapture).ToList());
            var servers = new Dictionary<string, object> { [GrasshopperBridge.ServerName] = new { type = "http", url = _mcpUrl } };
            var router = ClaudeChat.RhinoMcpRouter();
            if (router != null)
                servers["rhino"] = new { type = "stdio", command = router, args = new string[0] };
            if (router == null && !_hasRhinoServer && _mcpConfig == null)
                Write("RhinoAI's MCP router wasn't found -- Claude gets miniVivisection's Grasshopper tools only (it can't place components).\n");
            _hasRhinoServer = router != null;
            _mcpConfig = ClaudeChat.WriteConfig(servers);
            _configFor = key;
            return _mcpConfig;
        }

        // --- one conversation per definition -------------------------------------------------------

        private void UseSessionOfDefinition()
        {
            var key = "gh:" + (GrasshopperBridge.DocumentKey() ?? "none");
            if (key == _sessionKey)
                return;
            if (_sessionKey != null && _runner.SessionId != null)
                s_sessions[_sessionKey] = _runner.SessionId;
            _sessionKey = key;
            _runner.SessionId = s_sessions.TryGetValue(key, out var session) ? session : null;
            if (_runner.SessionId != null)
                Write("(continuing the conversation about " + (key.StartsWith("gh:unsaved ", StringComparison.Ordinal) ? "this definition" : Path.GetFileName(key.Substring(3))) + ")\n");
        }

        private static Dictionary<string, string> LoadSessions()
        {
            var sessions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (MiniVivisectionPlugin.Instance?.Settings.TryGetStringList("GhClaudeSessions", out var saved) == true)
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

        private static void SaveSessions() =>
            MiniVivisectionPlugin.Instance?.Settings.SetStringList("GhClaudeSessions", s_sessions.Take(50).Select(p => p.Key + "\t" + p.Value).ToArray());

        // --- small things ------------------------------------------------------------------------

        private static string Safe(Func<string> work)
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                return "Couldn't: " + ScriptEditorHook.FirstLine(ex);
            }
        }

        private static string ShortToolName(string name)
        {
            var prefix = "mcp__" + GrasshopperBridge.ServerName + "__";
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length)
                : name.StartsWith("mcp__", StringComparison.Ordinal) ? name.Substring(5).Replace("__", ".") : name;
        }

        private static string Short(string text, int max)
        {
            text = (text ?? "").Replace("\r", "").Replace("\n", " ");
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }

        private void Write(string text) => _view.Write(text);

        private static void Ui(Action action) => RhinoApp.InvokeOnUiThread(action);
    }
}
