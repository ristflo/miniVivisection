// Freeze guards: RhinoCode's shared trace lock (TraceLockGuard) -- a script paused on a
// thread of its own holds DebugControls.s_traceLock; another debugger starting or tracing
// on the UI thread must wait with Rhino's message loop going, not block it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        /// <summary>A debugger whose pause waits until it is told to go on.</summary>
        private sealed class HeldControls : DebugControls
        {
            public readonly ManualResetEventSlim HasPaused = new ManualResetEventSlim();
            public readonly ManualResetEventSlim Go = new ManualResetEventSlim();

            public override DebugAction Pause()
            {
                HasPaused.Set();
                Go.Wait(TimeSpan.FromSeconds(20));
                return DebugAction.Continue;
            }

            public override void Proceed(DebugAction action) => Go.Set();
        }

        // gh_run, gh_branch, gh_item in C# and IronPython 2 conditions (as Python 3 has them).
        private static void GrasshopperConditionChecks()
        {
            var run = ((int, string, string)?)(4, "{0;3}", "{0;3}[2]");
            Dictionary<string, object> Vars(params (string, object)[] own)
            {
                var variables = new Dictionary<string, object>();
                foreach (var (name, value) in own)
                    variables[name] = value;
                return BreakpointConditions.WithGrasshopperRun(variables, run);
            }
            Check("gh_run in a C# condition", CSharpConditions.IsTrue("gh_run == 4", Vars()), true);
            Check("another run doesn't match", CSharpConditions.IsTrue("gh_run == 7", Vars()), false);
            Check("gh_item, as the menu writes it", CSharpConditions.IsTrue("gh_item == \"{0;3}[2]\"", Vars()), true);
            Check("gh_branch", CSharpConditions.IsTrue("gh_branch == \"{0;3}\"", Vars()), true);
            Check("the script's own gh_run wins", CSharpConditions.IsTrue("gh_run == 1", Vars(("gh_run", 1))), true);
            Check("outside a Grasshopper run: not there", BreakpointConditions.WithGrasshopperRun(new Dictionary<string, object>(), null).ContainsKey("gh_run"), false);
        }

        // Which of Claude's expressions are read without asking, and which are code to approve.
        // "Mark in the V names": ° by default; text written "V Watch" (or with any mark) shown with it.
        private static void VNameChecks()
        {
            var degree = ((char)0x00B0).ToString();
            var onTheLine = ((char)0x2223).ToString();
            var thin = ((char)0x2009).ToString();
            Check("the default mark: the degree sign", VNames.Of("Watch"), "V" + degree + "Watch");
            Check("five choices", VNames.Marks.Length, 5);
            Check("the setting's default", MiniVivisectionSettings.Find("NameMark").Default, VNames.DefaultChoice);
            Check("text written with a space", VNames.Text("open V Watch or V Python, then V Log"),
                "open V" + degree + "Watch or V" + degree + "Python, then V" + degree + "Log");
            Check("written with another mark", VNames.Text("V|Edit, V" + onTheLine + "Help, V" + thin + "|" + thin + "Claude"),
                "V" + degree + "Edit, V" + degree + "Help, V" + degree + "Claude");
            Check("V AI and V Debug too", VNames.Text("V AI, V Debug"), "V" + degree + "AI, V" + degree + "Debug");
            Check("other words left alone", VNames.Text("V Watchman, a V shape, VWatch"), "V Watchman, a V shape, VWatch");
            Check("a setting's label", MiniVivisectionSettings.Find("TabRhinoWatch").Label, "Tab (bottom): V" + degree + "Watch");
        }

        // Hover: the whole call around a name -- "Watch rg.Point3d(…)" (HoverText.CallAt).
        private static void CallAtChecks()
        {
            const string line = "    pts.append(rg.Point3d(ri * math.cos(th), ri * math.sin(th), height * t))";
            const string call = "rg.Point3d(ri * math.cos(th), ri * math.sin(th), height * t)";
            Check("on the called name", HoverText.CallAt(line, line.IndexOf("Point3d") + 2), call);
            Check("on its module: the same call", HoverText.CallAt(line, line.IndexOf("rg.") + 1), call);
            Check("pts.append: its call (a method -- the tooltip offers no link for it)", HoverText.CallAt(line, line.IndexOf("append") + 1),
                "pts.append(rg.Point3d(ri * math.cos(th), ri * math.sin(th), height * t))");
            Check("an argument isn't called", HoverText.CallAt(line, line.IndexOf("ri *") + 1), null);
            Check("a parenthesis in a string", HoverText.CallAt("x = rg.TextDot(\"a)b\", p)", 8), "rg.TextDot(\"a)b\", p)");
            Check("not called", HoverText.CallAt("y = rg.Point3d.Origin", 8), null);
            Check("the call goes on to the next line", HoverText.CallAt("p = rg.Point3d(1,", 7), null);
            Check("a comment before it closes", HoverText.CallAt("p = rg.Point3d(1, # x)", 7), null);
            Check("a space before the parenthesis", HoverText.CallAt("p = rg.Vector3d (1, 0, 0)", 8), "rg.Vector3d (1, 0, 0)");
            Check("its label", HoverText.CallLabel(call), "rg.Point3d(…)");
        }

        // V°Claude's / commands, Tab completion (SlashCommands).
        private static void SlashCommandChecks()
        {
            const string help = "Type.\n  /debug [question]       paused frame\n  /deny                   refuse\n  /run, /deny             run or refuse\n";
            (string, string) C(string input)
            {
                var (completed, choices) = SlashCommands.Complete(input, help);
                return (completed, string.Join(" | ", choices.Select(c => c.Trim())));
            }
            Check("one match, more after it", C("/rep"), ("/replace ", ""));
            Check("one match, nothing after it", C("/his"), ("/history", ""));
            Check("case doesn't matter", C("/HIS"), ("/history", ""));
            Check("as far as they agree", C("/re"), ("/re", "/replace | /reject"));
            Check("several: their help lines", C("/de").Item2, "/debug [question]       paused frame | /deny                   refuse | /run, /deny             run or refuse");
            Check("/a: /apply and /allow agree on /a", C("/a").Item1, "/a");
            Check("the argument", C("/undo f"), ("/undo force", ""));
            Check("arguments that differ", C("/allow o"), ("/allow o", "/allow on|off"));
            Check("an empty input: / and every command", C("").Item1, "/");
            Check("no such command: as it is", C("/zz"), ("/zz", ""));
            Check("a message: not completed", C("hello").Item1, null);
            Check("every command", string.Join(" ", SlashCommands.Names), "/replace /fix /explain /debug /why /pending /apply /reject /undo /history /allow /run /deny /new /clear /help");
        }

        private static void CodeApprovalChecks()
        {
            Check("a name", CodeApproval.IsPlain("crv"), true);
            Check("attributes and an index", CodeApproval.IsPlain("pts[2].X"), true);
            Check("a comprehension without calls", CodeApproval.IsPlain("[p.X for p in pts if p.Z > 0]"), true);
            Check("a comparison", CodeApproval.IsPlain("i == 3 and name != 'x'"), true);
            Check("a call: code", CodeApproval.IsPlain("crv.PointAtEnd()"), false);
            Check("an import through a dunder", CodeApproval.IsPlain("__import__"), false);
            Check("a dunder attribute", CodeApproval.IsPlain("x.__class__"), false);
            Check("an assignment expression", CodeApproval.IsPlain("(y := 1)"), false);
            Check("a plain assignment", CodeApproval.IsPlain("y = 1"), false);
            Check("a lambda", CodeApproval.IsPlain("lambda: 1"), false);
            Check("two statements", CodeApproval.IsPlain("a; b"), false);
            Check("a log message's expressions", string.Join("|", CodeApproval.LogExpressions("i={i}, len={len(pts)}")), "i|len(pts)");
        }

        // A condition for one debug run (Debug failing run N) on a line with the user's own spec.
        private static void OneShotChecks()
        {
            var uri = new Uri("file:///C:/checks/oneshot.py");
            BreakpointSpecs.Set(uri, 12, s =>
            {
                s.Condition = "a > 1";
                s.LogMessage = "x={x}";
            });
            BreakpointSpecs.SetOneShot(uri, 12, "gh_run == 4");
            Check("the one-run condition decides now", BreakpointSpecs.Find(uri, 12)?.Condition, "gh_run == 4");
            Check("the user's log message set aside meanwhile", BreakpointSpecs.Find(uri, 12)?.LogMessage, "");
            var kept = BreakpointSpecs.Entries().Select(e => System.Text.Json.JsonDocument.Parse(e).RootElement.GetProperty("c").GetString()).ToList();
            Check("the user's spec is what's saved", kept.Contains("a > 1"), true);
            Check("the one-run condition is never saved", kept.Any(c => c.Contains("gh_run")), false);
            BreakpointSpecs.EndOneShot(uri, 12);
            Check("after the run: the user's condition back", BreakpointSpecs.Find(uri, 12)?.Condition, "a > 1");
            Check("and the log message", BreakpointSpecs.Find(uri, 12)?.LogMessage, "x={x}");

            BreakpointSpecs.SetOneShot(uri, 30, "gh_item == \"{0;1}[2]\"");
            BreakpointSpecs.EndOneShot(uri, 30);
            Check("a line without a spec of its own: none afterwards", BreakpointSpecs.Find(uri, 30), null);

            BreakpointSpecs.SetOneShot(uri, 40, "gh_run == 2");
            BreakpointSpecs.Set(uri, 40, s => s.Condition = "i == 9"); // the user changes it meanwhile
            BreakpointSpecs.EndOneShot(uri, 40);
            Check("the user's change during the run stays", BreakpointSpecs.Find(uri, 40)?.Condition, "i == 9");
            BreakpointSpecs.Remove(uri, 12);
            BreakpointSpecs.Remove(uri, 40);
        }

        // Text from the script in a tooltip's Markdown: never a link or an image.
        private static void MarkdownChecks()
        {
            Check("an image link from a value is just text", MarkdownText.Escape("![](https://host/t)"), @"\!\[\]\(https://host/t\)");
            Check("emphasis and code marks", MarkdownText.Escape("*a* `b` _c_"), @"\*a\* \`b\` \_c\_");
        }

        // The lessons the Examples tab opens: the user's working copies, never the shipped files.
        private static void LessonCopyChecks()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minivivisection-lesson-checks-" + Guid.NewGuid().ToString("N"));
            var shipped = System.IO.Path.Combine(root, "shipped");
            var working = System.IO.Path.Combine(root, "working");
            System.IO.Directory.CreateDirectory(shipped);
            string Shipped(string name) => System.IO.Path.Combine(shipped, name);
            string Working(string name) => System.IO.Path.Combine(working, name);
            System.IO.File.WriteAllText(Shipped("01_a.py"), "v1");
            System.IO.File.WriteAllText(Shipped("02_b.py"), "v1");
            System.IO.File.WriteAllText(Shipped("02_b_tools.py"), "helper");
            System.IO.Directory.CreateDirectory(Shipped("grasshopper"));
            System.IO.File.WriteAllText(Shipped(@"grasshopper\gh1_x.gh"), "gh");
            System.IO.File.WriteAllText(Shipped(@"grasshopper\gh1_x_script.py"), "run me");
            Check("the working folder is what opens", EditorExamples.WorkingCopies(shipped, working), working);
            Check("copies made, helpers too", System.IO.File.Exists(Working("01_a.py")) && System.IO.File.Exists(Working("02_b_tools.py")), true);
            Check("the Grasshopper lessons too, in grasshopper\\", System.IO.File.ReadAllText(Working(@"grasshopper\gh1_x.gh")), "gh");
            Check("and their script files, next to them", System.IO.File.ReadAllText(Working(@"grasshopper\gh1_x_script.py")), "run me");
            Check("a Grasshopper script file isn't a lesson of the Examples tab", EditorExamples.Lessons(working).Any(p => p.Contains("gh1_x_script")), false);
            System.IO.File.WriteAllText(Working("02_b.py"), "my fix");      // the user saves a fix
            System.IO.File.WriteAllText(Shipped("01_a.py"), "v2");          // a new version of both
            System.IO.File.WriteAllText(Shipped("02_b.py"), "v2");
            EditorExamples.WorkingCopies(shipped, working);
            Check("an unchanged copy follows the new version", System.IO.File.ReadAllText(Working("01_a.py")), "v2");
            Check("the user's changed copy is kept", System.IO.File.ReadAllText(Working("02_b.py")), "my fix");
            Check("the shipped lesson is never changed", System.IO.File.ReadAllText(Shipped("02_b.py")), "v2");
            System.IO.File.Delete(Working("02_b.py"));                       // to get the shipped one back
            EditorExamples.WorkingCopies(shipped, working);
            Check("deleted: copied again", System.IO.File.ReadAllText(Working("02_b.py")), "v2");
            System.IO.Directory.Delete(root, true);
        }

        private static void TraceLockChecks()
        {
            TraceLockGuard.TryInstall();
            Check("installed", TraceLockGuard.HookProblem, null);
            TraceLockGuard.SetUiThreadForChecks(Environment.CurrentManagedThreadId); // this thread plays Rhino's UI thread
            var pumps = 0;
            var pump = TraceLockGuard.Pump;
            var paused = new HeldControls();
            var script = new Uri("file:///C:/checks/async_script.py");
            paused.Breakpoints.Add(new CodeReferenceBreakpoint(script, 3));
            var worker = new Thread(() =>
            {
                paused.Attach(Guid.NewGuid());
                paused.Trace(ExecFrame.OnLine(new ContextIdentity("worker"), ExecThreadId.Empty, new ExecFrameId("<module>"),
                    new CodeReference(script, 3), Array.Empty<ExecVariable>()));
            }) { IsBackground = true };
            try
            {
                // The UI's message loop: the user continues the other script after a few rounds.
                TraceLockGuard.Pump = () =>
                {
                    if (++pumps == 5)
                        paused.Continue();
                };
                worker.Start();
                Check("the worker pauses inside the lock", paused.HasPaused.Wait(TimeSpan.FromSeconds(10)), true);
                var other = new StandInControls();
                var started = DateTime.UtcNow;
                other.Attach(Guid.NewGuid()); // another editor's Debug on the UI thread
                Check("the UI thread kept its message loop going while it waited", pumps >= 5, true);
                Check("counted as one wait", TraceLockGuard.Waits, 1);
                Check("and went on once the other pause was over", (DateTime.UtcNow - started).TotalSeconds < 10, true);
                Check("the worker's run went on", worker.Join(TimeSpan.FromSeconds(10)), true);
                other.Detach();
                Check("nothing to wait for: no pumping", pumps, 5);
            }
            finally
            {
                TraceLockGuard.Pump = pump;
                paused.Go.Set();
            }
        }
    }
}
