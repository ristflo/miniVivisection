// RunPythonScript, Stop and record: what a stop or an error records, and the code view's
// overlay for it (StoppedRuns). The run itself and the editor are checked in Rhino.
using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Runtime.Code.Execution;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        private static void StoppedRunChecks()
        {
            var run = new StoppedRuns.Run { Path = @"C:\\work\\main.py", Title = "main.py" };
            var traceback = "Traceback (most recent call last):\n" +
                "  File \"C:\\work\\main.py\", line 30, in <module>\n    run()\n" +
                "  File \"C:\\work\\main.py\", line 12, in run\n    x = helper(1)\n" +
                "  File \"C:\\work\\helpers.py\", line 4, in helper\n    raise ValueError('bad')\n";
            var error = StoppedRuns.FromError(run, new ExecuteException("ValueError: bad\nmore", traceback));
            Check("error: the script's own last line", error.Line, 12);
            Check("error: its function", error.Function, "run");
            Check("error: the message's first line", error.Error, "ValueError: bad");
            Check("error: the script's callers", string.Join(",", error.Stack.Select(f => f.Line)), "12,30");

            var where = new StoppedRuns.Where { File = "file:///C:/work/main.py", Line = 7, Function = "work" }; // as Python reports it
            where.Stack.Add(new StoppedRuns.StackLine { File = run.Path, Line = 7, Function = "work" });
            where.Stack.Add(new StoppedRuns.StackLine { File = run.Path, Line = 20, Function = "<module>" });
            where.Values.Add(new[] { "i", "41" });
            where.Values.Add(new[] { "label", "'</script><b>x'" });
            var stopped = StoppedRuns.FromStop(run, where);
            Check("stop: kind and line", stopped.Kind + " " + stopped.Line, "stopped 7");
            Check("stop: the file as a path", stopped.File, @"C:\work\main.py");
            var script = StoppedRuns.OverlayScript(stopped);
            // As the code view runs it (HoverPreview.Script): a function body. For a syntax check (node --check).
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minivivisection-overlay.js"), "(function () {" + script + "\n})();\n");
            Check("overlay: title", script.Contains("Stopped at line 7 in work()"), true);
            Check("overlay: the values", script.Contains("i = 41"), true);
            Check("overlay: the call stack", script.Contains("work() line 7 \\u003C- the script line 20"), true);
            Check("overlay: values never as HTML", script.Contains("</script>") || script.Contains("<b>"), false);
            // A Grasshopper component's run: its branch, item and the run-only breakpoint.
            var component = new StoppedRuns.Record
            {
                Kind = "error", Time = DateTime.Now, Title = "Py3", Line = 12, Error = "ZeroDivisionError: division by zero",
                Component = "Py3", RunNumber = 4, Branch = "{0;3}", Item = "{0;3}[2]", Inputs = "x {0;3}[2], y {0}",
            };
            var ghOverlay = StoppedRuns.OverlayScript(component);
            Check("gh overlay: component, line, run", ghOverlay.Contains("Py3") && ghOverlay.Contains(" at line 12, run 4"), true);
            Check("gh overlay: the run's data", ghOverlay.Contains("{0;3}[2] -- inputs: x {0;3}[2], y {0}"), true);
            Check("gh overlay: by data or run number", ghOverlay.Contains("gh_item") && ghOverlay.Contains("gh_run == 4"), true);
            // The version's time zone: its usual abbreviation, else the offset.
            var riyadh = TimeZoneInfo.FindSystemTimeZoneById("Arab Standard Time");
            Check("zone: AST", VersionInfo.ZoneName(riyadh, new DateTime(2026, 10, 9, 14, 5, 0)), "AST");
            var berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
            Check("zone: CEST in summer", VersionInfo.ZoneName(berlin, new DateTime(2026, 7, 1, 12, 0, 0)), "CEST");
            Check("zone: CET in winter", VersionInfo.ZoneName(berlin, new DateTime(2026, 1, 1, 12, 0, 0)), "CET");
            var odd = TimeZoneInfo.CreateCustomTimeZone("Checks Zone", TimeSpan.FromHours(5.5), "Checks Zone", "Checks Zone");
            Check("zone: else the offset", VersionInfo.ZoneName(odd, new DateTime(2026, 1, 1)), "UTC+5:30");
            Check("overlay: an error's tip", StoppedRuns.OverlayScript(error).Contains("right-click it for a condition"), true);
        }
    }
}
