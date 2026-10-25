// Checks added after the code review of 2026-10-15: a text part's map can't claim gigabytes,
// a heading with a long run of blanks parses in linear time, links name their file kinds and
// trust only nearby files, a Panel's data isn't mistaken for code or Markdown, and an RTF
// font name is escaped.
using System;
using System.Diagnostics;
using System.Linq;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        private static void MarkdownMapChecks()
        {
            var watch = Stopwatch.StartNew();
            Check("a map claiming 2e9 characters is no map", RhinoTextLayout.ParseMap("0:2000000000") == null, true);
            Check("  and is refused at once", watch.ElapsedMilliseconds < 100, true);
            Check("a negative count is no map", RhinoTextLayout.ParseMap("0:-5") == null, true);
            Check("a map's sum is bounded", RhinoTextLayout.ParseMap("0:600000,600000:600000") == null, true);
            var map = RhinoTextLayout.ParseMap("3:2,-1:1");
            Check("a plain map parses", map == null ? "null" : string.Join(",", map), "3,4,-1");
            Check("the map text round-trips", RhinoTextLayout.MapText(new[] { 3, 4, -1 }), "3:2,-1:1");
        }

        private static void MarkdownHeadingChecks()
        {
            var watch = Stopwatch.StartNew();
            var blocks = MarkdownBlocks.Parse("# Title" + new string(' ', 100000) + "\n\ntext");
            Check("a heading with 100 000 blanks parses in linear time", watch.ElapsedMilliseconds < 500, true);
            Check("  and is a heading", blocks.Count > 0 && blocks[0].Kind == MarkdownBlocks.BlockKind.Heading, true);
            Check("closing hashes are cut off", string.Join("", MarkdownBlocks.Parse("## Title ##  ")[0].Runs.Select(r => r.Text)), "Title");
            Check("a hash inside the title stays", string.Join("", MarkdownBlocks.Parse("# C# notes")[0].Runs.Select(r => r.Text)), "C# notes");
            Check("hashes without a blank before them stay", string.Join("", MarkdownBlocks.Parse("# Title##")[0].Runs.Select(r => r.Text)), "Title##");
            Check("an empty heading has no runs", MarkdownBlocks.Parse("# ")[0].Runs.Count, 0);
        }

        private static void LinkSchemeChecks()
        {
            Check("rhino: takes a .3dm", MarkdownLinks.FileKindMismatch("rhino", "a.3dm") == null, true);
            Check("rhino: refuses a .gh (it would run the definition)", MarkdownLinks.FileKindMismatch("rhino", "x.gh") != null, true);
            Check("gh: takes a .gh", MarkdownLinks.FileKindMismatch("gh", "x.gh") == null, true);
            Check("ghview: refuses a .3dm", MarkdownLinks.FileKindMismatch("ghview", "a.3dm") != null, true);
            Check("Split: file#Name", MarkdownLinks.Split("../a.3dm#Circle"), ("../a.3dm", "Circle"));
            Check("Split: a name alone", MarkdownLinks.Split("Circle"), ((string)null, "Circle"));
            Check("Split: a .gh alone", MarkdownLinks.Split("x.gh"), ("x.gh", ""));
            var folder = @"C:\Users\me\lessons\grasshopper";
            Check("a file in the linking folder is near", MarkdownLinks.Near(@"C:\Users\me\lessons\grasshopper\gh1.gh", folder), true);
            Check("a file one folder up is near (the lessons link up)", MarkdownLinks.Near(@"C:\Users\me\lessons\markdown_text.3dm", folder), true);
            Check("a file two folders up isn't", MarkdownLinks.Near(@"C:\Users\me\other.3dm", folder), false);
            Check("another drive isn't", MarkdownLinks.Near(@"D:\x.3dm", folder), false);
            Check("a share isn't", MarkdownLinks.Near(@"\\host\share\x.gh", folder), false);
            Check("no folder: nothing is near", MarkdownLinks.Near(@"C:\x.3dm", null), false);
        }

        private static void DetectDataChecks()
        {
            var json = "{\n  \"a\": 1,\n  \"b\": [1, 2]\n}";
            Check("a Panel's JSON data isn't C#", ScriptTokens.Detect(json, data: true), ScriptTokens.Language.None);
            Check("  (its own text: C# by its braces, as before)", ScriptTokens.Detect("{\n}\n};\n") == ScriptTokens.Language.CSharp, ScriptTokens.Detect("{\n}\n};\n") == ScriptTokens.Language.CSharp);
            Check("data strings with Markdown signs inside aren't Markdown", ScriptTokens.Detect("plain\n**x** and `y`\n[a](http://x)", data: true), ScriptTokens.Language.None);
            Check("data opening with a heading is Markdown", ScriptTokens.Detect("# Links\n\nGo to [the twist](gh:Twist).\n\n- one\n- two\n", data: true), ScriptTokens.Language.Markdown);
            Check("data opening with a list is Markdown", ScriptTokens.Detect("- [a](gh:A)\n- **b**\n", data: true), ScriptTokens.Language.Markdown);
            Check("data with the Python marker is Python", ScriptTokens.Detect("#! python 3\nimport math\n", data: true), ScriptTokens.Language.Python);
            Check("data of Python lines isn't Python (no marker)", ScriptTokens.Detect("import math\nimport os\ndef f():\n    pass\n", data: true), ScriptTokens.Language.None);
        }

        private static void RtfFaceChecks()
        {
            Check("braces in a font name are escaped", RhinoTextLayout.Escape("A{B}C"), @"A\{B\}C");
            Check("a backslash is escaped", RhinoTextLayout.Escape(@"a\b"), @"a\\b");
            Check("a non-ASCII name is written as \\u", RhinoTextLayout.Escape("Ü"), @"\u220?");
            Check("a plain name is as it is", RhinoTextLayout.Escape("Segoe UI"), "Segoe UI");
        }
    }
}
