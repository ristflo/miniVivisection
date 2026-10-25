// Checks for the MiniVivisection plug-in, outside Rhino (see the .csproj).
using System;
using System.Collections.Generic;
using System.Reflection;
using Eto.Forms;
using Python.Runtime;
using Rhino.Runtime.Code.Execution;
using WpfKey = System.Windows.Input.Key;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        private static int s_failures;
        private static int s_passed;

        private static void Check(string what, object actual, object expected)
        {
            var a = actual?.ToString();
            var e = expected?.ToString();
            if (a == e)
            {
                s_passed++;
                return;
            }
            s_failures++;
            Console.WriteLine("FAIL " + what + "\n     got:      " + Esc(a) + "\n     expected: " + Esc(e));
        }

        private static string Esc(string s) => (s ?? "<null>").Replace("\n", "\\n");

        private static void Section(string name, Action checks)
        {
            var before = s_failures;
            try
            {
                checks();
            }
            catch (Exception ex)
            {
                s_failures++;
                Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
            Console.WriteLine("{0,-17} {1}", name, s_failures == before ? "ok" : (s_failures - before) + " failed");
        }

        [STAThread]
        private static void Main()
        {
            // Logs of their own: Rhino's log of a running session stays as it is.
            Environment.SetEnvironmentVariable("MINIVIVISECTION_LOGDIR", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rhinovivisection", "minivivisection-checks"));
            Section("Logging", LoggingChecks);
            Section("TerminalView", TerminalViewChecks);
            Section("OptionsPage", OptionsPageChecks);
            Section("PythonBridge", PythonBridgeChecks);
            Section("CSharpConditions", CSharpConditionChecks);
            Section("Breakpoints", BreakpointChecks);
            Section("HoverScope", HoverScopeChecks);
            Section("HoverText", HoverTextChecks);
            Section("Changes", ChangesChecks);
            Section("StateToggle", StateToggleChecks);
            Section("TabStrips", TabStripChecks);
            Section("McpServer", McpServerChecks);
            Section("SpecsSettings", SpecAndSettingChecks);
            Section("Stepping", StepChecks);
            Section("OncePerLine", OncePerLineChecks);
            Section("StoppedRuns", StoppedRunChecks);
            Section("IronPython", IronPythonChecks);
            Section("FolderDrop", FolderDropChecks);
            Section("Threads", ThreadChecks);
            Section("Examples", ExampleChecks);
            Section("VNames", VNameChecks);
            Section("SlashCommands", SlashCommandChecks);
            Section("CallAt", CallAtChecks);
            Section("TraceLock", TraceLockChecks);
            Section("GhConditions", GrasshopperConditionChecks);
            Section("CodeApproval", CodeApprovalChecks);
            Section("OneShot", OneShotChecks);
            Section("Markdown", MarkdownChecks);
            Section("LessonCopies", LessonCopyChecks);
            Section("ScriptTokens", ScriptTokenChecks);
            Section("MarkdownMap", MarkdownMapChecks);
            Section("MarkdownHeading", MarkdownHeadingChecks);
            Section("LinkSchemes", LinkSchemeChecks);
            Section("DetectData", DetectDataChecks);
            Section("RtfFace", RtfFaceChecks);
            Section("MarkdownStyle", MarkdownStyleChecks);
            Section("MarkdownFields", MarkdownFieldChecks);
            Section("GhPrompts", GrasshopperPromptsChecks);
            Section("FileArchive", FileArchiveChecks);
            Console.WriteLine(s_failures == 0 ? string.Format("ALL OK ({0} checks)", s_passed) : s_failures + " FAILED");
            Console.Out.Flush();
            // pythonnet's shutdown hooks hang Environment.Exit: end the process directly,
            // with the number of failures as exit code.
            TerminateProcess(GetCurrentProcess(), (uint)s_failures);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        // --- the detailed log and the user log ---------------------------------------------

        private sealed class Counting
        {
            public int Formatted;

            public override string ToString()
            {
                Formatted++;
                return "counted";
            }
        }

        private static void LoggingChecks()
        {
            Log.Configure(LogLevel.Info, "Pause=Trace; Claude=Warning; bogus=Trace; Watch=nonsense");
            Check("info on by default", Log.IsOn(LogArea.Edit, LogLevel.Info), true);
            Check("debug off by default", Log.IsOn(LogArea.Edit, LogLevel.Debug), false);
            Check("per-area level up", Log.IsOn(LogArea.Pause, LogLevel.Trace), true);
            Check("per-area level down", Log.IsOn(LogArea.Claude, LogLevel.Info), false);
            Check("bad entries ignored", Log.IsOn(LogArea.Watch, LogLevel.Info), true);
            Check("off is never on", Log.IsOn(LogArea.Pause, LogLevel.Off), false);

            var counting = new Counting();
            Log.Debug(LogArea.Edit, "{0}", counting);
            Check("a disabled level doesn't format", counting.Formatted, 0);
            Log.Info(LogArea.Edit, "{0}", counting);
            Check("an enabled level formats once", counting.Formatted, 1);

            Log.Warning(LogArea.Pause, "pause warning");
            Log.Info(LogArea.Pause, "pause info");
            Log.Warning(LogArea.Edit, "edit warning");
            var recent = Log.Recent(10, LogLevel.Warning, LogArea.Pause);
            Check("recent by level and area", recent.Count, 1);
            Check("recent line", recent.Count == 1 && recent[0].EndsWith("Pause: pause warning"), true);
            Check("a bad format still logs", RecentContains("value {0 broken", () => Log.Warning(LogArea.Edit, "value {0 broken", 1)), true);

            Log.Configure(LogLevel.Off, "");
            Check("off: nothing on", Log.IsOn(LogArea.Edit, LogLevel.Error), false);
            Log.Configure(LogLevel.Info, "");

            UserLog.Clear();
            var added = new List<UserLogEntry>();
            UserLog.Added += added.Add;
            UserLog.Success("Wrote {0} back.", "crv");
            UserLog.Warning("A warning without Rhino.");
            UserLog.Added -= added.Add;
            Check("user log entries", UserLog.Entries.Count, 2);
            Check("user log event", added.Count, 2);
            Check("user log text", UserLog.Entries[0].Text, "Wrote crv back.");
            Check("user log kind", UserLog.Entries[1].Kind, UserLogKind.Warning);
            Check("user log also in the detailed log", Log.Recent(5).Exists(l => l.Contains("[user] Wrote crv back.")), true);
            Check("settings default without Rhino", MiniVivisectionSettings.UserLogKeep, 1000);
        }

        private static bool RecentContains(string text, Action write)
        {
            write();
            return Log.Recent(5).Exists(l => l.Contains(text));
        }

        // --- the console-style view, with real WPF input ----------------------------------------

        private static void TerminalViewChecks()
        {
            var app = Application.Instance ?? new Application(Eto.Platforms.Wpf);
            var view = new TerminalView(new object(), wrap: true) { MultilineInput = true };
            var form = new Form { Content = view, Width = 400, Height = 300 };
            form.Show();
            var text = (RichTextArea)view.Content;
            string submitted = null;
            view.Submitted += s => submitted = s;

            view.Write("banner\n");
            view.Prompt("> ");
            Check("prompt after output", view.TextNow, "banner\n> ");
            view.Input = "abc";
            Check("input set", view.TextNow, "banner\n> abc");
            view.Write("async line");
            Check("output above the prompt", view.TextNow, "banner\nasync line\n> abc");
            Check("input kept", view.Input, "abc");
            typeof(TerminalView).GetMethod("Submit", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null);
            Check("submitted", submitted, "abc");
            Check("not prompting after submit", view.IsPrompting, false);
            view.Prompt("> ");
            view.Input = "line1\nline2";
            view.Clear();
            Check("clear keeps prompt and input", view.TextNow, "> line1\nline2");
            view.Write("out");
            view.Input = "";
            var box = text.ControlObject;
            text.CaretIndex = 1;
            WpfInput.Type(box, "q");
            Check("typing in the output goes to the input", view.Input, "q");
            WpfInput.Key(box, WpfKey.Escape);
            Check("escape clears", view.Input, "");
            var before = view.TextNow;
            WpfInput.Key(box, WpfKey.Back);
            Check("backspace at the prompt", view.TextNow, before);
            WpfInput.Type(box, "ls");
            submitted = null;
            WpfInput.Key(box, WpfKey.Enter);
            Check("enter submits", submitted, "ls");
            WpfInput.Type(box, "z");
            Check("typing ignored while running", view.TextNow.EndsWith("> ls\n"), true);
            view.Prompt("> ");
            WpfInput.Key(box, WpfKey.Up);
            Check("up recalls", view.Input, "ls");

            // Tab completes V°Claude's / commands (SlashCommands), keys as typed.
            const string help = "Type a message.\n  /explain [question]     explain\n  /apply, /reject         apply or drop\n  /allow on|off           RhinoAI\n  /undo [force]           restore\n";
            view.Complete = input => SlashCommands.Complete(input, help);
            view.Input = "/ex";
            WpfInput.Key(box, WpfKey.Tab);
            Check("Tab completes a command", view.Input, "/explain ");
            view.Input = "/a";
            var shownBefore = view.TextNow.Length;
            WpfInput.Key(box, WpfKey.Tab);
            Check("several: the input stays", view.Input, "/a");
            Check("several: their help lines shown above", view.TextNow.Contains("/apply, /reject") && view.TextNow.Contains("/allow on|off") && view.TextNow.Length > shownBefore, true);
            view.Input = "/allow of";
            WpfInput.Key(box, WpfKey.Tab);
            Check("Tab completes the argument", view.Input, "/allow off");
            view.Input = "hello";
            WpfInput.Key(box, WpfKey.Tab);
            Check("a message: Tab leaves it", view.Input, "hello");
            form.Close();

            // Syntax colors: the tokenizer, code blocks, and the colors in the box.
            var tokens = string.Join(",", System.Linq.Enumerable.Select(SyntaxColors.Tokens("for i in range(3): # x\n    s = 'a'", "py"), t => t.Kind + ":" + t.Start + "+" + t.Length));
            Check("python tokens", tokens, "Keyword:0+3,Keyword:6+2,Number:15+1,Comment:19+3,String:31+3");
            var psTokens = string.Join(",", System.Linq.Enumerable.Select(SyntaxColors.Tokens("foreach ($f in 1..2) { \"x\" }", "pwsh"), t => t.Kind.ToString()));
            Check("powershell tokens", psTokens, "Keyword,Variable,Keyword,Number,Number,String");
            Check("unknown language plain", System.Linq.Enumerable.Count(SyntaxColors.Tokens("if x", "rust")), 0);
            var (blocks, next) = SyntaxColors.Fences("a\n```python\nx = 1\n```\nb\n```py\nopen", 0);
            Check("complete code blocks only", blocks.Count == 1 && blocks[0].Start == 12 && blocks[0].Length == 6 && blocks[0].Language == "python", true);
            Check("scanning goes on after the block", next, 21);

            var colored = new TerminalView(new object(), wrap: true) { InputLanguage = "python", InputColorsOn = () => true, ColorCodeBlocks = () => true };
            var coloredForm = new Form { Content = colored, Width = 400, Height = 300 };
            coloredForm.Show();
            colored.Write("Here:\n```python\nimport os\n```\n");
            colored.Prompt(">>> ");
            colored.Input = "if x: pass";
            Check("colors keep the text", colored.TextNow, "Here:\n```python\nimport os\n```\n>>> if x: pass");
            var rich = (System.Windows.Controls.RichTextBox)((RichTextArea)colored.Content).ControlObject;
            string ColorAt(int index)
            {
                // index counts line breaks as one character: walk insertion positions
                var pointer = rich.Document.ContentStart.GetInsertionPosition(System.Windows.Documents.LogicalDirection.Forward);
                for (var i = 0; i < index; i++)
                    pointer = pointer.GetNextInsertionPosition(System.Windows.Documents.LogicalDirection.Forward);
                var next = pointer.GetNextInsertionPosition(System.Windows.Documents.LogicalDirection.Forward);
                var brush = new System.Windows.Documents.TextRange(pointer, next).GetPropertyValue(System.Windows.Documents.TextElement.ForegroundProperty) as System.Windows.Media.SolidColorBrush;
                return brush == null ? "?" : brush.Color.ToString();
            }
            var keyword = SyntaxColors.ColorOf(TokenKind.Keyword, false);
            var keywordWpf = System.Windows.Media.Color.FromArgb((byte)keyword.Ab, (byte)keyword.Rb, (byte)keyword.Gb, (byte)keyword.Bb).ToString();
            Check("keyword in a code block colored", ColorAt(16), keywordWpf);
            Check("keyword in the input colored", ColorAt(34), keywordWpf);
            Check("output text plain", ColorAt(0) != keywordWpf, true);
            coloredForm.Close();
        }

        // --- the Options page: every setting has a control ---------------------------------------

        private static void OptionsPageChecks()
        {
            var page = new MiniVivisectionOptionsPage();
            Check("page control", page.PageControl is Control, true);
            var shown = System.Linq.Enumerable.Count(MiniVivisectionSettings.All, x => !x.Hidden);
            Check("a control for every shown setting", new SettingsView(immediate: false, narrow: false).Count, shown);
            Check("the left panel's too", new SettingsView(immediate: true, narrow: true).Count, shown);
            Check("the switches only in the top bar", System.Linq.Enumerable.Count(MiniVivisectionSettings.All, x => x.Hidden), 2);
            Check("keys unique", new HashSet<string>(System.Linq.Enumerable.Select(MiniVivisectionSettings.All, x => x.Key)).Count, MiniVivisectionSettings.All.Count);
        }

        // --- the paused script's Python, through Rhino's pythonnet ------------------------------

        private static void PythonBridgeChecks()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\.rhinocode\py39-rh8";
            Runtime.PythonDLL = home + @"\python39.dll";
            PythonEngine.PythonHome = home;
            PythonEngine.Initialize();

            Check("IsRunning", PythonBridge.IsRunning, true);
            Check("print", PythonBridge.Run("print(\"Test\")", false).Output, "Test\n");
            PythonBridge.Run("x = 5", false);
            Check("expression result", PythonBridge.Run("x + 1", false).Output, "6\n");
            Check("block needs more", PythonBridge.Run("for i in range(2):", false).More, true);
            Check("error", PythonBridge.Run("1/0", false).Output.Contains("ZeroDivisionError"), true);
            Check("show command", PythonBridge.Run("show(x)", false).Output, "x: shown\n");
            using (Py.GIL())
                Check("commands gone afterwards", Py.Import("builtins").HasAttr("show"), false);
            Check("cell", PythonBridge.Exec("a = 2\nb = a * 3\nb", false), "6\n");
            var extra = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minivivisection-syspath-check");
            Check("folder on the running Python's sys.path", PythonBridge.AddToSysPath(extra) && PythonBridge.AddToSysPath(extra), true);
            Check("once only", PythonBridge.Run("import sys; print(sys.path.count(" + System.Text.Json.JsonSerializer.Serialize(extra) + "))", false).Output, "1\n");

            // The drawing pipeline (python/minivivisection_visualize.py) with Rhino.Geometry's
            // structs -- managed, so they work outside Rhino.
            PythonBridge.PythonDirectory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\miniVivisection\python"));
            GC.KeepAlive(typeof(Rhino.Geometry.Point3d)); // loaded, so Python can import Rhino.Geometry
            var triple = PythonBridge.EvalInSession("[1, 2, 3]");
            Check("pipeline runs", PythonBridge.TryVisualize(triple, "auto", 100, out var how, out var parts), true);
            Check("pipeline how", how, "as point");
            Check("pipeline point", parts?.Count == 1 && parts[0].Value is Rhino.Geometry.Point3d p && p.Z == 3, true);
            PythonBridge.TryVisualize(triple, "vector", 100, out how, out parts);
            Check("pipeline arrow", parts?.Count == 1 && parts[0].Kind == "arrow" && parts[0].Value is Rhino.Geometry.Line l && l.To.Y == 2, true);
            var candidates = PythonBridge.Candidates(PythonBridge.EvalInSession("[[0, 0, 0], [1, 0, 0], [1, 1, 0]]"));
            Check("candidates", string.Join(",", candidates.ConvertAll(c => c.Id)), "points,polyline,polyline_closed,curve,vectors,raw");
            Check("candidate label", candidates.Count > 1 ? candidates[1].Label : null, "Polyline");

            var results = new List<(string, object, object)>();
            var bridgeFolder = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\miniVivisection\python"));
            PythonBridge.Exec("import sys\nsys.path.insert(0, " + System.Text.Json.JsonSerializer.Serialize(bridgeFolder) + ")", false); // rhinoviv (a copy of RhinoVivisection's bridge/rhinoviv.py)
            var scriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minivivisection_checks_visualizers.py");
            System.IO.File.WriteAllText(scriptPath,
                "import rhinoviv\n\n@rhinoviv.visualize(Node)\ndef show_node(n):\n    return [n.x, 0, 0]\n\n" +
                "@rhinoviv.inverse(Node)\ndef node_back(n, point):\n    n.x = point.X\n");
            Action paused = () =>
            {
                try
                {
                    results.Add(("raw write-back", PythonBridge.WriteBack("pts", "points",
                        new List<object> { new Rhino.Geometry.Point3d(5, 5, 5), new Rhino.Geometry.Point3d(6, 6, 6) }), "wrote 2 points"));
                    results.Add(("visualizer script", string.Join(";", PythonBridge.LoadVisualizerScripts(new[] { scriptPath })), "ok|" + scriptPath + "|node_back, show_node"));
                    PythonBridge.TryVisualize(PythonBridge.Eval("node"), "auto", 100, out var drawnHow, out _);
                    results.Add(("drawn by the visualizer", drawnHow, "by show_node"));
                    results.Add(("edit mode", PythonBridge.EditMode(PythonBridge.Eval("node"), "auto"), "inverse"));
                    results.Add(("inverse write-back", PythonBridge.WriteBack("node", "auto", new List<object> { new Rhino.Geometry.Point3d(9, 0, 0) }), "node_back() updated it in place"));
                    results.Add(("eval", PythonBridge.Eval("curves[1] + x"), 25));
                    PythonBridge.Assign("x", 42);
                    results.Add(("assign", PythonBridge.Eval("x"), 42));
                    results.Add(("run in frame", PythonBridge.Run("y = x * 2", true).Output, ""));
                    results.Add(("cell in frame", PythonBridge.Exec("z = y + 1\nz", true), "85\n"));
                    results.Add(("watch row", PythonBridge.WatchVariable("1+2")?.Id.Identifier, "1+2"));
                    results.Add(("watch row error", PythonBridge.WatchVariable("missing")?.Id.Identifier, "missing"));
                    results.Add(("variable for a value", PythonBridge.VariableFor("v", PythonBridge.Eval("curves"))?.Id.Identifier, "v"));
                    results.Add(("describe list", ValueText.Describe(PythonBridge.Eval("curves")), "Python sequence of 3:\n  [0] 10\n  [1] 20\n  [2] 30"));
                    results.Add(("describe number", ValueText.Describe(PythonBridge.Eval("x / 4")), "10.5"));
                }
                catch (Exception ex)
                {
                    results.Add(("paused", ex.GetType().Name + ": " + ex.Message, "no exception"));
                }
            };
            Func<string, object, ExecVariable> factory = (id, value) => new ExecVariable(id, value);
            using (Py.GIL())
            {
                var scope = Py.CreateScope();
                scope.Set("callback", paused.ToPython());
                scope.Set("factory", factory.ToPython());
                scope.Exec(@"
import sys

def __pynet_traceexecvar__(tracer, expandable, expander, value, id, typeId, repr, glob, nonpublic):
    return factory(id, value)

def __pynet_tracevar__(tracer, var_list, var_id, var_obj, is_global=False):
    if callable(var_obj):
        return False
    var_list.append(__pynet_traceexecvar__(tracer, False, None, var_obj, var_id, type(var_obj).__name__, str(var_obj), is_global, False))
    return True

__pynet_tracer__ = object()

class Node:
    def __init__(self):
        self.x = 0

def target():
    curves = [10, 20, 30]
    x = 5
    pts = [[0, 0, 0], [1, 0, 0]]
    node = Node()
    marker = 1   # paused here
    return x, pts, node.x

def __pynet_tracefunc__(frame, event, arg):
    if frame.f_code.co_name == 'target' and event == 'line' and frame.f_lineno == target.__code__.co_firstlineno + 5:
        callback()
    return __pynet_tracefunc__

sys.settrace(__pynet_tracefunc__)
returned = target()
sys.settrace(None)
");
                foreach (var (what, actual, expected) in results)
                    Check(what, actual, expected);
                Check("continues with the changed values", scope.Get("returned"), "(42, [[5.0, 5.0, 5.0], [6.0, 6.0, 6.0]], 9.0)");
            }
        }
    }
}
