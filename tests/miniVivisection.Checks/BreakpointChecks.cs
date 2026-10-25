// Checks for conditional breakpoints: the C# evaluator, and RhinoCode's own pause
// decision (NextStep.ShouldPause) with the plug-in's Harmony postfix installed, for C#
// frames and for Python frames inside a simulated trace callback.
using System;
using System.Collections.Generic;
using System.Linq;
using Python.Runtime;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        /// <summary>A debugger that never waits: only its breakpoints are used.</summary>
        private sealed class StandInControls : DebugControls
        {
            public override DebugAction Pause() => DebugAction.Continue;

            public override void Proceed(DebugAction action)
            {
            }
        }

        private static void CSharpConditionChecks()
        {
            var variables = new Dictionary<string, object>
            {
                ["i"] = 5,
                ["name"] = "abc",
                ["pts"] = new List<double> { 1, 2, 3 },
                ["none"] = null,
                ["arr"] = new[] { 10, 20 },
            };
            object Eval(string expression)
            {
                try
                {
                    return CSharpConditions.Evaluate(expression, variables);
                }
                catch (ConditionException ex)
                {
                    return "error: " + ex.Message;
                }
            }
            Check("compare", Eval("i > 3"), true);
            Check("and/or", Eval("i > 3 && name == \"abc\" || false"), true);
            Check("arithmetic", Eval("i * 2 + 1"), 11);
            Check("modulo", Eval("i % 2 == 1"), true);
            Check("member", Eval("pts.Count"), 3);
            Check("indexer", Eval("pts[1] + 0.5"), 2.5);
            Check("array", Eval("arr[1] - arr[0]"), 10);
            Check("method", Eval("name.StartsWith(\"ab\")"), true);
            Check("string +", Eval("name + i"), "abc5");
            Check("Math", Eval("Math.Max(i, 10)"), 10);
            Check("not", Eval("!(i < 3)"), true);
            Check("null", Eval("none == null"), true);
            Check("short-circuit &&", Eval("none != null && none.Length > 0"), false);
            Check("short-circuit ||", Eval("none == null || none.Length > 0"), true);
            Check("unknown name", Eval("missing > 1"), "error: 'missing' isn't a variable here");
            Check("syntax ok", CSharpConditions.SyntaxError("a.b(c, 1) >= 2 && !d"), null);
            Check("syntax ok ==", CSharpConditions.SyntaxError("a.Length == 3 && b != null"), null);
            Check("syntax error", CSharpConditions.SyntaxError("i >") != null, true);
            Check("syntax trailing", CSharpConditions.SyntaxError("i > 3 )"), "unexpected ')'");

            Check("hit 3", BreakpointConditions.HitMatches("3", 3) && !BreakpointConditions.HitMatches("3", 4), true);
            Check("hit >= 2", BreakpointConditions.HitMatches(">= 2", 2) && !BreakpointConditions.HitMatches(">=2", 1), true);
            Check("hit % 10", BreakpointConditions.HitMatches("% 10", 20) && !BreakpointConditions.HitMatches("%10", 15), true);
            Check("hit syntax", BreakpointConditions.HitSyntaxError("every 3") != null && BreakpointConditions.HitSyntaxError("> 4") == null, true);
            Check("format", BreakpointConditions.Format("i={i} {{x}} n={pts.Count} {bad}", variables), "i=5 {x} n=3 <'bad' isn't a variable here>");
        }

        private static void BreakpointChecks()
        {
            BreakpointConditions.TryInstall();
            var controls = new StandInControls();

            // --- C#: the frame's variables -------------------------------------------------
            var cs = new Uri("file:///C:/checks/script.cs");
            controls.Breakpoints.Add(new CodeReferenceBreakpoint(cs, 7));
            var terminal = new List<string>();
            BreakpointConditions.TerminalWriter = (c, text) => terminal.Add(text);
            string RunCs(int line, int from, int to)
            {
                var paused = new List<int>();
                for (var i = from; i <= to; i++)
                {
                    var frame = ExecFrame.OnLine(ContextIdentity.Empty, ExecThreadId.Empty, new ExecFrameId("Main"), new CodeReference(cs, line),
                        new[] { new ExecVariable("i", i), new ExecVariable("name", "abc") });
                    if (NextStep.PauseOnNextBreakpoint().ShouldPause(controls, frame, null, out _))
                        paused.Add(i);
                }
                return string.Join(",", paused);
            }
            Check("no spec: every time", RunCs(7, 0, 3), "0,1,2,3");
            Check("no breakpoint: never", RunCs(8, 0, 3), "");
            BreakpointSpecs.Set(cs, 7, s => s.Condition = "i > 2 && name.Length == 3");
            Check("C# condition", RunCs(7, 0, 5), "3,4,5");
            Check("C# condition: no errors", UserLog.Entries.Any(e => e.Text.Contains("the condition 'i > 2")), false);
            Check("pause reason", BreakpointConditions.TakeReason(), "i > 2 && name.Length == 3");
            BreakpointSpecs.ResetHits();
            BreakpointSpecs.Set(cs, 7, s => s.HitCondition = ">= 2");
            Check("C# condition + hit count", RunCs(7, 0, 5), "4,5");
            Check("hits counted", BreakpointSpecs.Find(cs, 7).Hits, 3);
            BreakpointSpecs.Set(cs, 7, s =>
            {
                s.HitCondition = "";
                s.LogMessage = "i={i} {{i}}";
            });
            Check("C# logpoint never pauses", RunCs(7, 0, 4), "");
            Check("C# logpoint lines", string.Join(";", terminal), "i=3 {i};i=4 {i}");
            Check("logpoint in the user log", UserLog.Entries.Any(e => e.Text == "Logpoint, line 7: i=4 {i}"), true);
            BreakpointSpecs.Set(cs, 7, s =>
            {
                s.LogMessage = "";
                s.Condition = "missing > 1";
            });
            var warningsBefore = UserLog.Entries.Count(e => e.Text.Contains("the condition 'missing > 1' failed"));
            Check("C# condition error pauses", RunCs(7, 0, 2), "0,1,2");
            Check("error kept", BreakpointSpecs.Find(cs, 7).LastError, "'missing' isn't a variable here");
            Check("error reported once", UserLog.Entries.Count(e => e.Text.Contains("the condition 'missing > 1' failed")) - warningsBefore, 1);
            Check("inactive breakpoint", RunInactive(controls, cs), false);
            BreakpointSpecs.Remove(cs, 7);
            Check("removed: every time", RunCs(7, 0, 1), "0,1");

            // --- Python: inside the trace callback ---------------------------------------------
            var py = new Uri("file:///C:/checks/script.py");
            controls.Breakpoints.Add(new CodeReferenceBreakpoint(py, 4));
            Func<int, bool> decide = line => NextStep.PauseOnNextBreakpoint().ShouldPause(controls,
                ExecFrame.OnLine(ContextIdentity.Empty, ExecThreadId.Empty, new ExecFrameId("loop"), new CodeReference(py, line), Array.Empty<ExecVariable>()),
                null, out _);
            string RunPy()
            {
                using (Py.GIL())
                {
                    var scope = Py.CreateScope();
                    scope.Set("decide", decide.ToPython());
                    scope.Exec(@"
import sys, io

def loop():
    paused = []
    for i in range(10):
        marker = i   # line 4 of the function: the breakpoint
    return paused

paused = []
def __pynet_tracefunc__(frame, event, arg):
    if frame.f_code.co_name == 'loop' and event == 'line':
        line = frame.f_lineno - loop.__code__.co_firstlineno
        if line == 3 and decide(4):
            paused.append(frame.f_locals['i'])
    return __pynet_tracefunc__

out = io.StringIO()
sys.stdout, saved = out, sys.stdout
sys.settrace(__pynet_tracefunc__)
loop()
sys.settrace(None)
sys.stdout = saved
printed = out.getvalue()
");
                    var printed = scope.Get("printed").ToString();
                    var paused = string.Join(",", ((PyObject)scope.Get("paused")).As<int[]>());
                    return paused + (printed.Length > 0 ? " | " + printed.TrimEnd().Replace("\n", ";") : "");
                }
            }
            Check("Python: every time", RunPy(), "0,1,2,3,4,5,6,7,8,9");
            BreakpointSpecs.Set(py, 4, s => s.Condition = "i % 3 == 0");
            Check("Python condition", RunPy(), "0,3,6,9");
            BreakpointSpecs.ResetHits();
            BreakpointSpecs.Set(py, 4, s => s.HitCondition = "2");
            Check("Python condition + hit count", RunPy(), "3");
            BreakpointSpecs.Set(py, 4, s =>
            {
                s.HitCondition = "";
                s.LogMessage = "i={i}, sq={i * i}, {{ok}}";
            });
            Check("Python logpoint prints", RunPy(), " | i=0, sq=0, {ok};i=3, sq=9, {ok};i=6, sq=36, {ok};i=9, sq=81, {ok}");
            BreakpointSpecs.Set(py, 4, s =>
            {
                s.LogMessage = "";
                s.Condition = "undefined_name > 1";
            });
            Check("Python condition error pauses", RunPy(), "0,1,2,3,4,5,6,7,8,9");
            Check("Python error kept", BreakpointSpecs.Find(py, 4).LastError, "name 'undefined_name' is not defined");
            Check("Python syntax check", PythonBridge.SyntaxError("i >") != null && PythonBridge.SyntaxError("i > 1 and x.y") == null, true);
            BreakpointSpecs.Remove(py, 4);
        }

        /// <summary>The hover preview's scoping: a name as Python resolves it at a line.</summary>
        private static void HoverScopeChecks()
        {
            const string source = @"
import sys
crv = 'global curve'          # global line
other = 7

def helper():
    crv = 'helper local'      # helper line
    return crv

def uses_global():
    return crv + '!'          # uses line

def target():
    crv = 5                   # assign line
    later = 1                 # pause line
    unassigned_yet = 2        # unassigned line
    return crv

def __pynet_tracefunc__(frame, event, arg):
    if frame.f_code.co_name == 'target' and event == 'line' and frame.f_lineno == target.__code__.co_firstlineno + 2:
        callback()
    return __pynet_tracefunc__

sys.settrace(__pynet_tracefunc__)
target()
sys.settrace(None)
";
            var lines = source.Split('\n');
            int LineOf(string marker) => Array.FindIndex(lines, l => l.Contains("# " + marker)) + 1;
            var results = new List<(string, object, object)>();
            object At(string expression, string marker)
            {
                try
                {
                    return PythonBridge.EvalAt(expression, LineOf(marker), null);
                }
                catch (Exception ex)
                {
                    return ex.Message.Contains("not in scope") ? "not in scope" : "error: " + ex.Message;
                }
            }
            Action paused = () =>
            {
                results.Add(("global at module level", At("crv", "global line"), "global curve"));
                results.Add(("local in the paused function", At("crv", "assign line"), 5));
                results.Add(("expression in the paused function", At("crv + 1", "assign line"), 6));
                results.Add(("global read in the paused function", At("other", "assign line"), 7));
                results.Add(("local of a function not running", At("crv", "helper line"), "not in scope"));
                results.Add(("global in a function not running", At("crv", "uses line"), "global curve"));
                results.Add(("local not assigned yet", At("unassigned_yet", "unassigned line"), "not in scope"));
                results.Add(("paused frame as before", PythonBridge.Eval("crv"), 5));
            };
            using (Py.GIL())
            {
                var scope = Py.CreateScope();
                scope.Set("callback", paused.ToPython());
                scope.Exec(source);
            }
            Check("hover: paused", results.Count, 8);
            foreach (var (what, actual, expected) in results)
                Check("hover: " + what, actual, expected);
        }

        /// <summary>The two-state buttons, rendered: ON is a green pill, OFF has none;
        /// the images go to %TEMP%\minivivisection-toggles-*.png for a look.</summary>
        private static void StateToggleChecks()
        {
            int GreenPixels(StateToggle toggle, string file)
            {
                var size = toggle.Size;
                using (var bitmap = new Eto.Drawing.Bitmap(size.Width * 4, size.Height * 4, Eto.Drawing.PixelFormat.Format32bppRgba))
                {
                    using (var g = new Eto.Drawing.Graphics(bitmap))
                    {
                        g.Clear(Eto.Drawing.Color.FromArgb(30, 30, 30));
                        g.ScaleTransform(4, 4);
                        toggle.Render(g, size);
                    }
                    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minivivisection-toggles-" + file + ".png");
                    bitmap.Save(path, Eto.Drawing.ImageFormat.Png);
                    var count = 0;
                    for (var y = 0; y < bitmap.Height; y++)
                        for (var x = 0; x < bitmap.Width; x++)
                        {
                            var c = bitmap.GetPixel(x, y);
                            if (c.Gb > 128 && c.Rb < 80) // Rb, Gb: bytes
                                count++;
                        }
                    return count;
                }
            }
            var auto = new StateToggle(DrawnIcons.AutoEdit, null, "Auto edit", "test", showState: true);
            Check("toggle off: no green", GreenPixels(auto, "auto-off"), 0);
            auto.Checked = true;
            var green = GreenPixels(auto, "auto-on");
            Check("toggle on: green pill (" + green + " px)", green > 400, true);
            Check("toggle tooltip", auto.ToolTip.StartsWith("Auto edit: ON"), true);
            auto.ButtonLook = true;
            GreenPixels(auto, "auto-button-on");
            auto.Checked = false;
            GreenPixels(auto, "auto-button-off");
            var aiSwitch = new StateToggle(DrawnIcons.EyeOpen, null, "AI", null, showState: true) { Caption = "AI", Checked = true };
            GreenPixels(aiSwitch, "ai-on");
            var debugSwitch = new StateToggle(DrawnIcons.EyeOpen, null, "Debug", null, showState: true) { Caption = "Debug" };
            GreenPixels(debugSwitch, "debug-off");
            var eye = new StateToggle(DrawnIcons.EyeOpen, DrawnIcons.EyeClosed, "Drawn in Rhino", null, showState: false);
            GreenPixels(eye, "eye-off");
            eye.Checked = true;
            GreenPixels(eye, "eye-on");
        }

        /// <summary>The code's tooltips while paused: what a value is and the value.</summary>
        private static void HoverTextChecks()
        {
            Check("expression: name", HoverText.ExpressionAt("    total = count + 1", 13), "count");
            Check("expression: after the name", HoverText.ExpressionAt("    total = count + 1", 17), "count");
            Check("expression: member", HoverText.ExpressionAt("a = crv.PointAtStart.X", 10), "crv.PointAtStart");
            Check("expression: base of a member", HoverText.ExpressionAt("a = crv.PointAtStart.X", 5), "crv");
            Check("expression: indexed", HoverText.ExpressionAt("y = curves[0].Domain", 16), "curves[0].Domain");
            Check("expression: keyword", HoverText.ExpressionAt("for i in range(3):", 1), null);
            Check("expression: number", HoverText.ExpressionAt("x = 42", 4), null);
            Check("expression: just after a name", HoverText.ExpressionAt("x = 42", 1), "x");
            Check("expression: operator", HoverText.ExpressionAt("x = 42", 2), null);

            const string source = @"
import sys
import clr
clr.AddReference('RhinoCommon')
import Rhino.Geometry as rg

class Node:
    def __init__(self):
        self.x = 3
        self.label = 'n1'

def scale(v, k=2):
    return v * k

count = 5
ratio = 2.5
flag = True
name = 'abc'
pts = [1, 2, 3]
lookup = {'a': 1, 'b': 2}
nothing = None
node = Node()
p = rg.Point3d(1, 2, 3)
long_text = 'x' * 150
many = list(range(250))
corners = [rg.Point3d(0, 0, 0), rg.Point3d(1, 0, 0)]

def target():
    local_count = 7
    marker = 1   # pause line
    return local_count

def __pynet_tracefunc__(frame, event, arg):
    if frame.f_code.co_name == 'target' and event == 'line' and frame.f_lineno == target.__code__.co_firstlineno + 2:
        callback()
    return __pynet_tracefunc__

sys.settrace(__pynet_tracefunc__)
target()
sys.settrace(None)
";
            var lines = source.Split('\n');
            var moduleLine = Array.FindIndex(lines, l => l.StartsWith("count = 5")) + 1;
            var pauseLine = Array.FindIndex(lines, l => l.Contains("# pause line")) + 1;
            var results = new List<(string, object, object)>();
            string Tip(string expression, int line)
            {
                try
                {
                    return HoverText.ForPython(expression, line, null);
                }
                catch (Exception ex)
                {
                    return "error: " + ScriptEditorHook.FirstLine(ex);
                }
            }
            Action paused = () =>
            {
                results.Add(("integer", Tip("count", moduleLine), "```python\ncount = 5\n```"));
                results.Add(("local integer", Tip("local_count", pauseLine), "```python\nlocal_count = 7\n```"));
                results.Add(("float", Tip("ratio", moduleLine), "```python\nratio = 2.5\n```"));
                results.Add(("boolean", Tip("flag", moduleLine), "```python\nflag = True\n```"));
                results.Add(("string", Tip("name", moduleLine), "```python\nname = 'abc'\n```"));
                results.Add(("list", Tip("pts", moduleLine), "```python\npts = [1,2,3]\n```\n\n*3 items*"));
                results.Add(("dictionary", Tip("lookup", moduleLine), "```python\nlookup = {'a': 1,'b': 2}\n```\n\n*2 entries*"));
                results.Add(("None", Tip("nothing", moduleLine), "```python\nnothing = None\n```"));
                results.Add(("member", Tip("node.x", moduleLine), "```python\nnode.x = 3\n```"));
                results.Add(("object", Tip("node", moduleLine), "```python\nnode = Node(x=3,label='n1')\n```"));
                results.Add(("function", Tip("scale", moduleLine), "```python\ndef scale(v,k=2)\n```"));
                results.Add(("Rhino point", Tip("p", moduleLine), "```python\np = (1,2,3)\n```"));
                results.Add(("long list: first items and how many more", Tip("many", moduleLine), "```python\nmany = [0,1,2,… +247]\n```\n\n*250 items*"));
                results.Add(("list of points", Tip("corners", moduleLine), "```python\ncorners = [(0,0,0),(1,0,0)]\n```\n\n*2 items*"));
                results.Add(("long text", Tip("long_text", moduleLine), "```python\nlong_text = 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx…'\n```\n\n*150 characters*"));
                results.Add(("not in scope", Tip("local_count", moduleLine).StartsWith("error: "), true));
            };
            using (Py.GIL())
            {
                var scope = Py.CreateScope();
                scope.Set("callback", paused.ToPython());
                scope.Exec(source);
            }
            Check("tooltips: paused", results.Count, 16);
            foreach (var (what, actual, expected) in results)
                Check("tooltip: " + what, actual, expected);
            Check("tooltip: C# value", HoverText.ForValue("count", 5), "```csharp\ncount = 5\n```");
            Check("tooltip: C# string", HoverText.ForValue("name", "abc"), "```csharp\nname = \"abc\"\n```");
            Check("tooltip: C# line", HoverText.ForValue("l", new Rhino.Geometry.Line(0, 0, 0, 3, 4, 0)), "```csharp\nl = (0,0,0)→(3,4,0)\n```\n\n*length 5*");
            Check("tooltip: C# list", HoverText.ForValue("xs", new List<int> { 1, 2, 3, 4, 5, 6, 7, 8 }), "```csharp\nxs = [1,2,3,… +5]\n```\n\n*8 items*");
            Check("tooltip: box facts", HoverText.Facts(new Rhino.Geometry.BoundingBox(0, 0, 0, 2, 3, 4)), "size 2×3×4");
            Check("compact: commas", HoverText.Compact("(1, 2, 3)"), "(1,2,3)");
            Check("compact: quoted text kept", HoverText.Compact("['a, b', \"c, d\", 'it\\'s, x', 2]"), "['a, b',\"c, d\",'it\\'s, x',2]");
        }

        /// <summary>Inline values, "was" after a step, and the tooltip's links.</summary>
        private static void ChangesChecks()
        {
            var code = "def f(k):\n    total = k * 2   # total here\n    s = 'total'\n    return obj.total + total\n";
            var texts = new Dictionary<string, string> { ["k"] = "3", ["total"] = "6", ["s"] = "'total'" };
            var lines = InlineText.Lines(code, 1, 4, texts, new HashSet<string> { "total" });
            Check("inline: lines", string.Join(" | ", lines.Select(l => l.Line + ":" + l.Text + (l.Changed ? "*" : ""))),
                "1:k = 3 | 2:total = 6, k = 3* | 3:s = 'total' | 4:total = 6*");

            var debugger = new object();
            ValueHistory.NewPause(debugger, "f|1", 1, new Dictionary<string, string> { ["i"] = "0", ["xs"] = "[0]" });
            Check("was: first pause", ValueHistory.WasFor(debugger, "i", 3, "0"), null);
            ValueHistory.WasFor(debugger, "xs[0]", 3, "0");
            ValueHistory.NewPause(debugger, "f|1", 1, new Dictionary<string, string> { ["i"] = "1", ["xs"] = "[0,1]" });
            Check("was: a variable", ValueHistory.WasFor(debugger, "i", 3, "1"), "0");
            Check("was: unchanged", ValueHistory.WasFor(debugger, "xs[0]", 3, "0"), null);
            Check("changed", ValueHistory.Changed(debugger, "xs", "[0,1]") && !ValueHistory.Changed(debugger, "i", "0"), true);
            ValueHistory.NewPause(debugger, "g|9", 9, new Dictionary<string, string> { ["i"] = "5" });
            Check("was: another frame starts afresh", ValueHistory.WasFor(debugger, "i", 10, "5"), null);

            var tip = new HoverText.Tip { Text = "[1, 2]", Facts = "2 items", Language = "python" };
            Check("tooltip with was and links", HoverText.Markdown("xs", tip, "[1]", new List<(string, string)> { ("Watch in Rhino", HoverText.LinkFor("watch", "xs[0]", 7)) }),
                "```python\nxs = [1,2]\n```\n\n*2 items · was \\[1\\]*\n\n[Watch in Rhino](https://minivivisection.invalid/watch?e=xs%5B0%5D&l=7&t=" + HoverText.LinkToken + " \"Add xs to the " + VNames.Of("Watch") + " tab: drawn in Rhino at every pause\")");

            // The paused frame's variables, at two pauses of a loop.
            const string source = @"
import sys

def loop():
    total = 0
    for i in range(3):
        total += i   # pause line
    return total

def __pynet_tracefunc__(frame, event, arg):
    if frame.f_code.co_name == 'loop' and event == 'line' and frame.f_lineno == loop.__code__.co_firstlineno + 3:
        callback()
    return __pynet_tracefunc__

sys.settrace(__pynet_tracefunc__)
loop()
sys.settrace(None)
";
            var results = new List<string>();
            var history = new object();
            Action paused = () =>
            {
                try
                {
                    var (key, first, current, items) = PythonBridge.InlineValues();
                    var values = items.ToDictionary(t => t.Name, t => t.Text);
                    ValueHistory.NewPause(history, key, first, values);
                    results.Add(string.Format("{0}-{1}: {2} | total changed: {3} | was {4}", first, current,
                        string.Join(", ", items.Select(t => t.Name + "=" + t.Text)), ValueHistory.Changed(history, "total", values["total"]),
                        ValueHistory.WasFor(history, "total", current, values["total"]) ?? "-"));
                }
                catch (Exception ex)
                {
                    results.Add("error: " + ex.Message);
                }
            };
            using (Py.GIL())
            {
                var scope = Py.CreateScope();
                scope.Set("callback", paused.ToPython());
                scope.Exec(source);
            }
            Check("inline values: pauses", results.Count, 3);
            Check("inline values: first pause", results.ElementAtOrDefault(0), "4-7: total=0, i=0 | total changed: False | was -");
            Check("inline values: second pause", results.ElementAtOrDefault(1), "4-7: total=0, i=1 | total changed: False | was -");
            Check("inline values: third pause", results.ElementAtOrDefault(2), "4-7: total=1, i=2 | total changed: True | was 0");
        }

        private static bool RunInactive(StandInControls controls, Uri uri)
        {
            var breakpoint = new CodeReferenceBreakpoint(uri, 20, active: false);
            controls.Breakpoints.Add(breakpoint);
            BreakpointSpecs.Set(uri, 20, s => s.Condition = "true");
            var frame = ExecFrame.OnLine(ContextIdentity.Empty, ExecThreadId.Empty, new ExecFrameId("Main"), new CodeReference(uri, 20), Array.Empty<ExecVariable>());
            var paused = NextStep.PauseOnNextBreakpoint().ShouldPause(controls, frame, null, out _);
            BreakpointSpecs.Remove(uri, 20);
            return paused;
        }
    }
}
