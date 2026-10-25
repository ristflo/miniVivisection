// Stepping across files: RhinoCode's own step logic (DebugControls.Trace with its
// DebugThreadEventStack), fed with the events of a real Python run the way RhinoCode's
// CPythonTracer does it. A function in module B ending in `return modc.get(x)` (module C)
// left frames on RhinoCode's event stack, so "step over" in the script never paused
// again -- the rest ran in one go. StepTracking fixes that.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Python.Runtime;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        /// <summary>A debugger that answers every pause with one action and records where it paused.</summary>
        private sealed class SteppingControls : DebugControls
        {
            public readonly List<string> Pauses = new List<string>();
            public readonly Queue<DebugAction> Actions = new Queue<DebugAction>(); // then Fallback
            public DebugAction Fallback = DebugAction.StepOver;

            public override DebugAction Pause()
            {
                var frame = Results?.CurrentThread?.CurrentFrame;
                Pauses.Add(Path.GetFileNameWithoutExtension(frame?.Reference.Uri.LocalPath ?? "?") + ":" + frame?.Reference.Position.LineNumber);
                return Actions.Count > 0 ? Actions.Dequeue() : Fallback;
            }

            public override void Proceed(DebugAction action)
            {
            }
        }

        private const string ModuleC = "def get(x):\n    return x * 2\n";

        private const string ModuleB = @"import step_modc

def f(x):
    return step_modc.get(x)

def g(x):
    try:
        raise ValueError('inside')
    except ValueError:
        pass
    return step_modc.get(x) + 1
";

        private const string Script = @"import step_modb

def main():
    a = 1
    b = step_modb.f(a)
    c = b + 1
    d = step_modb.g(c)
    e = d + 1
    return e

main()
";

        /// <summary>Where the debugger pauses, from a breakpoint on main()'s first line:
        /// <paramref name="actions"/> first, then "step over" at every pause. Script lines
        /// as numbers, module lines as "b:4".</summary>
        private static string StepPauses(params DebugAction[] actions) =>
            TracePauses(Script, new[] { 4 }, DebugAction.StepOver, actions); // a = 1

        /// <summary>Where the debugger pauses in <paramref name="script"/> (with modules B and
        /// C next to it), from <paramref name="breakpoints"/>: <paramref name="actions"/> first,
        /// then <paramref name="fallback"/> at every pause.</summary>
        private static string TracePauses(string script, int[] breakpoints, DebugAction fallback, params DebugAction[] actions) =>
            TracePauses(script, breakpoints, fallback, null, actions);

        /// <summary>As above; <paramref name="onLine"/> sees every line event first (its count from 1).</summary>
        private static string TracePauses(string script, int[] breakpoints, DebugAction fallback, Action<DebugControls, int> onLine, params DebugAction[] actions)
        {
            var folder = Path.Combine(Path.GetTempPath(), "minivivisection-step-checks");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "step_modb.py"), ModuleB);
            File.WriteAllText(Path.Combine(folder, "step_modc.py"), ModuleC);
            var scriptPath = Path.Combine(folder, "step_script.py");
            File.WriteAllText(scriptPath, script);

            var controls = new SteppingControls { Fallback = fallback };
            foreach (var action in actions)
                controls.Actions.Enqueue(action);
            var context = new ContextIdentity("step checks " + Guid.NewGuid());
            var lines = 0;
            foreach (var line in breakpoints)
                controls.Breakpoints.Add(new CodeReferenceBreakpoint(new Uri(scriptPath), line));
            controls.Attach(Guid.NewGuid());
            // RhinoCode's CPythonTracer: one frame per event, its id from the function's name
            // and arguments (trace_begin.py's __pynet_traceframeid__).
            Action<string, int, string, string> trace = (file, line, kind, frameId) =>
            {
                var reference = new CodeReference(new Uri(file), line);
                var id = new ExecFrameId(frameId);
                var none = Array.Empty<ExecVariable>();
                ExecFrame frame;
                switch (kind)
                {
                    case "call":
                        frame = ExecFrame.OnCall(context, ExecThreadId.Empty, id, reference, none);
                        break;
                    case "line":
                        onLine?.Invoke(controls, ++lines);
                        frame = ExecFrame.OnLine(context, ExecThreadId.Empty, id, reference, none);
                        break;
                    case "return":
                        frame = ExecFrame.OnReturn(context, ExecThreadId.Empty, id, reference, none);
                        break;
                    default:
                        frame = ExecFrame.OnException(context, ExecThreadId.Empty, id, reference, "ValueError", "inside", "");
                        break;
                }
                controls.Trace(frame);
            };
            using (Py.GIL())
            {
                var scope = Py.CreateScope();
                scope.Set("trace", trace.ToPython());
                scope.Set("folder", folder.ToPython());
                scope.Set("script_path", scriptPath.ToPython());
                scope.Exec(@"
import sys, os
sys.path.insert(0, folder)
for name in ('step_modb', 'step_modc'):
    sys.modules.pop(name, None)

def frame_id(frame):
    code = frame.f_code
    if code.co_name == '<module>':
        return '__main__'
    count = code.co_argcount + code.co_kwonlyargcount
    return code.co_name + '(' + ', '.join(code.co_varnames[:count]) + ')'

def tracefunc(frame, event, arg):
    if event in ('call', 'line', 'return', 'exception') and os.path.dirname(frame.f_code.co_filename) == folder:
        trace(frame.f_code.co_filename, frame.f_lineno, event, frame_id(frame))
    return tracefunc

code = compile(open(script_path).read(), script_path, 'exec')
sys.settrace(tracefunc)
try:
    exec(code, {'__name__': '__main__'})
finally:
    sys.settrace(None)
    sys.path.remove(folder)
");
            }
            return string.Join(" ", controls.Pauses.Select(p => p.StartsWith("step_script:") ? p.Substring("step_script:".Length) : p.Replace("step_mod", "")));
        }

        private static void StepChecks()
        {
            // Without the fix (as RhinoCode is), for the record.
            StepTracking.Enabled = false;
            var without = StepPauses();
            StepTracking.TryInstall();
            StepTracking.Enabled = true;
            var with = StepPauses();
            Console.WriteLine("    step over without the fix: " + without + "; with it: " + with);
            Check("RhinoCode alone loses the stepping after `return other_module.f()`", without.StartsWith("4 5 6 7 8"), false);
            Check("step over pauses on every line of main()", with.StartsWith("4 5 6 7 8 9"), true);
            var inOut = StepPauses(DebugAction.StepOver, DebugAction.StepIn, DebugAction.StepOut);
            Console.WriteLine("    step in at line 5, then out: " + inOut);
            Check("step in goes into module B", inOut.StartsWith("4 5 b:4"), true);
            Check("step out comes back to main() and stepping goes on", inOut.Split(' ').Skip(3).Take(3).All(p => !p.Contains(":")) && inOut.Contains(" 7 ") && inOut.Contains(" 8"), true);
        }

        private const string LoopScript = @"def main():
    rungs = [1, 2, 3]
    y = [r * 2 for r in rungs]
    s = 0
    for r in rungs: s += r
    t = sum(r for r in rungs)
    return s + t

