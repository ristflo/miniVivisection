// Checks added after the code review of 2026-10-06: the tab strips' show/hide, the MCP
// server over HTTP, breakpoint conditions following their lines, and the settings that
// SettingEffects reacts to.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        // --- a stand-in for the editor's tab strip (BaseTabControl + TabTray) --------------------

        private enum FakeTray
        {
            Terminal,
            Diagnostics,
            Ours1,
            Ours2,
        }

        private sealed class FakeTab
        {
            public FakeTab(FakeTray tray) => Tray = tray;

            public FakeTray Tray { get; }

            public override string ToString() => Tray.ToString();
        }

        private class FakeTabControl
        {
            private readonly object _itemsLock = new object();
            private FakeTab _selected;

            protected List<object> Items { get; } = new List<object>();

            public int Dirty;

            public FakeTray? SelectedTray
            {
                get => _selected?.Tray;
                set => _selected = Items.OfType<FakeTab>().FirstOrDefault(t => t.Tray == value) ?? _selected;
            }

            internal void MakeDirty() => Dirty++;

            public string Shown => string.Join(",", Items);

            public object Lock => _itemsLock;
        }

        private sealed class FakeTabTray : FakeTabControl
        {
            private readonly List<object> _tabs = new List<object>();

            public object Add(FakeTray tray)
            {
                var tab = new FakeTab(tray);
                _tabs.Add(tab);
                Items.Add(tab);
                return tab;
            }

            /// <summary>What the editor does on a tab group toggle: the strip anew from _tabs.</summary>
            public void Rebuild()
            {
                Items.Clear();
                Items.AddRange(_tabs);
            }
        }

        private static void TabStripChecks()
        {
            var strip = new FakeTabTray();
            strip.Add(FakeTray.Terminal);
            strip.Add(FakeTray.Diagnostics);
            var ours = new List<object> { strip.Add(FakeTray.Ours1), strip.Add(FakeTray.Ours2) };
            Check("last tab", TabStrips.LastTab(strip).ToString(), "Ours2");
            strip.SelectedTray = FakeTray.Ours1;

            TabStrips.SetVisible(strip, ours, t => t.ToString() != "Ours1", "Terminal");
            Check("hidden", strip.Shown, "Terminal,Diagnostics,Ours2");
            Check("selection moved off the hidden tab", strip.SelectedTray, FakeTray.Terminal);
            Check("marked for layout", strip.Dirty > 0, true);
            strip.Rebuild();
            Check("hidden after the editor rebuilds the strip", strip.Shown, "Terminal,Diagnostics,Ours2");

            strip.SelectedTray = FakeTray.Diagnostics;
            TabStrips.SetVisible(strip, ours, t => true, "Terminal");
            Check("shown again, in order", strip.Shown, "Terminal,Diagnostics,Ours1,Ours2");
            Check("selection kept", strip.SelectedTray, FakeTray.Diagnostics);
            TabStrips.SetVisible(strip, ours, t => false, "Terminal");
            Check("all hidden", strip.Shown, "Terminal,Diagnostics");
        }

        // --- the MCP server, over HTTP ----------------------------------------------------------

        private static void McpServerChecks()
        {
            var url = McpServer.Instance.Register("checks", "test server", new[]
            {
                new McpServer.Tool("echo", "Echoes text.", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}",
                    args => Task.FromResult("echo: " + args.GetProperty("text").GetString())),
                new McpServer.Tool("fails", "Throws.", "{\"type\":\"object\"}", args => throw new InvalidOperationException("broken tool")),
            });
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                string Post(string address, string json, out int status)
                {
                    var response = http.PostAsync(address, new StringContent(json, Encoding.UTF8, "application/json")).Result;
                    status = (int)response.StatusCode;
                    return response.Content.ReadAsStringAsync().Result;
                }
                var init = Post(url, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}", out var status);
                Check("initialize", status == 200 && init.Contains("\"name\":\"checks\"") && init.Contains("test server"), true);
                var list = Post(url, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}", out _);
                Check("tools/list", list.Contains("\"echo\"") && list.Contains("\"fails\""), true);
                var call = Post(url, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"hi\"}}}", out _);
                Check("tools/call", call.Contains("echo: hi") && call.Contains("\"isError\":false"), true);
                var failed = Post(url, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"fails\"}}", out _);
                Check("a failing tool is an error result", failed.Contains("broken tool") && failed.Contains("\"isError\":true"), true);
                var unknown = Post(url, "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"nope\"}", out _);
                Check("unknown method", unknown.Contains("-32601"), true);
                Post(url, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", out status);
                Check("notification", status, 202);
                Post(url.Substring(0, url.LastIndexOf('/') + 1) + "not-a-key", "{}", out status);
                Check("unknown endpoint", status, 404);
                Check("GET refused", (int)http.GetAsync(url).Result.StatusCode, 405);
            }
            // A huge body is refused before it is allocated: the connection closes, no answer.
            var port = new Uri(url).Port;
            using (var client = new TcpClient("127.0.0.1", port))
            using (var stream = client.GetStream())
            {
                var request = Encoding.ASCII.GetBytes("POST " + new Uri(url).AbsolutePath + " HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 2000000000\r\n\r\n");
                stream.Write(request, 0, request.Length);
                stream.ReadTimeout = 5000;
                int read;
                try
                {
                    read = stream.Read(new byte[64], 0, 64);
                }
                catch (IOException)
                {
                    read = 0;
                }
                Check("oversized body refused", read, 0);
            }
        }

        // --- IronPython 2 scripts: recognized, and their conditions through the C# evaluator ---------

        private static void IronPythonChecks()
        {
            Check("language: IronPython", ScriptKinds.FromLanguage("IronPython 2.7.12 (mcneel.ironpython.python)"), ScriptKind.IronPython);
            Check("language: Python 3", ScriptKinds.FromLanguage("Python 3.9.10 (mcneel.pythonnet.python)"), ScriptKind.Python3);
            Check("language: C#", ScriptKinds.FromLanguage("C# 9.0 (mcneel.roslyn.csharp)"), ScriptKind.CSharp);
            Check("first line: #! python2", ScriptKinds.AsksForPython2("#! python2") && ScriptKinds.AsksForPython2("#!python 2.7"), true);
            Check("first line: #! python3 / none", ScriptKinds.AsksForPython2("#! python3") || ScriptKinds.AsksForPython2("import os"), false);

            var folder = Path.Combine(Path.GetTempPath(), "minivivisection-kind-checks");
            Directory.CreateDirectory(folder);
            var ironPath = Path.Combine(folder, "old_script.py");
            File.WriteAllText(ironPath, "#! python2\nfor i in range(5):\n    x = i\n");
            var newPath = Path.Combine(folder, "new_script.py");
            File.WriteAllText(newPath, "#! python3\nx = 1\n");
            Check("file: IronPython 2", ScriptKinds.Of(new Uri(ironPath)), ScriptKind.IronPython);
            Check("file: Python 3", ScriptKinds.Of(new Uri(newPath)), ScriptKind.Python3);
            var learned = new Uri(Path.Combine(folder, "unsaved.py"));
            ScriptKinds.Learn(learned, "IronPython 2.7.12 (mcneel.ironpython.python)");
            Check("learned from the editor", ScriptKinds.Of(learned), ScriptKind.IronPython);

            var variables = new Dictionary<string, object> { ["i"] = 3, ["name"] = "abc", ["none"] = null, ["order"] = 2 };
            Check("Python spelling: and/or/not", CSharpConditions.IsTrue("i > 2 and not (name == 'x') or False", variables), true);
            Check("Python spelling: None", CSharpConditions.IsTrue("none == None and True", variables), true);
            Check("a name that starts like an operator", CSharpConditions.IsTrue("order > 1", variables), true);
            Check("Python spelling parses", CSharpConditions.SyntaxError("a and b or not c"), null);

            // A conditional breakpoint in the IronPython file: decided by the C# evaluator over
            // the frame's variables (RhinoCode's IronPython tracer gives the same frames).
            BreakpointConditions.TryInstall();
            var controls = new StandInControls();
            var uri = new Uri(ironPath);
            controls.Breakpoints.Add(new CodeReferenceBreakpoint(uri, 3));
            BreakpointSpecs.Set(uri, 3, s => s.Condition = "i % 2 == 0 and i > 0");
            var paused = new List<int>();
            for (var i = 0; i < 5; i++)
            {
                var frame = ExecFrame.OnLine(ContextIdentity.Empty, ExecThreadId.Empty, new ExecFrameId("<module>"), new CodeReference(uri, 3),
                    new[] { new ExecVariable("i", i) });
                if (NextStep.PauseOnNextBreakpoint().ShouldPause(controls, frame, null, out _))
                    paused.Add(i);
            }
            Check("IronPython condition", string.Join(",", paused), "2,4");
            BreakpointSpecs.Remove(uri, 3);
        }

        // --- breakpoint conditions follow their lines; settings that SettingEffects names ---------

        private static void SpecAndSettingChecks()
        {
            var uri = new Uri("file:///C:/checks/lines.py");
            BreakpointSpecs.Set(uri, 3, s => s.Condition = "a > 1");
            BreakpointSpecs.Set(uri, 7, s => s.LogMessage = "x={x}");
            Check("lines moved with an edit", BreakpointSpecs.SetLines(uri, new[] { 4, 9 }), true);
            Check("condition moved", BreakpointSpecs.Find(uri, 4)?.Condition, "a > 1");
            Check("log message moved", BreakpointSpecs.Find(uri, 9)?.LogMessage, "x={x}");
            Check("old lines empty", BreakpointSpecs.Find(uri, 3) == null && BreakpointSpecs.Find(uri, 7) == null, true);
            Check("a count mismatch changes nothing", BreakpointSpecs.SetLines(uri, new[] { 5 }), false);
            BreakpointSpecs.Remove(uri, 4);
            BreakpointSpecs.Remove(uri, 9);
            Check("removed", BreakpointSpecs.HasAny(uri), false);

            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\src\miniVivisection\SettingEffects.cs"));
            var keys = Regex.Matches(File.ReadAllText(source), "case \"(\\w+)\":").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            var unknown = keys.Where(k =>
            {
                try
                {
                    MiniVivisectionSettings.Find(k);
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }).ToList();
            Check("SettingEffects names " + keys.Count + " settings, all of them real", string.Join(", ", unknown), "");
        }
    }
}
