using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace MiniVivisection
{
    /// <summary>
    /// The Grasshopper side of the prompts window, in miniVivisection.GH (which references
    /// Grasshopper; the .rhp doesn't): its tools for Claude (GrasshopperTools), the undo step per
    /// answer (ClaudeUndo), the context line. Reached by reflection, as the .gha reaches the .rhp.
    /// Everything here runs on Rhino's UI thread.
    /// </summary>
    internal static class GrasshopperBridge
    {
        public const string ServerName = "grasshopper";

        public const string Instructions =
            "The Grasshopper definition open in Grasshopper's window: the selection, a parameter's data with the geometry's facts, " +
            "a script's code; setting sliders, Panels, toggles, value lists, stored values and Rhino references, writing scripts, " +
            "nicknames, enabled, preview, groups, removing wires, Markdown notes, an image of the canvas.";

        private static Type Type(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection.GH")?.GetType("MiniVivisection.GH." + name);

        private static object Invoke(string type, string method, params object[] arguments)
        {
            var found = Type(type)?.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
                        ?? throw new InvalidOperationException("miniVivisection.GH isn't loaded (Grasshopper isn't started, or its plug-in is missing).");
            try
            {
                return found.Invoke(null, arguments);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>Grasshopper is started and miniVivisection.GH is loaded.</summary>
        public static bool Loaded => Type("GrasshopperTools") != null;

        /// <summary>The folder gh_capture_canvas saves into (Claude may Read it).</summary>
        public static string CaptureFolder => Path.Combine(Path.GetTempPath(), "rhinovivisection", "minivivisection", "captures");

        /// <summary>The tools as MCP tools: read-only ones only (<paramref name="readOnly"/>), the capture
        /// only when allowed. Each runs on the UI thread.</summary>
        public static IEnumerable<McpServer.Tool> Tools(bool readOnly, bool capture)
        {
            if (!(Invoke("GrasshopperTools", "Tools") is object[][] tools))
                yield break;
            foreach (var tool in tools)
            {
                var name = (string)tool[0];
                var changes = (bool)tool[3];
                if ((changes && readOnly) || (name == "gh_capture_canvas" && !capture))
                    continue;
                yield return new McpServer.Tool(name, (string)tool[1], (string)tool[2],
                    arguments => ScriptEditorAccess.OnUiThread(() => Task.FromResult(Call(name, arguments))));
            }
        }

        /// <summary>The names of the tools that change the definition.</summary>
        public static string[] ChangingTools() =>
            Invoke("GrasshopperTools", "Tools") is object[][] tools ? tools.Where(t => (bool)t[3]).Select(t => (string)t[0]).ToArray() : new string[0];

        /// <summary>The definition the answer being written is about (GrasshopperChat); null between answers.
        /// A tool called while another one is shown refuses: it would change the wrong definition.</summary>
        public static string AnswerFor;

        public static string Call(string name, JsonElement arguments)
        {
            var pinned = AnswerFor;
            if (pinned != null && DocumentKey() != pinned)
                return "Not done: the user switched Grasshopper to another definition while you were answering. Stop here and tell the user; " +
                       "nothing was changed by this call.";
            Log.Now(LogArea.Claude, "Grasshopper tool {0}", name);
            var tools = Type("GrasshopperTools");
            tools?.GetField("MaxItems", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, MiniVivisectionSettings.GhClaudeItems);
            tools?.GetField("CaptureFolder", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, CaptureFolder);
            var answer = (string)Invoke("GrasshopperTools", "Call", name, arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText());
            Log.Debug(LogArea.Claude, "{0}: {1}", name, answer.Length > 200 ? answer.Substring(0, 200) + "..." : answer);
            return answer;
        }

        /// <summary>The context line for a message ("[Grasshopper: ..., selected: ...]"); null without a definition.</summary>
        public static string Context(bool selection) => Loaded ? (string)Invoke("GrasshopperTools", "Context", selection) : null;

        /// <summary>The open definition's file; null when unsaved, none, or Grasshopper isn't loaded.</summary>
        public static string DocumentPath() => Loaded ? (string)Invoke("GrasshopperTools", "DocumentPath") : null;

        /// <summary>Which definition a conversation is about: its file, or "unsaved (its id)"; null without one.</summary>
        public static string DocumentKey() => Loaded ? (string)Invoke("GrasshopperTools", "DocumentKey") : null;

        /// <summary>The definition before a message (ClaudeUndo.Begin); null without one.</summary>
        public static object BeginUndo() => Loaded ? Invoke("ClaudeUndo", "Begin") : null;

        /// <summary>After the answer: one undo record when it changed the definition; what changed, or null.</summary>
        public static string CommitUndo(object before, string name) => before == null ? null : (string)Invoke("ClaudeUndo", "Commit", before, name);

        private static MethodInfo s_remember;

        /// <summary>A script component's outputs before its data is cleared (PausedSolution.Remember); quiet.</summary>
        public static void RememberOutputs(object component)
        {
            try
            {
                if (s_remember == null)
                    s_remember = Type("PausedSolution")?.GetMethod("Remember", BindingFlags.Public | BindingFlags.Static);
                s_remember?.Invoke(null, new[] { component });
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Breakpoints, "remembering a script component's outputs: {0}", Log.Describe(ex));
            }
        }

        /// <summary>While <paramref name="paused"/> is paused: the held changes computed again (PausedSolution.Recompute).</summary>
        public static string RecomputeWhilePaused(object paused, object[] held) => Loaded ? (string)Invoke("PausedSolution", "Recompute", paused, held) : null;

        /// <summary>The pause is over: inputs filled from a paused component's last data expired (PausedSolution);
        /// their documents.</summary>
        public static object[] ExpireAfterPause() => Loaded ? (object[])Invoke("PausedSolution", "ExpireAfterPause") : new object[0];

        /// <summary>What's downstream of <paramref name="paused"/> marked on the canvas (null: no marks).</summary>
        public static void MarkPaused(object paused)
        {
            if (Loaded)
                Invoke("PausedSolution", "Mark", paused);
        }

        /// <summary>/undo: the last undo record, when it's V°Claude's.</summary>
        public static string UndoLast(string prefix) => Loaded ? (string)Invoke("ClaudeUndo", "UndoLast", prefix) : "Grasshopper isn't started.";
    }
}
