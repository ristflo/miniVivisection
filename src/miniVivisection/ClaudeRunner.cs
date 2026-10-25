using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiniVivisection
{
    /// <summary>
    /// Claude Code, headless: one <c>claude -p</c> process per message, the prompt on
    /// stdin, <c>--output-format stream-json</c> read line by line; <c>--resume</c> keeps
    /// the conversation. MCP servers come from a config file
    /// (<c>--mcp-config</c>, <c>--strict-mcp-config</c>); allowed tools are those
    /// servers' plus read-only file tools, so Claude changes the script only through the
    /// script-editor tools. Events are raised on the reading thread.
    /// </summary>
    internal sealed class ClaudeRunner
    {
        private Process _process;

        /// <summary>Assistant text (a whole message step).</summary>
        public event Action<string> Text;

        /// <summary>A tool call: name and a short form of its input.</summary>
        public event Action<string, string> ToolUse;

        /// <summary>The session started: its MCP servers and their status.</summary>
        public event Action<string> Connected;

        /// <summary>The turn ended: final text, cost in USD, whether it failed.</summary>
        public event Action<string, double, bool> Finished;

        /// <summary>The Claude Code session the next message continues (--resume); null: a new one.</summary>
        public string SessionId { get; set; }

        public bool Busy => _process != null;

        public void NewSession() => SessionId = null;

        /// <summary>claude.exe: the Options page's path, else MINIVIVISECTION_CLAUDE, else on
        /// PATH (its absolute folders only: an empty or relative entry would mean Rhino's
        /// current folder), else where the installer puts it.</summary>
        public static string FindClaude()
        {
            var set = MiniVivisectionSettings.ClaudePath;
            if (!string.IsNullOrEmpty(set) && File.Exists(set))
                return set;
            var configured = MiniVivisectionSettings.Environment("CLAUDE");
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                return configured;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir.Trim()))
                    continue;
                foreach (var name in new[] { "claude.exe", "claude.cmd" })
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim(), name);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Path.Combine(home, ".local", "bin", "claude.exe");
            return File.Exists(local) ? local : null;
        }

        /// <summary>Claude Code's tools the Script Editor's Claude never gets.</summary>
        private static readonly string[] AlwaysRefused = { "Bash", "Edit", "MultiEdit", "Write", "NotebookEdit", "WebFetch", "WebSearch" };

        public void Send(string prompt, string mcpConfigPath, IEnumerable<string> allowedTools, IEnumerable<string> disallowedTools,
            string systemPrompt, string workingDirectory, string scriptFolder = null)
        {
            if (Busy)
                throw new InvalidOperationException("Claude is still answering.");
            var claude = FindClaude() ?? throw new FileNotFoundException("Claude Code (claude.exe) wasn't found -- install it, or set its path in Tools > Options > miniVivisection.");
            var start = new ProcessStartInfo(claude)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory,
            };
            foreach (var argument in new[] { "-p", "--output-format", "stream-json", "--verbose", "--mcp-config", mcpConfigPath, "--strict-mcp-config" })
                start.ArgumentList.Add(argument);
            // The default permission mode, and Claude Code's own tools that change files or
            // reach out always refused: permissions in the user's own Claude settings don't
            // widen what the Script Editor's Claude may do.
            start.ArgumentList.Add("--permission-mode");
            start.ArgumentList.Add("default");
            // The user's own settings only: no project or local .claude settings (hooks, env)
            // from any folder; the script's folder is reachable for Read, Glob and Grep.
            start.ArgumentList.Add("--setting-sources");
            start.ArgumentList.Add("user");
            if (scriptFolder != null)
            {
                start.ArgumentList.Add("--add-dir");
                start.ArgumentList.Add(scriptFolder);
            }
            start.ArgumentList.Add("--allowedTools");
            start.ArgumentList.Add(string.Join(",", allowedTools));
            var disallowed = AlwaysRefused.Concat(disallowedTools).Distinct().ToList();
            if (disallowed.Count > 0)
            {
                start.ArgumentList.Add("--disallowedTools");
                start.ArgumentList.Add(string.Join(",", disallowed));
            }
            if (!string.IsNullOrWhiteSpace(MiniVivisectionSettings.ClaudeModel))
            {
                start.ArgumentList.Add("--model");
                start.ArgumentList.Add(MiniVivisectionSettings.ClaudeModel.Trim());
            }
            if (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                // cmd.exe parses a .cmd's arguments: a prompt with line breaks would cut off
                // the ones after it. As a file instead.
                var promptFile = Path.Combine(Path.GetTempPath(), "minivivisection-claude-prompt.txt");
                File.WriteAllText(promptFile, systemPrompt);
                start.ArgumentList.Add("--append-system-prompt-file");
                start.ArgumentList.Add(promptFile);
            }
            else
            {
                start.ArgumentList.Add("--append-system-prompt");
                start.ArgumentList.Add(systemPrompt);
            }
            if (SessionId != null)
            {
                start.ArgumentList.Add("--resume");
                start.ArgumentList.Add(SessionId);
            }

            var process = Process.Start(start) ?? throw new InvalidOperationException("Claude Code didn't start.");
            _process = process;
            var errors = new StringBuilder();
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                    lock (errors)
                        errors.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();
            process.StandardInput.Write(prompt);
            process.StandardInput.Close();

            Task.Run(() =>
            {
                var finished = false;
                try
                {
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        if (Handle(line))
                            finished = true;
                    }
                    process.WaitForExit();
                }
                catch (Exception ex)
                {
                    lock (errors)
                        errors.AppendLine(ex.Message);
                }
                finally
                {
                    _process = null;
                    if (!finished)
                    {
                        string error;
                        lock (errors)
                            error = errors.ToString().Trim();
                        Finished?.Invoke(error.Length > 0 ? error : "Claude Code ended without an answer.", 0, true);
                    }
                    process.Dispose();
                }
            });
        }

        public void Stop()
        {
            try
            {
                _process?.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // already gone
            }
        }

        /// <summary>One stream-json line; true for the final result.</summary>
        private bool Handle(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
                return false;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                return false;
            }
            using (document)
            {
                var root = document.RootElement;
                var type = Str(root, "type");
                if (root.TryGetProperty("session_id", out var session) && session.ValueKind == JsonValueKind.String)
                    SessionId = session.GetString();
                switch (type)
                {
                    case "system" when Str(root, "subtype") == "init":
                        if (root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                            Connected?.Invoke(string.Join(", ", servers.EnumerateArray().Select(s => Str(s, "name") + ": " + Str(s, "status"))));
                        return false;
                    case "assistant":
                        if (root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                var kind = Str(block, "type");
                                if (kind == "text")
                                    Text?.Invoke(Str(block, "text"));
                                else if (kind == "tool_use")
                                    ToolUse?.Invoke(Str(block, "name"), block.TryGetProperty("input", out var input) ? Shorten(input.GetRawText(), 160) : "");
                            }
                        }
                        return false;
                    case "result":
                        var cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var usd) ? usd : 0;
                        var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                        Finished?.Invoke(Str(root, "result"), cost, isError);
                        return true;
                    default:
                        return false;
                }
            }
        }

        private static string Str(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : "";
        }

        private static string Shorten(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "...";
    }
}
