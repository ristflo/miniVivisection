using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rhino.Runtime.Code.Execution;

namespace MiniVivisection
{
    /// <summary>StoppedRuns' data: where a RunPythonScript run ended (Break now, an error),
    /// as recorded in last_stop.json, and the code view's overlay for it -- apart from the
    /// editor and the run, for the checks.</summary>
    internal static partial class StoppedRuns
    {
        public sealed class StackLine
        {
            public string File { get; set; }
            public int Line { get; set; }
            public string Function { get; set; }
        }

        /// <summary>Where a running script was (PythonBridge.StopScript).</summary>
        public sealed class Where
        {
            public long Ident { get; set; } // the thread, as Python names it
            public string File { get; set; }
            public int Line { get; set; }
            public string Function { get; set; }
            public List<StackLine> Stack { get; set; } = new List<StackLine>();
            public List<string[]> Values { get; set; } = new List<string[]>();
        }

        /// <summary>What the Script Editor shows: last_stop.json.</summary>
        public sealed class Record
        {
            public string Kind { get; set; } // "stopped" (Break now) or "error"
            public DateTime Time { get; set; }
            public string File { get; set; }
            public string Title { get; set; }
            public int Line { get; set; }
            public string Function { get; set; }
            public string Error { get; set; }
            public string Traceback { get; set; }
            public List<StackLine> Stack { get; set; } = new List<StackLine>();
            public List<string[]> Values { get; set; } = new List<string[]>();
            public bool Shown { get; set; }

            // A Grasshopper script component's run (GrasshopperRuns); null for RunPythonScript.
            public string Component { get; set; }   // its nickname
            public string ComponentId { get; set; } // its InstanceGuid
            public int RunNumber { get; set; }      // GH_Component.RunCount: 1, 2, ...
            public string Branch { get; set; }      // the run's main branch: {0;3}
            public string Item { get; set; }        // and item: {0;3}[2] (item access)
            public string Inputs { get; set; }      // every input's path: "x {0;3}[2], y {0}"
            public bool BreakpointSet { get; set; } // a breakpoint was set for the next Debug run (Debug failing run N)
            public string Condition { get; set; }   // its condition: gh_item == "{0;3}[2]", or gh_run == 4
            public bool InputScript { get; set; }   // the component ran the script of its input (a Panel's text, a file): no debugger for it yet
        }

        internal sealed class Run
        {
            public string Path;
            public string Title;
            public long Ident;          // its thread (Python's ident) when known: the UI thread's; 0 for an async run
            public Where Stopped;       // under s_lock
            public volatile bool Stopping;
        }

        /// <summary>One press of Break now's hotkey: given up after its wait, it does nothing
        /// when Python's lock comes later.</summary>
        internal sealed class StopAttempt
        {
            public volatile bool Abandoned;
        }

        internal static string RecordPath => System.IO.Path.Combine(Log.Folder, "last_stop.json");

        internal static Record FromStop(Run run, Where where) => new Record
        {
            Kind = "stopped",
            Time = DateTime.Now,
            File = ToPath(where.File) ?? run.Path,
            Title = run.Title,
            Line = where.Line,
            Function = where.Function,
            Stack = where.Stack.Select(f => new StackLine { File = ToPath(f.File), Line = f.Line, Function = f.Function }).ToList(),
            Values = where.Values,
        };

        internal static Record FromError(Run run, Exception error)
        {
            var execute = error as ExecuteException ?? error.InnerException as ExecuteException;
            var traceback = (execute?.StackTrace ?? error.StackTrace ?? "").Trim();
            var record = new Record
            {
                Kind = "error",
                Time = DateTime.Now,
                File = run.Path,
                Title = run.Title,
                Error = FirstLine(error.Message),
                Traceback = traceback,
            };
            // The script's own innermost line in the traceback (an error inside a library
            // call shows where the script called it); else the exception's position.
            var lines = Regex.Matches(traceback, "File \"(?<file>[^\"]+)\", line (?<line>\\d+)(?:, in (?<function>\\S+))?")
                .Cast<Match>()
                .Where(m => run.Path == null || SamePath(m.Groups["file"].Value, run.Path))
                .ToList();
            if (lines.Count > 0)
            {
                var last = lines[lines.Count - 1];
                record.Line = int.Parse(last.Groups["line"].Value);
                record.Function = last.Groups["function"].Success ? last.Groups["function"].Value : null;
                record.Stack = lines.AsEnumerable().Reverse().Select(m => new StackLine
                {
                    File = ToPath(m.Groups["file"].Value),
                    Line = int.Parse(m.Groups["line"].Value),
                    Function = m.Groups["function"].Value,
                }).ToList();
            }
            else if (execute != null && execute.Position.LineNumber > 0)
            {
                record.Line = execute.Position.LineNumber;
            }
            return record;
        }

