using System.Collections.Generic;
using System.Linq;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        // Scripts in Grasshopper Panels (miniVivisection.GH's ScriptPanels): the language, and
        // the colored pieces of each line.
        private static void ScriptTokenChecks()
        {
            Check("Python by its marker", ScriptTokens.Detect("#! python 3\na = 1\n"), ScriptTokens.Language.Python);
            Check("Python 2 by its marker", ScriptTokens.Detect("\n#! python 2\nprint 'x'"), ScriptTokens.Language.Python);
            Check("C# by its marker", ScriptTokens.Detect("// #! csharp\nvar a = 1;"), ScriptTokens.Language.CSharp);
            Check("C# by a Grasshopper script's first line", ScriptTokens.Detect("// Grasshopper Script Instance\nusing System;"), ScriptTokens.Language.CSharp);
            Check("Python by its lines", ScriptTokens.Detect("import math\nimport Rhino.Geometry as rg\npts = []\nfor i in range(n):\n    pts.append(i)\n"), ScriptTokens.Language.Python);
            Check("C# by its lines", ScriptTokens.Detect("using System;\nusing Rhino.Geometry;\nvar a = x * 2;\nA = a;\n"), ScriptTokens.Language.CSharp);
            Check("notes aren't a script", ScriptTokens.Detect("THE BUG\nSomething fails.\n\nTRY THIS\n1. Hover it."), ScriptTokens.Language.None);
            Check("a lesson quoting one line of code isn't a script",
                ScriptTokens.Detect("4. Fix it in V Python:\n       def angle_of(tick):\n           return 0\n   Press F5."), ScriptTokens.Language.None);
            Check("one import alone isn't enough", ScriptTokens.Detect("import this, and that"), ScriptTokens.Language.None);
            Check("numbers aren't a script", ScriptTokens.Detect("1.0\n2.5\n3.25"), ScriptTokens.Language.None);
            Check("tabs to the next 4 columns", ScriptTokens.ExpandTabs("\ta\tbc\td"), "    a   bc  d");
            Check("Markdown: notes with a heading, bold and a list", ScriptTokens.Detect("# Notes\n\nSome **bold** text.\n\n- one\n- two\n"), ScriptTokens.Language.Markdown);
            Check("Markdown: a heading and a code span", ScriptTokens.Detect("# Ring\nThe `area` of a ring."), ScriptTokens.Language.Markdown);
            Check("Markdown: a link and a quote, no heading", ScriptTokens.Detect("See [docs](https://x.org).\n\n> careful"), ScriptTokens.Language.Markdown);
            Check("not Markdown: a heading alone (a Python comment looks the same)", ScriptTokens.Detect("# compute the area\na = x * 2"), ScriptTokens.Language.None);
            Check("not Markdown: a lesson's numbered steps", ScriptTokens.Detect("TRY THIS\n1. Hover it.\n2. Fix it.\n3. Run it."), ScriptTokens.Language.None);
            Check("not Markdown: Python's x**2", ScriptTokens.Detect("d = x**2 + y**2"), ScriptTokens.Language.None);
            Check("Python with a heading comment stays Python", ScriptTokens.Detect("# Ring\nimport math\nfor i in range(3):\n    print(`x`)"), ScriptTokens.Language.Python);

            string Pieces(string text, ScriptTokens.Language language)
            {
                var lines = ScriptTokens.Lines(text);
                var tokens = ScriptTokens.Tokenize(lines, language);
                return string.Join(" | ", lines.Select((line, i) => string.Join(" ", tokens[i].Select(t => t.Kind + ":" + line.Substring(t.Start, t.Length)))));
            }

            Check("Python: keywords, a definition, a built-in, a string, a comment",
                Pieces("def ratio(step):\n    print('x', 2.5)  # why", ScriptTokens.Language.Python),
                "Keyword:def Definition:ratio | Builtin:print String:'x' Number:2.5 Comment:# why");
            Check("Python: a call, a prefixed string, a decorator, keywords",
                Pieces("@staticmethod\nreturn rg.Point3d(f\"{x}\", None) if ok else 0x1F", ScriptTokens.Language.Python),
                "Decorator:@staticmethod | Keyword:return Definition:Point3d String:f\"{x}\" Keyword:None Keyword:if Keyword:else Number:0x1F");
            Check("Python: a triple-quoted string over three lines",
                Pieces("s = \"\"\"one\ntwo\nthree\"\"\" + 'x'", ScriptTokens.Language.Python),
                "String:\"\"\"one | String:two | String:three\"\"\" String:'x'");
            Check("Python: a quote escaped in a string", Pieces("'it\\'s' # c", ScriptTokens.Language.Python), "String:'it\\'s' Comment:# c");
            Check("Python: a # inside a string is no comment", Pieces("t = '#1'", ScriptTokens.Language.Python), "String:'#1'");
            Check("C#: usings, a class, a method call, a number with a suffix",
                Pieces("using System;\npublic class Script_Instance : GH_ScriptInstance\nvar d = Math.Sqrt(2.0f); // root", ScriptTokens.Language.CSharp),
                "Keyword:using | Keyword:public Keyword:class Definition:Script_Instance Builtin:GH_ScriptInstance | Keyword:var Builtin:Math Definition:Sqrt Number:2.0f Comment:// root");
            Check("C#: a block comment over two lines, then code",
                Pieces("/* one\ntwo */ int x = 1;", ScriptTokens.Language.CSharp),
                "Comment:/* one | Comment:two */ Keyword:int Number:1");
            Check("C#: a verbatim string over two lines with a doubled quote",
                Pieces("var p = @\"C:\\a\n\"\"b\"\"\";", ScriptTokens.Language.CSharp),
                "Keyword:var String:@\"C:\\a | String:\"\"b\"\"\"");
            Check("C#: interpolated string, char, preprocessor",
                Pieces("#region Members\nPrint($\"{x}\", 'c');", ScriptTokens.Language.CSharp),
                "Decorator:#region Members | Builtin:Print String:$\"{x}\" String:'c'");
            Check("no language: no pieces", Pieces("a = 1", ScriptTokens.Language.None), "");
            Check("Markdown source: heading, list marker, bold, code, fences",
                Pieces("# Title\n- **b** `c`\n```\nx\n```", ScriptTokens.Language.Markdown),
                "Keyword:# Title | Decorator:- Definition:**b** String:`c` | Decorator:``` | String:x | Decorator:```");

            // Markdown rendered in a Panel: blocks, and runs that know their place in the source
            var markdown = "# Title\n\nSome **bold** and *it* text,\nnext line.\n\n- one\n- two `x`\n\n> quote\n\n```\ncode\n```\n---\n| a | b |\n1. first";
            var blocks = MarkdownBlocks.Parse(markdown);
            Check("Markdown: the blocks", string.Join(" ", blocks.Select(b => b.Kind + (b.Kind == MarkdownBlocks.BlockKind.Heading ? b.Level.ToString() : ""))),
                "Heading1 Paragraph ListItem ListItem Quote Code Rule Table ListItem");
            string Runs(MarkdownBlocks.Block block) => string.Join("|", block.Runs.Select(r => r.ToString()));
            Check("Markdown: a heading's text, at its column", Runs(blocks[0]), "Title@0:2");
            Check("Markdown: a paragraph's runs -- bold, italic, the lines joined",
                Runs(blocks[1]), "Some @2:0|Bold:bold@2:7| and @2:13|Italic:it@2:19| text,@2:22| @-1:0|next line.@3:0");
            Check("Markdown: a list item's code span", Runs(blocks[3]), "two @6:2|Code:x@6:7");
            Check("Markdown: a quote without its >", Runs(blocks[4]), "quote@8:2");
            Check("Markdown: code lines as they are", string.Join("|", blocks[5].Lines.Select(l => l.Text + "@" + l.Line)), "code@11");
            Check("Markdown: a numbered item's marker", blocks[8].Marker, "1.");
            Check("Markdown: a link's text at its column", string.Join("|", MarkdownBlocks.Inline("see [docs](http://x) now", 0, 0).Select(r => r.ToString())),
                "see @0:0|Link:docs@0:5| now@0:20");
            Check("Markdown: an escaped star stays", string.Join("|", MarkdownBlocks.Inline(@"a\*b*", 0, 0).Select(r => r.ToString())), "a@0:0|*b*@0:2");
            Check("Markdown: snake_case isn't italic", string.Join("|", MarkdownBlocks.Inline("my_var_name", 0, 0).Select(r => r.ToString())), "my_var_name@0:0");
            // The same through Rhino's Markdig (the Script Editor preview's parser)
            var viaMarkdig = MarkdownMarkdig.Parse(markdown);
            Check("Markdig: the same blocks -- a table needs its header rule (GitHub's rule)", string.Join(" ", viaMarkdig.Select(b => b.Kind + (b.Kind == MarkdownBlocks.BlockKind.Heading ? b.Level.ToString() : ""))),
                "Heading1 Paragraph ListItem ListItem Quote Code Rule Paragraph ListItem");
            Check("Markdig: a table with its rule", string.Join(" ", MarkdownMarkdig.Parse("| a | b |\n|---|---|\n| 1 | 2 |").Select(b => b.Kind + ":" + b.Lines.Count)), "Table:3");
            Check("Markdig: a heading's text, at its column", Runs(viaMarkdig[0]), "Title@0:2");
            Check("Markdig: a paragraph's runs, at their columns",
                Runs(viaMarkdig[1]), "Some @2:0|Bold:bold@2:7| and @2:13|Italic:it@2:19| text,@2:22| @-1:0|next line.@3:0");
            Check("Markdig: a code span", Runs(viaMarkdig[3]), "two @6:2|Code:x@6:7");
            Check("Markdig: a task list's boxes", string.Join(" ", MarkdownMarkdig.Parse("- [ ] open\n- [x] done").Select(b => b.Marker + Runs(b))),
                "☐ open@0:5 ☑ done@1:5");
            // Task boxes: where each one's ' ' or 'x' is (a click checks it), by both parsers
            const string tasks = "- [ ] open\n  - [X] nested done\n\t- [ ] after a tab\n- [x]";
            string Boxes(List<MarkdownBlocks.Block> parsed) => string.Join(" ", parsed.Where(b => b.TaskLine >= 0).Select(b => b.Marker + b.TaskLine + ":" + b.TaskColumn));
            Check("Markdig: the task boxes' places", Boxes(MarkdownMarkdig.Parse(tasks)), "☐0:3 ☑1:5 ☐2:7 ☑3:3");
            Check("Markdown: the task boxes' places", Boxes(MarkdownBlocks.Parse(tasks)), "☐0:3 ☑1:5 ☐2:7 ☑3:3");
            Check("Markdown: a task's text without its box", Runs(MarkdownBlocks.Parse("- [ ] open")[0]), "open@0:6");
            Check("Markdown: [x] in a numbered list is no task", MarkdownBlocks.Parse("1. [x] not")[0].Marker, "1.");
            Check("Task: checked", MarkdownBlocks.ToggleTask(tasks, 0, 3), tasks.Replace("- [ ] open", "- [x] open"));
            Check("Task: unchecked (X too)", MarkdownBlocks.ToggleTask(tasks, 1, 5), tasks.Replace("[X]", "[ ]"));
            Check("Task: after a tab (columns counted expanded)", MarkdownBlocks.ToggleTask(tasks, 2, 7), tasks.Replace("\t- [ ]", "\t- [x]"));
            Check("Task: the last line, CRLF", MarkdownBlocks.ToggleTask("a\r\n- [x]", 1, 3), "a\r\n- [ ]");
            Check("Task: no box there (changed since)", MarkdownBlocks.ToggleTask(tasks, 0, 4), null);
            Check("Task: a line past the end", MarkdownBlocks.ToggleTask(tasks, 9, 3), null);
            Check("Markdig: a quote in a quote, one level in", string.Join(" ", MarkdownMarkdig.Parse("> a\n>\n> > b").Select(b => b.Kind + ":" + b.Level)), "Quote:1 Quote:2");
            Check("Markdig: a footnote's mark and its text", string.Join(" | ", MarkdownMarkdig.Parse("See it[^1].\n\n[^1]: The note.").Select(b => b.Kind + " " + string.Concat(b.Runs.Select(r => r.Text)))),
                "Paragraph See it[1]. | Rule  | Paragraph [1] The note.");
            Check("Markdown: a hard line break (two spaces)", string.Join("|", MarkdownBlocks.Parse("one  \ntwo")[0].Runs.Select(r => r.ToString())), "one@0:0|\n@-1:0|two@1:0");
            // Links' targets: gh:, ghview:, rhino:, rhinoview:, web (MarkdownLinks follows them)
            string Urls(IEnumerable<MarkdownBlocks.Run> runs) => string.Join("|", runs.Where(r => r.Url != null).Select(r => r.Text + "->" + r.Url));
            Check("Markdown: a link's target", Urls(MarkdownBlocks.Inline("see [the twist](gh:Twist) now", 0, 0)), "the twist->gh:Twist");
            Check("Markdown: a target in angle brackets (spaces), a title left out", Urls(MarkdownBlocks.Inline("[a](<gh:My Twist> \"title\") [b](rhinoview:Top 'x')", 0, 0)), "a->gh:My Twist|b->rhinoview:Top");
            Check("Markdown: an autolink's target", Urls(MarkdownBlocks.Inline("at <https://x.org/a> now", 0, 0)), "https://x.org/a->https://x.org/a");
            Check("Markdown: an image is no link", Urls(MarkdownBlocks.Inline("![pic](a.png)", 0, 0)), "");
            Check("Markdig: a link's target, on every run of its text", Urls(MarkdownMarkdig.Parse("see [the **twist**](gh:Twist)")[0].Runs), "the ->gh:Twist|twist->gh:Twist");
            Check("Markdig: a target in angle brackets (spaces)", Urls(MarkdownMarkdig.Parse("[a](<ghview:My View>)")[0].Runs), "a->ghview:My View");
            Check("Markdig: an autolink and an e-mail", Urls(MarkdownMarkdig.Parse("<https://x.org> <me@x.org>")[0].Runs), "https://x.org->https://x.org|me@x.org->mailto:me@x.org");
        }
    }
}
