using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MiniVivisection
{
    /// <summary>
    /// Which of RhinoAI's MCP tools the Claude tab lets Claude use, and what it tells
    /// Claude about them -- from <c>rhino_tools.json</c> next to the plug-in (RhinoAI's
    /// tools change between versions; the file can be updated without a rebuild). A copy in
    /// the plug-in's settings folder comes first: a Package Manager update installs into a
    /// new folder, so edits next to the .rhp would be lost.
    /// Read-only and modify tools are allowed; destructive ones (commands, scripts,
    /// open/save/close) only when the tab's checkbox allows them; excluded ones never.
    /// Claude Code runs headless, so an allowed tool runs without asking.
    /// </summary>
    internal sealed class RhinoToolPolicy
    {
        private const string Prefix = "mcp__rhino__";

        private RhinoToolPolicy()
        {
        }

        public string[] ReadOnly { get; private set; } = new string[0];

        public string[] Modify { get; private set; } = new string[0];

        public string[] Destructive { get; private set; } = new string[0];

        public Dictionary<string, string> Excluded { get; private set; } = new Dictionary<string, string>();

        public string[] Guidance { get; private set; } = new string[0];

        /// <summary>Where it came from, or why the built-in fallback is used.</summary>
        public string Source { get; private set; }

        /// <summary>The user's copy, kept across updates: %APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\miniVivisection (…)\settings\rhino_tools.json;
        /// null outside Rhino.</summary>
        public static string UserFile
        {
            get
            {
                try
                {
                    var folder = MiniVivisectionPlugin.Instance?.SettingsDirectory;
                    return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "rhino_tools.json");
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static RhinoToolPolicy Load()
        {
            var policy = new RhinoToolPolicy();
            var path = UserFile is string user && File.Exists(user)
                ? user
                : Path.Combine(Path.GetDirectoryName(typeof(RhinoToolPolicy).Assembly.Location) ?? "", "rhino_tools.json");
            try
            {
                using (var document = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    var root = document.RootElement;
                    policy.ReadOnly = Strings(root, "readOnly");
                    policy.Modify = Strings(root, "modify");
                    policy.Destructive = Strings(root, "destructive");
                    policy.Guidance = Strings(root, "guidance");
                    if (root.TryGetProperty("excluded", out var excluded) && excluded.ValueKind == JsonValueKind.Object)
                        policy.Excluded = excluded.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
                }
                policy.Source = path;
            }
            catch (Exception ex)
            {
                // Without the file: the context and selection tools only.
                policy.ReadOnly = new[] { "get_context", "get_selection", "list_objects" };
                policy.Source = "built-in minimum (" + ex.Message + ")";
            }
            return policy;
        }

        /// <summary>Claude Code's --allowedTools entries for the rhino server.</summary>
        public IEnumerable<string> Allowed(bool allowDestructive)
        {
            var tools = ReadOnly.Concat(Modify);
            if (allowDestructive)
                tools = tools.Concat(Destructive);
            return tools.Select(t => Prefix + t);
        }

        /// <summary>--disallowedTools entries: removed from Claude's tools altogether, so it
        /// doesn't try them.</summary>
        public IEnumerable<string> Disallowed(bool allowDestructive)
        {
            var tools = Excluded.Keys.AsEnumerable();
            if (!allowDestructive)
                tools = tools.Concat(Destructive);
            return tools.Select(t => Prefix + t);
        }

        /// <summary>The system prompt's part about the rhino tools; <paramref name="keep"/> drops guidance
        /// lines that don't apply where the chat is (the Grasshopper window has no script-editor tools).</summary>
        public string Prompt(bool allowDestructive, Func<string, bool> keep = null)
        {
            var lines = new List<string> { "rhino tools (RhinoAI), for the Rhino document and model:" };
            lines.AddRange(Guidance.Where(g => keep == null || keep(g)).Select(g => "- " + g));
            lines.Add(allowDestructive
                ? "- Allowed now as well: " + string.Join(", ", Destructive) + "."
                : "- Not allowed in this tab right now: " + string.Join(", ", Destructive) +
                  ". Tell the user what to do, or ask them to allow these tools (/allow on).");
            foreach (var excluded in Excluded)
                lines.Add(string.Format("- {0} isn't available: {1}.", excluded.Key, excluded.Value));
            return string.Join("\n", lines);
        }

        private static string[] Strings(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
                : new string[0];
        }
    }
}
