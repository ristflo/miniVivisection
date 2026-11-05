// IronPythonBridge against a real IronPython 2.7 (Rhino's IronPython.dll), hosted here with
// tracing on: a script paused in a function and at module level, as RhinoCode's IronPython
// tracer pauses it, its TraceBackFrame behind an ExecFrame closure as there.
using System;
using System.Collections.Generic;
using System.Linq;
using IronPython.Runtime.Exceptions;
using Rhino.Runtime.Code.Execution;

namespace MiniVivisection
{
    /// <summary>What the traced script hands over: the frame at a pause, and what runs then.
    /// Public, as a type IronPython binds to must be.</summary>
    public sealed class IronPythonPause
    {
        public object Frame;
        public Action<object> OnPause;

        // Called from the script's trace function, with the frame live
        public void Paused(object frame)
        {
            Frame = frame;
            OnPause?.Invoke(frame);
        }
    }

    internal static partial class Checks
    {
        private const string IronPythonLib = @"C:\Program Files\Rhino 8\Plug-ins\IronPython\Lib";

        private const string IronPythonScript = @"import sys
def tracer(frame, event, arg):
    if event == 'line' and frame.f_code.co_name == 'work' and frame.f_lineno == 11:
        pause.Paused(frame)
    elif event == 'line' and frame.f_code.co_name != 'work' and frame.f_lineno == 2:
        pause.Paused(frame)
    return tracer
def work(points):
    n = len(points)
    scale = 1.0
    top = n
    return n * scale * points[0]
sys.settrace(tracer)
result = work([1, 2, 3])
exec 'done = True\nlast = done' in globals()   # module-level code that is traced (the frame that set the trace isn't)
sys.settrace(None)
";

        /// <summary>An ExecFrame as RhinoCode's IronPython tracer makes it: its variables come
        /// from a closure over the TraceBackFrame (the field IronPythonBridge.FrameOf looks for).</summary>
        private static ExecFrame ExecFrameOver(object frame, int line)
        {
            var tb = (TraceBackFrame)frame;
            return ExecFrame.OnLine(ContextIdentity.Empty, ExecThreadId.Empty, new ExecFrameId("ipy"), new CodeReference(new Uri("file:///C:/checks/ipy_bridge.py"), line),
                (ExecFrame.VariableProvider)(() => NoVariables(tb)));
        }

        private static IEnumerable<ExecVariable> NoVariables(TraceBackFrame frame) => Enumerable.Empty<ExecVariable>();

        private static void IronPythonBridgeChecks()
        {
            var engine = IronPython.Hosting.Python.CreateEngine(new Dictionary<string, object> { ["Tracing"] = true, ["Frames"] = true });
            if (System.IO.Directory.Exists(IronPythonLib))
                engine.SetSearchPaths(new[] { IronPythonLib });
            Check("IronPython is loaded", IronPythonBridge.IsLoaded, true);
            Check("no IronPython frame behind a plain ExecFrame", IronPythonBridge.FrameOf(ExecFrame.OnLine(ContextIdentity.Empty, ExecThreadId.Empty, new ExecFrameId("x"),
                new CodeReference(new Uri("file:///C:/checks/x.py"), 1), new[] { new ExecVariable("i", 1) })), null);

            var pauses = 0;
            var pause = new IronPythonPause();
            pause.OnPause = raw =>
            {
                pauses++;
                if (pauses == 1)
                    InWork(ExecFrameOver(raw, 11));
                else
                    AtModuleLevel(ExecFrameOver(raw, 2));
            };
            var scope = engine.CreateScope();
            scope.SetVariable("pause", pause);
            engine.Execute(IronPythonScript, scope);
            Check("paused twice", pauses, 2);
            Check("the writes reached the running function: result = 3 * 2.0 * 10", scope.GetVariable("result"), 60.0);

            Check("Describe: a .NET exception as Python names it", IronPythonBridge.Describe(new DivideByZeroException("Attempted to divide by zero.")), "ZeroDivisionError: Attempted to divide by zero.");
            Check("syntax check: fine", IronPythonBridge.SyntaxError("n == 3 and len(points) > 1"), null);
            Check("syntax check: wrong", IronPythonBridge.SyntaxError("n ==") != null, true);
        }

