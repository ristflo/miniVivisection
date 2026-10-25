// Threads of a debugged script (Threads): Break now through the real NextStep.ShouldPause
// (BreakpointConditions' postfix), in any thread or one; the freeze guard without a UI
// heartbeat; the snapshot's rows. And the lessons in the editor's Examples tab
// (EditorExamples) with a real SourceLibrary.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        private static ExecFrame LineIn(long pythonThread, int managedThread, Uri uri, int line) =>
            ExecFrame.OnLine(ContextIdentity.Empty, new ExecThreadIdNative(pythonThread, managedThread), new ExecFrameId("<module>"),
                new CodeReference(uri, line), new ExecVariable[0]);

        private static bool Pauses(DebugControls controls, ExecFrame frame) =>
            NextStep.PauseOnNextBreakpoint().ShouldPause(controls, frame, null, out _);

        private static void ThreadChecks()
        {
            BreakpointConditions.TryInstall();
            var uri = new Uri("file:///C:/checks/threads.py");
            var controls = new StandInControls();
            var other = new StandInControls();
            Threads.ResetRun();

            Check("no breakpoint: runs on", Pauses(controls, LineIn(11, 3, uri, 5)), false);
            Threads.RequestBreak(controls, 0);
            Check("break requested", Threads.BreakRequested, true);
            Check("Break now: another debugger's script runs on", Pauses(other, LineIn(11, 3, uri, 5)), false);
            Check("Break now: the next line pauses", Pauses(controls, LineIn(11, 3, uri, 5)), true);
            Check("Break now: taken once", Pauses(controls, LineIn(11, 3, uri, 6)), false);

            Threads.RequestBreak(controls, 22);
            Check("Break in thread: another thread runs on", Pauses(controls, LineIn(11, 3, uri, 7)), false);
            Check("Break in thread: that thread pauses", Pauses(controls, LineIn(22, 4, uri, 7)), true);
            Threads.RequestBreak(controls, 0);
            Threads.ResetRun();
            Check("a new run drops the request", Threads.BreakRequested, false);
            Check("thread count unknown (no Python 3 pause): 1", Threads.Count(controls), 1);
            Threads.ForgetPause(controls);
            Check("no paused thread name after a non-Python 3 pause", Threads.PausedLabel(controls), null);

            Check("async: the lesson's header", ScriptKinds.AsksForAsync("#! python 3\n# async:true\n\"\"\"doc\"\"\""), true);
            Check("async: C# comment, false", ScriptKinds.AsksForAsync("// async: false\r\nusing System;"), false);
            Check("async: not said", ScriptKinds.AsksForAsync("#! python 3\nimport threading\n"), null);
            Check("async: needs a space after # (RhinoCode's rule)", ScriptKinds.AsksForAsync("#async:true\n"), null);
            Check("async: only the first 32 lines", ScriptKinds.AsksForAsync(new string('\n', 40) + "# async:true\n"), null);
            Check("freeze guard: no heartbeat, no verdict", Threads.UiBlocked(TimeSpan.FromMilliseconds(10)), false);
            Check("pause line: one thread", Threads.Describe(("main", 1)), "");
            Check("pause line: async script alone", Threads.Describe(("script", 1)), "");
            Check("pause line: a worker", Threads.Describe(("Worker-2", 3)), " in thread Worker-2 (3 threads)");

            var row = new ThreadRow(5, "Worker-1", false, false, "waiting in wait()", new List<ThreadFrame>
            {
                new ThreadFrame(@"C:\Python\Lib\threading.py", 300, "wait", own: false),
                new ThreadFrame(@"C:\scripts\job.py", 12, "work", own: true),
            });
            Check("a thread's location: its frame of the script", row.Location.Where, "work -- job.py:12");
            Check("waiting", row.IsWaiting, true);
        }

        private static void ExampleChecks()
        {
            var folder = Path.Combine(Path.GetTempPath(), "minivivisection-example-checks");
            Directory.CreateDirectory(folder);
            foreach (var name in new[] { "02_second.py", "01_first.py", "09_lesson_tools.py", "notes.py" })
                File.WriteAllText(Path.Combine(folder, name), "#! python 3\nx = 1\n");
            Check("lessons: numbered, in order, no helpers", string.Join(",", EditorExamples.Lessons(folder).Select(Path.GetFileName)), "01_first.py,02_second.py");

            var library = new SourceLibrary(new Uri("rhinocode://mcneel.rhino3d.rhino/examples"), isReadOnly: true)
            {
                new SourceCode(Rhino.Runtime.Code.Languages.LanguageSpec.Python3, "print(1)", new Uri("rhinocode://mcneel.rhino3d.rhino/Python 3/Example 1.py")),
            };
            Check("added", EditorExamples.Apply(library, folder, on: true), 2);
            Check("added once", EditorExamples.Apply(library, folder, on: true), 2);
            var first = library.OfType<EditorExamples.ExampleFile>().First(e => e.Path.EndsWith("01_first.py"));
            Check("in the miniVivisection folder, beside McNeel's", first.Uri.ToString(), "rhinocode://mcneel.rhino3d.rhino/miniVivisection/01_first.py");
            Check("its text, for the tab's preview", first.Text.ToString().StartsWith("#! python 3"), true);
            Check("McNeel's example kept", library.Count(), 3);
            Check("removed when off", EditorExamples.Apply(library, folder, on: false), 0);
            Check("only ours removed", library.Count(), 1);
        }
    }
}
