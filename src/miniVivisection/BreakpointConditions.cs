using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>What a breakpoint does besides stopping: a condition (pause only when it
    /// holds), a hit count (5, >= 5, % 10) and a log message (print instead of pausing).</summary>
    internal sealed class BreakpointSpec
    {
        public int Line;
        public string Condition = "";
        public string HitCondition = "";
        public string LogMessage = "";

        // For one debug run (StoppedRuns: Debug failing run N), not kept: the line's own spec
        // set aside meanwhile (null: it had none), back when that run ends.
        public bool OneShot;
        public BreakpointSpec Saved;

        // Per run, not kept.
        public int Hits;
        public string LastError;
        public bool ErrorReported;

        public bool IsEmpty => Condition.Length == 0 && HitCondition.Length == 0 && LogMessage.Length == 0;

        /// <summary>Compact, for the code view's marker and the Breakpoints tab.</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Condition.Length > 0)
                    parts.Add("if " + Condition);
                if (HitCondition.Length > 0)
                    parts.Add("hit " + HitCondition);
                if (LogMessage.Length > 0)
                    parts.Add("log " + LogMessage);
                return string.Join(" · ", parts);
            }
        }

        public BreakpointSpec Copy() => (BreakpointSpec)MemberwiseClone();
    }

    /// <summary>
    /// The breakpoints' conditions, hit counts and log messages, by script file and line
    /// -- RhinoCode's breakpoints have none, and the editor makes new breakpoint objects
    /// whenever the margin changes, so they're kept here and their lines are tracked in
    /// the code view (BreakpointMarkers). Kept in the plug-in's settings.
    /// </summary>
    internal static class BreakpointSpecs
    {
        private const string SettingsKey = "BreakpointSpecs";
        private static readonly object s_lock = new object();
        private static Dictionary<string, List<BreakpointSpec>> s_byFile;

        /// <summary>The specs of a file changed (its key); any thread.</summary>
        public static event Action<string> Changed;

        public static string Key(Uri uri) => uri == null ? "" : uri.IsFile ? uri.LocalPath.ToLowerInvariant() : uri.ToString().ToLowerInvariant();

        public static bool HasAny(Uri uri)
        {
            lock (s_lock)
                return Files.TryGetValue(Key(uri), out var specs) && specs.Count > 0;
        }

        public static BreakpointSpec Find(Uri uri, int line)
        {
            lock (s_lock)
                return Files.TryGetValue(Key(uri), out var specs) ? specs.FirstOrDefault(s => s.Line == line) : null;
        }

        /// <summary>Copies, by line.</summary>
        public static List<BreakpointSpec> Of(Uri uri)
        {
            lock (s_lock)
                return Files.TryGetValue(Key(uri), out var specs) ? specs.OrderBy(s => s.Line).Select(s => s.Copy()).ToList() : new List<BreakpointSpec>();
        }

        /// <summary>Changes the spec at <paramref name="line"/> (made when missing; removed
        /// when it ends up empty).</summary>
        public static void Set(Uri uri, int line, Action<BreakpointSpec> change)
        {
            var key = Key(uri);
            lock (s_lock)
            {
                if (!Files.TryGetValue(key, out var specs))
                    Files[key] = specs = new List<BreakpointSpec>();
                var spec = specs.FirstOrDefault(s => s.Line == line);
                if (spec == null)
                    specs.Add(spec = new BreakpointSpec { Line = line });
                if (spec.OneShot)
                {
                    // The user changes the line: theirs from now on, the one-run condition is over.
                    spec.OneShot = false;
                    spec.Saved = null;
                }
                change(spec);
                spec.Condition = (spec.Condition ?? "").Trim();
                spec.HitCondition = (spec.HitCondition ?? "").Trim();
                spec.LogMessage = (spec.LogMessage ?? "").Trim();
                spec.LastError = null;
                spec.ErrorReported = false;
                if (spec.IsEmpty)
                    specs.Remove(spec);
                if (specs.Count == 0)
                    Files.Remove(key);
                Save();
            }
            Log.Info(LogArea.Breakpoints, "{0} line {1}: {2}", key, line, Find(uri, line)?.Summary ?? "(none)");
            Changed?.Invoke(key);
        }

        public static void Remove(Uri uri, int line) => Set(uri, line, s =>
        {
            s.Condition = "";
            s.HitCondition = "";
            s.LogMessage = "";
        });

        /// <summary>A condition for the next debug run only (StoppedRuns): the line's own
        /// condition, hit count and log message are set aside, never lost, and never is the
        /// one-run condition saved -- it is gone after a restart even if the run never came.</summary>
        public static void SetOneShot(Uri uri, int line, string condition)
        {
            var key = Key(uri);
            lock (s_lock)
            {
                if (!Files.TryGetValue(key, out var specs))
                    Files[key] = specs = new List<BreakpointSpec>();
                var spec = specs.FirstOrDefault(s => s.Line == line);
                if (spec == null)
                    specs.Add(spec = new BreakpointSpec { Line = line });
                if (!spec.OneShot)
                {
                    spec.Saved = spec.IsEmpty ? null : spec.Copy();
                    spec.OneShot = true;
                }
                spec.Condition = (condition ?? "").Trim();
                spec.HitCondition = "";
                spec.LogMessage = "";
                spec.LastError = null;
                spec.ErrorReported = false;
                Save();
            }
            Log.Info(LogArea.Breakpoints, "{0} line {1}: for one run, if {2}", key, line, condition);
            Changed?.Invoke(key);
        }

        /// <summary>The one-run condition at <paramref name="line"/> over: the line's own spec
        /// back, or none. Nothing when the user changed the line meanwhile.</summary>
        public static void EndOneShot(Uri uri, int line)
        {
            var key = Key(uri);
            lock (s_lock)
            {
                if (!Files.TryGetValue(key, out var specs))
                    return;
                var spec = specs.FirstOrDefault(s => s.Line == line && s.OneShot);
                if (spec == null)
                    return;
                if (spec.Saved != null)
                {
                    spec.Condition = spec.Saved.Condition;
                    spec.HitCondition = spec.Saved.HitCondition;
                    spec.LogMessage = spec.Saved.LogMessage;
                }
                else
                {
                    specs.Remove(spec);
                }
                spec.OneShot = false;
                spec.Saved = null;
                spec.LastError = null;
                spec.ErrorReported = false;
                if (specs.Count == 0)
                    Files.Remove(key);
                Save();
            }
            Changed?.Invoke(key);
        }

        /// <summary>New lines for the file's specs, in line order (the code view's markers
        /// moved with an edit). False when nothing changed.</summary>
        public static bool SetLines(Uri uri, IList<int> lines)
        {
            var key = Key(uri);
            lock (s_lock)
            {
                if (!Files.TryGetValue(key, out var specs) || specs.Count != lines.Count)
                    return false;
                var ordered = specs.OrderBy(s => s.Line).ToList();
                var changed = false;
                for (var i = 0; i < ordered.Count; i++)
                {
                    if (lines[i] > 0 && ordered[i].Line != lines[i])
                    {
                        ordered[i].Line = lines[i];
                        changed = true;
                    }
                }
                if (!changed)
                    return false;
                Save();
            }
            Changed?.Invoke(key);
            return true;
        }

        /// <summary>A run starts: hit counts from zero, errors reported again.</summary>
        public static void ResetHits()
        {
            lock (s_lock)
            {
                foreach (var spec in Files.Values.SelectMany(s => s))
                {
                    spec.Hits = 0;
                    spec.ErrorReported = false;
                }
            }
        }

        private static Dictionary<string, List<BreakpointSpec>> Files
        {
            get
            {
                if (s_byFile == null)
                    s_byFile = Load();
                return s_byFile;
            }
        }

        // Kept as JSON entries: {"f": file, "l": line, "c": condition, "h": hit count, "m": message}.
        private static Dictionary<string, List<BreakpointSpec>> Load()
        {
            var files = new Dictionary<string, List<BreakpointSpec>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (MiniVivisectionPlugin.Instance?.Settings.TryGetStringList(SettingsKey, out var saved) != true)
                    return files;
                foreach (var entry in saved)
                {
                    using (var json = JsonDocument.Parse(entry))
                    {
                        var root = json.RootElement;
                        var file = root.GetProperty("f").GetString();
                        if (!files.TryGetValue(file, out var specs))
                            files[file] = specs = new List<BreakpointSpec>();
                        specs.Add(new BreakpointSpec
                        {
                            Line = root.GetProperty("l").GetInt32(),
                            Condition = root.TryGetProperty("c", out var c) ? c.GetString() : "",
                            HitCondition = root.TryGetProperty("h", out var h) ? h.GetString() : "",
                            LogMessage = root.TryGetProperty("m", out var m) ? m.GetString() : "",
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Breakpoints, "can't read the saved conditions: {0}", Log.Describe(ex));
            }
            return files;
        }

        private static void Save() => MiniVivisectionPlugin.Instance?.Settings.SetStringList(SettingsKey, Entries());

        /// <summary>What is kept in the settings, one JSON entry per spec (the checks read it).</summary>
        internal static string[] Entries()
        {
            // A one-run condition isn't kept: the line's own spec is, where it has one.
            return Files.SelectMany(f => f.Value
                .Select(s => (s.Line, Kept: s.OneShot ? s.Saved : s))
                .Where(s => s.Kept != null && !s.Kept.IsEmpty)
                .Select(s => JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["f"] = f.Key, ["l"] = s.Line, ["c"] = s.Kept.Condition, ["h"] = s.Kept.HitCondition, ["m"] = s.Kept.LogMessage,
                }))).ToArray();
        }
    }

    /// <summary>
    /// Conditional breakpoints, hit counts and logpoints, in RhinoCode's own decision to
    /// pause: a Harmony postfix on <c>NextStep.ShouldPause</c>. RhinoCode's trace loop
    /// (<c>DebugControls.Trace</c>) asks it for every traced line, and only a true answer
    /// sets the Paused state and raises the Paused event -- a false one runs on, and
    /// nothing reacts. The postfix acts only on a breakpoint hit while running freely
    /// (<c>NextStepKind.PauseOnNextBreakpoint</c>: stepping onto the line still stops) for
    /// a line with a spec. It runs on the script's thread inside Python's trace callback
    /// (the GIL is held, tracing is off), so Python conditions are evaluated in the traced
    /// frame; C# conditions by CSharpConditions over the frame's variables. Anything
    /// unexpected: pause, as without a condition.
    /// </summary>
    internal static class BreakpointConditions
    {
        private static Harmony s_harmony;
        private static FieldInfo s_stepKind;
        private static string s_error;

        /// <summary>For the health line (EditorHooks): null when installed, else why not.</summary>
        internal static string HookProblem => s_harmony != null ? null : s_error ?? "not installed";

        [ThreadStatic]
        private static string t_reason;

        public static void TryInstall()
        {
            if (s_harmony != null || s_error != null)
                return;
            try
            {
                s_stepKind = typeof(NextStep).GetField("_stepKind", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new MissingFieldException(nameof(NextStep), "_stepKind");
                var shouldPause = typeof(NextStep).GetMethod(nameof(NextStep.ShouldPause), BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingMethodException(nameof(NextStep), nameof(NextStep.ShouldPause));
                var harmony = new Harmony("minivivisection.breakpointconditions");
                harmony.Patch(shouldPause, postfix: new HarmonyMethod(typeof(BreakpointConditions), nameof(ShouldPausePostfix)));
                s_harmony = harmony;
                Log.Debug(LogArea.Breakpoints, "NextStep.ShouldPause patched");
            }
            catch (Exception ex)
            {
                s_error = ex.Message;
                UserLog.Warning("Conditional breakpoints aren't available: {0}", ex.Message);
            }
        }

        /// <summary>Why the debugger just paused, when a spec decided it ("if i > 3, hit
        /// 4"); taken once, on the paused thread (ScriptEditorHook.OnPaused).</summary>
        public static string TakeReason()
        {
            var reason = t_reason;
            t_reason = null;
            return reason;
        }

        private static void ShouldPausePostfix(NextStep __instance, DebugControls controls, ExecFrame frame, DebugThreadEventStack stack, ref bool __result, ref ExecFrame headFrame)
        {
            if (frame == null || frame.Event != ExecEvent.Line)
                return;
            try
            {
                // Break now (V Threads, the hotkey) first, whatever RhinoCode decided: during a
                // step every line would pause, and "stop once per line" may hold them all -- a
                // one-line loop after Step over would never take the request.
                if (Threads.TakeBreak(controls, frame))
                {
                    __result = true; // the next traced line, here
                    headFrame = frame;
                    t_reason = "Break now";
                    OncePerLine.Paused(frame, stack);
                    return;
                }
                var uri = frame.Reference?.Uri;
                var kind = (NextStepKind)s_stepKind.GetValue(__instance);
                var held = OncePerLine.Holds(frame, stack); // also ends a hold, so asked for every line
                if (!__result)
                    return;
                if (held && MiniVivisectionSettings.OncePerLine && OncePerLine.Enabled && OncePerLine.Applies(kind) &&
                    !(kind == NextStepKind.PauseOnNextBreakpoint && BreakpointSpecs.Find(uri, frame.Reference.Position.LineNumber) != null))
                {
                    __result = false; // Continue / Step over: the rest of the line paused at
                    return;
                }
                if (MiniVivisectionSettings.ConditionalBreakpoints && BreakpointSpecs.HasAny(uri) && kind == NextStepKind.PauseOnNextBreakpoint)
                {
                    var breakpoint = controls.Breakpoints.OfType<CodeReferenceBreakpoint>().FirstOrDefault(b => b.IsActive && b.Matches(frame));
                    var line = breakpoint?.Reference.Position.LineNumber ?? 0;
                    var spec = breakpoint == null ? null : BreakpointSpecs.Find(uri, line);
                    if (spec != null)
                        __result = Decide(spec, frame, uri, line, controls);
                }
                if (__result)
                    OncePerLine.Paused(frame, stack);
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.Breakpoints, "deciding a pause: {0}", Log.Describe(ex));
                __result = true;
            }
        }

        /// <summary>Pause or not, for a breakpoint hit at <paramref name="line"/> with a spec.</summary>
        internal static bool Decide(BreakpointSpec spec, ExecFrame frame, Uri uri, int line, IDebugControls controls)
        {
            var python = IsPython(uri);
            if (spec.Condition.Length > 0)
            {
                bool holds;
                try
                {
                    holds = python ? PythonBridge.ConditionHolds(spec.Condition) : CSharpConditions.IsTrue(spec.Condition, ConditionVariables(frame));
                }
                catch (Exception ex)
                {
                    var message = ScriptEditorHook.FirstLine(ex);
                    spec.LastError = message;
                    if (!spec.ErrorReported)
                    {
                        spec.ErrorReported = true;
                        UserLog.Warning("Breakpoint at line {0}: the condition '{1}' failed ({2}) -- paused.", line, spec.Condition, message);
                    }
                    t_reason = "the condition failed: " + message;
                    return true;
                }
                spec.LastError = null;
                Log.Debug(LogArea.Breakpoints, "line {0}: {1} -> {2}", line, spec.Condition, holds);
                if (!holds)
                    return false;
            }
            spec.Hits++;
            if (spec.HitCondition.Length > 0 && !HitMatches(spec.HitCondition, spec.Hits))
            {
                Log.Trace(LogArea.Breakpoints, "line {0}: hit {1}, waiting for {2}", line, spec.Hits, spec.HitCondition);
                return false;
            }
            if (spec.LogMessage.Length > 0)
            {
                string text;
                try
                {
                    text = python ? PythonBridge.Logpoint(spec.LogMessage, MiniVivisectionSettings.ThreadTags) : PrintCSharp(Format(spec.LogMessage, ConditionVariables(frame)), controls);
                }
                catch (Exception ex)
                {
                    text = "<" + ScriptEditorHook.FirstLine(ex) + ">";
                }
                UserLog.Info("Logpoint, line {0}: {1}", line, text);
                return false;
            }
            var reasons = new List<string>();
            if (spec.Condition.Length > 0)
                reasons.Add(spec.Condition);
            if (spec.HitCondition.Length > 0)
                reasons.Add("hit " + spec.Hits);
            t_reason = string.Join(", ", reasons);
            return true;
        }

        /// <summary>"5" or "== 5": the 5th hit; ">= 5", "> 5", "&lt; 5", "&lt;= 5"; "% 10":
        /// every 10th. Unreadable: every hit.</summary>
        internal static bool HitMatches(string hitCondition, int hits)
        {
            var match = Regex.Match(hitCondition.Trim(), @"^(==|>=|<=|>|<|%)?\s*(\d+)$");
            if (!match.Success)
                return true;
            var n = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            switch (match.Groups[1].Value)
            {
                case ">=":
                    return hits >= n;
                case ">":
                    return hits > n;
                case "<=":
                    return hits <= n;
                case "<":
                    return hits < n;
                case "%":
                    return n > 0 && hits % n == 0;
                default:
                    return hits == n;
            }
        }

        /// <summary>Null when <paramref name="hitCondition"/> is readable, else how to write it.</summary>
        internal static string HitSyntaxError(string hitCondition) =>
            hitCondition.Trim().Length == 0 || Regex.IsMatch(hitCondition.Trim(), @"^(==|>=|<=|>|<|%)?\s*\d+$")
                ? null
                : "a number (the n-th hit), or >= n, > n, <= n, < n, % n";

        /// <summary>Python 3 conditions run in the traced CPython frame; IronPython 2 and C#
        /// ones go to CSharpConditions over the frame's variables (it reads Python's
        /// and/or/not and True/False/None too).</summary>
        private static bool IsPython(Uri uri)
        {
            switch (ScriptKinds.Of(uri))
            {
                case ScriptKind.Python3:
                    return true;
                case ScriptKind.IronPython:
                case ScriptKind.CSharp:
                    return false;
                default:
                    return PythonBridge.IsRunning && PythonBridge.InTraceCallback();
            }
        }

        /// <summary>What a C# or IronPython 2 condition or logpoint can name: the frame's
        /// variables, and in a Grasshopper script component's run gh_run, gh_branch and gh_item
        /// (as Python 3 conditions have them) -- a variable of the script's own wins.</summary>
        internal static Dictionary<string, object> ConditionVariables(ExecFrame frame) =>
            WithGrasshopperRun(VariablesOf(frame), PythonBridge.GrasshopperRun?.Invoke());

        /// <summary><paramref name="variables"/> with gh_run, gh_branch and gh_item of
        /// <paramref name="run"/> added where the script has no such name.</summary>
        internal static Dictionary<string, object> WithGrasshopperRun(Dictionary<string, object> variables, (int Run, string Branch, string Item)? run)
        {
            if (run is (int number, string branch, string item))
            {
                variables.TryAdd("gh_run", number);
                variables.TryAdd("gh_branch", branch);
                variables.TryAdd("gh_item", item);
            }
            return variables;
        }

        /// <summary>The frame's variables by name (C#).</summary>
        internal static Dictionary<string, object> VariablesOf(ExecFrame frame)
        {
            var variables = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var variable in frame.Evaluate().Select(r => r.Result).OfType<ExecVariable>())
            {
                var name = variable.Id?.Identifier;
                if (name != null && !variables.ContainsKey(name))
                    variables[name] = variable.TryGetValue(out object value) ? value : null;
            }
            return variables;
        }

        /// <summary>A log message with its {expressions} filled in (C#); {{ and }} are braces.</summary>
        internal static string Format(string message, IReadOnlyDictionary<string, object> variables)
        {
            var text = new StringBuilder();
            foreach (Match part in Regex.Matches(message, @"\{\{|\}\}|\{[^{}]+\}|[^{}]+|[{}]"))
            {
                var p = part.Value;
                if (p == "{{")
                    text.Append('{');
                else if (p == "}}")
                    text.Append('}');
                else if (p.Length > 2 && p[0] == '{' && p[p.Length - 1] == '}')
                {
                    try
                    {
                        var value = CSharpConditions.Evaluate(p.Substring(1, p.Length - 2), variables);
                        text.Append(value == null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture));
                    }
                    catch (Exception ex)
                    {
                        text.Append('<').Append(ScriptEditorHook.FirstLine(ex)).Append('>');
                    }
                }
                else
                    text.Append(p);
            }
            return text.ToString();
        }

        /// <summary>Where a C# logpoint's line goes: the editor's Terminal (set by the plug-in at load).</summary>
        internal static Action<IDebugControls, string> TerminalWriter = (controls, text) => { };

        private static string PrintCSharp(string text, IDebugControls controls)
        {
            TerminalWriter(controls, text);
            return text;
        }
    }
}