main()
";

        private const string OneLineLoop = @"def main():
    s = 0
    for r in range(200): s += r
    return s

main()
";

        // A breakpoint on a line that runs code several times (a comprehension, a one-line
        // loop): Continue and Step over finish the line (OncePerLine).
        private static void OncePerLineChecks()
        {
            BreakpointConditions.TryInstall();
            try
            {
                OncePerLine.Enabled = false;
                OncePerLine.Reset();
                var without = TracePauses(LoopScript, new[] { 3, 5 }, DebugAction.Continue);
                OncePerLine.Enabled = true;
                OncePerLine.Reset();
                var with = TracePauses(LoopScript, new[] { 3, 5 }, DebugAction.Continue);
                Console.WriteLine("    continue, without: " + without + "; with: " + with);
                Check("RhinoCode alone pauses for every item", without.Split(' ').Count(p => p == "3") > 1, true);
                Check("continue: once per line", with, "3 5");
                var steps = TracePauses(LoopScript, new[] { 3 }, DebugAction.StepOver);
                Console.WriteLine("    step over from line 3: " + steps);
                Check("step over: once per line", steps.StartsWith("3 4 5 6 7"), true);
                var into = TracePauses(LoopScript, new[] { 3 }, DebugAction.StepOver, DebugAction.StepIn);
                Check("step into still goes into the comprehension", into.StartsWith("3 3"), true);
                // Continue without breakpoints switches tracing off; Break now on again.
                var plain = new SteppingControls();
                plain.Attach(Guid.NewGuid());
                typeof(DebugControls).GetProperty(nameof(DebugControls.CanTrace)).SetValue(plain, false);
                Threads.RequestBreak(plain, 0);
                Check("Break now switches tracing on again", plain.CanTrace, true);
                Threads.TakeBreak(plain, ExecFrame.OnLine(new ContextIdentity("x"), ExecThreadId.Empty, new ExecFrameId("m"), new CodeReference(new Uri("file:///C:/x.py"), 1), Array.Empty<ExecVariable>()));

                // Break now during Step over on a one-line loop: the hold would swallow every
                // round, so the request is taken first (else a UI-thread script spins for good).
                var spin = TracePauses(OneLineLoop, new[] { 3 }, DebugAction.StepOver, (c, n) =>
                {
                    if (n == 40)
                        Threads.RequestBreak(c, 0);
                }, DebugAction.StepOver);
                Console.WriteLine("    step over a one-line loop, Break now at the 40th line event: " + spin);
                Check("Break now ends a once-per-line hold", spin.StartsWith("3 3"), true);
                Threads.ResetRun();

                // Break now in the script only: a line of a module doesn't take it, the script's next line does.
                var scriptOf = Threads.ScriptOf;
                try
                {
                    Threads.ScriptOf = c => new Uri("file:///C:/work/main.py");
                    Threads.RequestBreak(plain, 0);
                    ExecFrame LineIn(string file) => ExecFrame.OnLine(new ContextIdentity("x"), ExecThreadId.Empty, new ExecFrameId("m"), new CodeReference(new Uri(file), 3), Array.Empty<ExecVariable>());
                    Check("Break now: not in a module", Threads.TakeBreak(plain, LineIn("file:///C:/work/helpers.py")), false);
                    Check("Break now: in the script", Threads.TakeBreak(plain, LineIn("file:///C:/Work/MAIN.py")), true);
                }
                finally
                {
                    Threads.ScriptOf = scriptOf;
                }
            }
            finally
            {
                OncePerLine.Enabled = true;
                OncePerLine.Reset();
            }
        }
    }
}
