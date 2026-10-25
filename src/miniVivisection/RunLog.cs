using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MiniVivisection
{
    /// <summary>
    /// What a Script Editor's recent runs left behind, kept after they ended -- for
    /// Claude (get_run_errors). The editor shows a debug run's exception only as a
    /// break in the code view, cleared when the debugger ends, and the next run clears
    /// the Terminal. Kept: the exceptions the debugger stopped at (type, message, line,
    /// traceback) and the Terminal output at the end of each run, for the last runs.
    /// </summary>
    internal sealed class RunLog
    {
        private const int MaxRuns = 5;
        private const int MaxOutput = 20000;

        private sealed class Run
        {
            public DateTime Started = DateTime.Now;
            public DateTime? Ended;
            public string Script = "";
            public readonly List<string> Exceptions = new List<string>();
            public string Output = "";
        }

        private readonly object _lock = new object();
        private readonly List<Run> _runs = new List<Run>();

        /// <summary>A run starts (any thread).</summary>
        public void Begin(string script)
        {
            lock (_lock)
            {
                _runs.Add(new Run { Script = script ?? "" });
                while (_runs.Count > MaxRuns)
                    _runs.RemoveAt(0);
            }
        }

        /// <summary>The debugger stopped at an exception.</summary>
        public void AddException(string text)
        {
            lock (_lock)
            {
                var run = Current();
                if (!run.Exceptions.Contains(text))
                    run.Exceptions.Add(text);
            }
        }

        /// <summary>The run ended: its Terminal output as it is now.</summary>
        public void End(string output)
        {
            lock (_lock)
            {
                var run = Current();
                if (run.Ended.HasValue && run.Output.Length > 0 && string.IsNullOrEmpty(output))
                    return; // ended twice (debugger and editor): keep what was seen
                run.Ended = DateTime.Now;
                output = output ?? "";
                run.Output = output.Length <= MaxOutput ? output : "...\n" + output.Substring(output.Length - MaxOutput);
            }
        }

        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                    return _runs.Count == 0;
            }
        }

        /// <summary>The last <paramref name="count"/> runs, newest first, for Claude.</summary>
        public string Describe(int count)
        {
            lock (_lock)
            {
                if (_runs.Count == 0)
                    return "No runs recorded since the Script Editor was found (runs are recorded from then on).";
                var text = new StringBuilder();
                var runs = _runs.AsEnumerable().Reverse().Take(Math.Max(1, count)).ToList();
                for (var i = 0; i < runs.Count; i++)
                {
                    var run = runs[i];
                    text.AppendFormat("Run {0} ({1}): {2} started {3:HH:mm:ss}, {4}\n", i + 1, i == 0 ? "latest" : "earlier",
                        run.Script.Length > 0 ? run.Script : "the script", run.Started,
                        run.Ended.HasValue ? string.Format("ended {0:HH:mm:ss}", run.Ended.Value) : "still running");
                    if (run.Exceptions.Count == 0)
                        text.Append("  The debugger stopped at no exception.\n");
                    foreach (var exception in run.Exceptions)
                        text.Append("  Exception the debugger stopped at:\n").Append(Indent(exception, "    ")).Append('\n');
                    text.Append(run.Output.Trim().Length == 0
                        ? "  Terminal output: (none)\n"
                        : "  Terminal output at the end of the run:\n" + Indent(run.Output.TrimEnd(), "    ") + "\n");
                    text.Append('\n');
                }
                return text.ToString().TrimEnd() + "\n";
            }
        }

        private Run Current()
        {
            if (_runs.Count == 0)
                _runs.Add(new Run()); // a run that started before the editor was found
            return _runs[_runs.Count - 1];
        }

        private static string Indent(string text, string prefix) =>
            string.Join("\n", (text ?? "").Replace("\r\n", "\n").Split('\n').Select(line => prefix + line));
    }
}