        private static void InWork(ExecFrame frame)
        {
            Check("the IronPython frame is found behind the ExecFrame", IronPythonBridge.FrameOf(frame) is TraceBackFrame, true);
            Check("eval a local", IronPythonBridge.Eval(frame, "n * 2"), 6);
            Check("eval an expression over a parameter", IronPythonBridge.Eval(frame, "points[1] + len(points)"), 5);
            Check("eval a comprehension", string.Join(",", ((System.Collections.IEnumerable)IronPythonBridge.Eval(frame, "[p * 2 for p in points]")).Cast<object>()), "2,4,6");
            string error = null;
            try
            {
                IronPythonBridge.Eval(frame, "nope + 1");
            }
            catch (IronPythonError ex)
            {
                error = ex.Message;
            }
            Check("an unknown name: Python's error", error, "NameError: name 'nope' is not defined");

            Check("condition holds", IronPythonBridge.ConditionHolds(frame, "n == 3 and len(points) == 3"), true);
            Check("condition doesn't", IronPythonBridge.ConditionHolds(frame, "scale > 1"), false);
            var extras = new Dictionary<string, object> { ["gh_run"] = 4, ["gh_item"] = "{0;3}[2]" };
            Check("condition with gh_run, gh_item", IronPythonBridge.ConditionHolds(frame, "gh_run == 4 and gh_item.endswith('[2]')", extras), true);
            Check("the extras are gone afterwards", IronPythonBridge.Eval(frame, "'gh_run' in globals()"), false);
            Check("logpoint", IronPythonBridge.Logpoint(frame, "n={n} {{x}} first={points[0]} bad={nope}", extras), "n=3 {x} first=1 bad=<NameError: name 'nope' is not defined>");

            Check("exec: a Python 2 print and a final expression", IronPythonBridge.Exec(frame, "print n\nn + 1"), "3\n4\n");
            Check("exec: an error is reported", IronPythonBridge.Exec(frame, "1/0").TrimEnd().Split('\n').Last().StartsWith("ZeroDivisionError"), true);
            Check("prompt: an unfinished block", IronPythonBridge.Run(frame, "for i in range(2):"), (true, ""));
            Check("prompt: an expression is printed", IronPythonBridge.Run(frame, "n"), (false, "3\n"));
            Check("prompt: a block", IronPythonBridge.Run(frame, "for i in range(2):\n    print i\n"), (false, "0\n1\n"));

            IronPythonBridge.Assign(frame, "scale", 2.0);           // the function's local
            IronPythonBridge.Assign(frame, "points[0]", 10);        // an item of its parameter
            Check("assigned: the local", IronPythonBridge.Eval(frame, "scale"), 2.0);
            Check("assigned: the item", IronPythonBridge.Eval(frame, "points"), IronPythonBridge.Eval(frame, "[10, 2, 3]"));
            Check("no temporary left in the globals", IronPythonBridge.Eval(frame, "'__minivivisection_value' in globals()"), false);

            Check("frame key: function and first line", IronPythonBridge.FrameKey(frame).EndsWith("|work|8"), true);
            var variable = IronPythonBridge.WatchVariable(frame, "points");
            Check("Watch tab row: the value (a plain ExecVariable outside Rhino)", (variable.GetValueRepr(), variable.TryGetValue(out object list) && list is IronPython.Runtime.List), ("3 Item(s)", true));
            Check("Watch tab row: an error", IronPythonBridge.WatchVariable(frame, "nope").GetValueRepr(), "NameError: name 'nope' is not defined");

            var (key, first, current, items) = IronPythonBridge.InlineValues(frame);
            Check("inline values: the frame", (key.EndsWith("|work|8"), first, current), (true, 8, 11));
            var texts = items.ToDictionary(i => i.Name, i => i.Text);
            // i and p: the prompt's loop and the comprehension above left them in the function's scope (Python 2)
            Check("inline values: the locals, with Python's text", string.Join(" ", new[] { "n", "points", "scale", "top" }.Select(n => n + "=" + (texts.TryGetValue(n, out var t) ? t : "<missing>"))),
                "n=3 points=[10, 2, 3] scale=2.0 top=None");
        }

        private static void AtModuleLevel(ExecFrame frame)
        {
            Check("module level: eval", IronPythonBridge.Eval(frame, "result"), 60.0);
            Check("module level: exec", IronPythonBridge.Exec(frame, "result = result + 1\nresult"), "61.0\n");
            IronPythonBridge.Assign(frame, "result", 60.0);
            var (key, first, current, items) = IronPythonBridge.InlineValues(frame);
            Check("module level: inline values", (first, current), (1, 2));
            var names = items.Select(i => i.Name).OrderBy(n => n).ToList();
            Check("module level: variables, not functions or modules", string.Join(" ", names), "done pause result");
            Check("module level: the frame key's code name", key.Split('|')[1], "<module>");
        }
    }
}