        private static string FirstLine(string text) => (text ?? "").Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";

        /// <summary>A file path: RhinoCode gives a script's code the file's URI as its
        /// filename (file:///C:/...), which Python then reports.</summary>
        internal static string ToPath(string file)
        {
            if (string.IsNullOrEmpty(file))
                return file;
            return file.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(file, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : file;
        }

        private static bool SamePath(string a, string b)
        {
            a = ToPath(a);
            b = ToPath(b);
            try
            {
                return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        // --- the record ------------------------------------------------------------------------

        private static readonly JsonSerializerOptions s_json = new JsonSerializerOptions { WriteIndented = true };

        internal static void Save(Record record)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RecordPath));
            // Whole or not at all: another Rhino may read it meanwhile.
            var temporary = RecordPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record, s_json));
            File.Move(temporary, RecordPath, overwrite: true);
        }

        internal static Record Load()
        {
            try
            {
                return File.Exists(RecordPath) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(RecordPath)) : null;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Runs, "last_stop.json: {0}", Log.Describe(ex));
                return null;
            }
        }

        /// <summary>What a script Panel shows when Ctrl+Alt+B stopped the component running its
        /// script (miniVivisection.GH's CodeBox and ScriptPanels): a title, and lines saying what
        /// happened, with the run's data and values then, and what to do -- the Script Editor's
        /// overlay (<see cref="OverlayScript"/>) for a Panel, which has no Debug.</summary>
        internal static (string Title, string[] Lines) PanelOverlay(Record record)
        {
            var hotkey = MiniVivisectionSettings.BreakHotkey == "Off" ? "Break now" : MiniVivisectionSettings.BreakHotkey;
            var inFunction = record.Function != null && record.Function != "<module>" ? " in " + record.Function + "()" : "";
            var run = record.RunNumber > 0 ? ", run " + record.RunNumber : "";
            var title = "Stopped in '" + record.Component + "' at line " + record.Line + inFunction + run + " (" + hotkey + ")";
            var lines = new List<string>
            {
                "Grasshopper ran this Panel's script without the debugger, so " + hotkey + " ended its solution at this line, at " + record.Time.ToString("HH:mm") + ".",
            };
            if (record.Branch != null || record.Inputs != null)
                lines.Add("The run's data: " + (record.Item ?? record.Branch) + (record.Inputs != null ? " -- inputs: " + record.Inputs : "") + ".");
            var values = ShownValues(record);
            if (values.Count > 0)
                lines.Add("Its values then: " + string.Join(", ", values.Take(8).Select(v => v[0] + " = " + v[1])) + (values.Count > 8 ? ", ..." : ""));
            if (record.Stack.Count > 1)
                lines.Add("Call stack then (innermost first): " + string.Join(" <- ", record.Stack.Take(8).Select(f =>
                    (f.Function == "<module>" ? "the script" : f.Function + "()") + " line " + f.Line)) + (record.Stack.Count > 8 ? " <- ..." : ""));
            lines.Add("Fix it here and click elsewhere: the Panel keeps it, and the component recomputes. Esc drops your changes.");
            if (MiniVivisectionSettings.GrasshopperAlwaysDebug)
                lines.Add(AlwaysDebugNote(hotkey));
            return (VNames.Text(title), lines.Select(VNames.Text).ToArray());
        }

        // Names Grasshopper and the console put in every script's scope: not the script's own values.
        private static readonly HashSet<string> s_scopeNames = new HashSet<string>(StringComparer.Ordinal) { "_", "ghdoc", "ghenv", "scriptcontext", "__builtins__" };

        /// <summary>The record's values without Grasshopper's own names (ghdoc, ghenv, ...).</summary>
        private static List<string[]> ShownValues(Record record) =>
            record.Values.Where(v => v != null && v.Length >= 2 && !s_scopeNames.Contains(v[0])).ToList();

        /// <summary>Always debug is on, but a script from a component's input (a Panel, a file)
        /// can't run in the debugger yet: why Ctrl+Alt+B stopped it rather than paused it.</summary>
        private static string AlwaysDebugNote(string hotkey) =>
            "Always debug is on, but a script from a Panel or a file can't run in the debugger yet: it runs plainly, so " + hotkey +
            " stops it instead of pausing it, and its breakpoints don't pause.";

        /// <summary>The code view's part: the line in view, marked, and the overlay (top right)
        /// explaining it -- built from text nodes, so nothing in the record is read as HTML.</summary>
        internal static string OverlayScript(Record record)
        {
            var hotkey = MiniVivisectionSettings.BreakHotkey == "Off" ? "Break now" : MiniVivisectionSettings.BreakHotkey;
            var inFunction = record.Function != null && record.Function != "<module>" ? " in " + record.Function + "()" : "";
            string title, why;
            if (record.Kind == "stopped")
            {
                title = "Stopped at line " + record.Line + inFunction + " (" + hotkey + ")";
                why = "RunPythonScript ran this script without the debugger, so " + hotkey + " couldn't pause it: it ended the script at this line, at " +
                      record.Time.ToString("HH:mm") + ".";
            }
            else
            {
                title = "Error at line " + record.Line + inFunction;
                why = "The script ended with this error when RunPythonScript ran it (" + record.Time.ToString("HH:mm") + "): " + record.Error;
            }
            if (record.Component != null)
            {
                var run = record.RunNumber > 0 ? ", run " + record.RunNumber : "";
                if (record.Kind == "stopped")
                {
                    title = "Stopped in '" + record.Component + "' at line " + record.Line + inFunction + run + " (" + hotkey + ")";
                    why = record.InputScript
                        ? "Grasshopper ran this script -- the script input of '" + record.Component + "' -- without the debugger, so " + hotkey +
                          " ended its solution at this line, at " + record.Time.ToString("HH:mm") + "."
                        : "Grasshopper ran this component without the debugger (its editor was closed), so " + hotkey +
                          " ended its solution at this line, at " + record.Time.ToString("HH:mm") + ".";
                }
                else
                {
                    title = "Error in '" + record.Component + "' at line " + record.Line + inFunction + run;
                    why = "This run failed (" + record.Time.ToString("HH:mm") + "): " + record.Error;
                }
            }
            var lines = new List<string> { why };
            if (record.Component != null && (record.Branch != null || record.Inputs != null))
                lines.Add("The run's data: " + (record.Item ?? record.Branch) + (record.Inputs != null ? " -- inputs: " + record.Inputs : "") +
                          (record.Kind == "stopped" ? "." : ". Right-click > " + VNames.Menu("Copy failing items") + ", then feed Tree Branch (or V Edit's Index)."));
            var values = ShownValues(record);
            if (values.Count > 0)
                lines.Add("Its values then: " + string.Join(", ", values.Take(8).Select(v => v[0] + " = " + v[1])) + (values.Count > 8 ? ", ..." : ""));
            if (record.Stack.Count > 1)
                lines.Add("Call stack then (innermost first): " + string.Join(" <- ", record.Stack.Take(8).Select(f =>
                    (f.Function == "<module>" ? "the script" : f.Function + "()") + " line " + f.Line +
                    (SamePath(f.File ?? "", record.File) ? "" : " of " + System.IO.Path.GetFileName(f.File ?? "")))) +
                    (record.Stack.Count > 8 ? " <- ..." : "") +
                    ". The editor's Call Stack tab fills only while the debugger is paused -- in the Debug run below.");
            if (record.Component != null && record.InputScript)
            {
                lines.Add("Fix it here and save: the component recomputes. (Debug here would run this file on its own, without the component's inputs.)");
                if (MiniVivisectionSettings.GrasshopperAlwaysDebug)
                    lines.Add(AlwaysDebugNote(hotkey));
            }
            else if (record.Component != null && record.BreakpointSet)
                lines.Add("A breakpoint is set on this line" + (record.Condition != null ? " for this run only (condition: " + record.Condition + ")" : "") +
                          ". The component recomputes in the debugger and pauses here -- hover a name, V Watch, V Python, F10. It's removed after this debug run.");
            else if (record.Component != null)
                lines.Add("Press Debug (the green arrow with the bug) to recompute it in the debugger: " + hotkey + " then pauses it instead of stopping it. " +
                          "To stop at this line, click its margin for a breakpoint" +
                          (record.Item != null ? " -- with the condition gh_item == \"" + record.Item + "\" (right-click the line) for this data only, or gh_run == " + record.RunNumber + " for this run number"
                           : record.RunNumber > 0 ? " -- with the condition gh_run == " + record.RunNumber + " (right-click the line) for this run only" : "") + ".");
            else
                lines.Add("Run it again here with Debug (the green arrow with the bug): " + hotkey + " then pauses it instead of stopping it, and you can look at every value -- " +
                          "hover a name, V Watch, V Python -- and step on with F10. To stop at this line, click its margin for a breakpoint" +
                          (record.Kind == "error" ? " (right-click it for a condition: only in the round that fails)." : "."));
            var data = JsonSerializer.Serialize(new { line = record.Line, title = VNames.Text(title), lines = lines.Select(VNames.Text).ToList(), error = record.Kind == "error" });
            return @"try {
" + CodeViewScripts.FindEditor + @"
var d = " + data + @";
var line = d.line;
try { editor.revealLineInCenter(line); editor.setPosition({ lineNumber: line, column: 1 }); } catch (e) {}
var dark = !!(editor.getDomNode() && editor.getDomNode().closest('.vs-dark, .hc-black'));
if (!document.getElementById('mv-stop-style')) {
  var style = document.createElement('style');
  style.id = 'mv-stop-style';
  style.textContent = '.mv-stop-line { background: rgba(255, 170, 0, 0.25); } .mv-stop-error { background: rgba(255, 60, 60, 0.22); }';
  document.head.appendChild(style);
}
globalThis.__mvStopDeco = editor.deltaDecorations(globalThis.__mvStopDeco || [],
  [{ range: new monaco.Range(line, 1, line, 1), options: { isWholeLine: true, className: d.error ? 'mv-stop-error' : 'mv-stop-line' } }]);
if (globalThis.__mvStopWidget) { try { editor.removeOverlayWidget(globalThis.__mvStopWidget); } catch (e) {} }
var node = document.createElement('div');
node.style.cssText = 'max-width: 440px; padding: 10px 12px; margin: 8px 18px 0 0; border-radius: 6px; font: 12px Segoe UI, sans-serif; line-height: 1.45;' +
  'box-shadow: 0 2px 10px rgba(0,0,0,0.35); border-left: 4px solid ' + (d.error ? '#e5484d' : '#f5a524') + ';' +
  (dark ? 'background: #2b2b2b; color: #e6e6e6;' : 'background: #fffdf5; color: #222;');
var head = document.createElement('div');
head.style.cssText = 'display: flex; align-items: center; gap: 8px; font-weight: 600; margin-bottom: 6px;';
var titleNode = document.createElement('span');
titleNode.textContent = 'miniVivisection: ' + d.title;
titleNode.style.flex = '1';
var close = document.createElement('span');
close.textContent = '✕';
close.title = 'Close';
close.style.cssText = 'cursor: pointer; opacity: 0.7; padding: 0 2px;';
head.appendChild(titleNode);
head.appendChild(close);
node.appendChild(head);
d.lines.forEach(function (text) {
  var p = document.createElement('div');
  p.textContent = text;
  p.style.marginTop = '4px';
  node.appendChild(p);
});
var widget = {
  getId: function () { return 'minivivisection.stop'; },
  getDomNode: function () { return node; },
  getPosition: function () { return { preference: monaco.editor.OverlayWidgetPositionPreference.TOP_RIGHT_CORNER }; }
};
close.onclick = function () {
  try { editor.removeOverlayWidget(widget); } catch (e) {}
  globalThis.__mvStopDeco = editor.deltaDecorations(globalThis.__mvStopDeco || [], []);
  globalThis.__mvStopWidget = null;
};
editor.addOverlayWidget(widget);
globalThis.__mvStopWidget = widget;
return 'shown at line ' + line;
} catch (e) { return 'error: ' + e; }
";
        }
    }
}
