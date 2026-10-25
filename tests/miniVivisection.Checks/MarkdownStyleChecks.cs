// Markdown styles (MarkdownStyle): the CSS subset read -- elements, properties, lengths, colors,
// characters, body's inherited font and color --, what isn't known reported, the shipped
// defaults stamped and recognized (of any version), a default's stamp taken off a copy of one's
// own, and the defaults' values as the layouts' fallbacks.
using System;
using System.IO;
using System.Linq;

namespace MiniVivisection
{
    internal static partial class Checks
    {
        private static void MarkdownStyleChecks()
        {
            var css = MarkdownStyle.Parse(@"
/* a comment { with braces } */
body { font-family: 'Segoe UI', Arial; color: #336699; margin-bottom: 0.5em; }
h1, h2 { font-size: 2em; font-weight: bold; }
h3 { font-size: 150%; font-weight: 300; font-style: italic; }
li { list-style-type: ""\2022"" '\25E6' ""-""; padding: 0.1em 0.2em; }
task { checked: ""\2611""; unchecked: drawn; size: 0.9; }
blockquote { bar: ""|""; color: rgb(10, 20, 30); }
pre { font-family: inherit; border: none; }
a { color: royalblue; text-decoration-thickness: 8%; }
hr { bar: ""}"" ; }
");
            Check("style: nothing left out", string.Join("; ", css.Problems), "");
            Check("style: an element's length (em)", css.Length("h2", "font-size", 1), 2.0);
            Check("style: percent is em/100", css.Length("h3", "font-size", 1), 1.5);
            Check("style: a missing length: the fallback", css.Length("p", "font-size", 1.25), 1.25);
            Check("style: bold, a weight under 600 isn't", (css.Bold("h1", false), css.Bold("h3", true), css.Bold("p", true)), (true, false, true));
            Check("style: italic", (css.Italic("h3", false), css.Italic("p", false)), (true, false));
            Check("style: body's font inherited (the first of a list, unquoted)", (css.Font("p"), css.Font("pre")), ("Segoe UI", (string)null));
            Check("style: body's color inherited; an element's own", (css.ColorOf("p").ToArgb() & 0xFFFFFF, css.ColorOf("blockquote").ToArgb() & 0xFFFFFF), (0x336699, 0x0A141E));
            Check("style: a link color by name", css.LinkColor.ToArgb() & 0xFFFFFF, 0x4169E1);
            Check("style: bullets per level, the last for deeper ones", (css.Bullet(0), css.Bullet(1), css.Bullet(2), css.Bullet(5)), ("•", "◦", "-", "-"));
            Check("style: padding's two values", (css.Length("li", "padding-top", 0), css.Length("li", "padding-left", 0), css.Length("li", "padding-right", 0)), (0.1, 0.2, 0.2));
            Check("style: a task's character, or drawn (null)", (css.TaskMark(true), css.TaskMark(false)), ("☑", (string)null));
            Check("style: the quote's bar", css.QuoteBar(null), "|");
            Check("style: margin-bottom isn't inherited", css.Raw("p", "margin-bottom"), null);
            Check("style: an unknown element, property, value, and an unclosed rule reported",
                MarkdownStyle.Parse("h7 { color: red; }\np { colour: red; font-size: big; }\nli { list-style-type: x; }\na { color: red").Problems.Count, 5);
            Check("style: a problem says its line", MarkdownStyle.Parse("p { }\n\np {\n  colour: red;\n}").Problems.FirstOrDefault(), "line 4: colour: no such property");
            Check("style: the same text, the same style", ReferenceEquals(MarkdownStyle.Parse("p { }"), MarkdownStyle.Parse("p { }")), true);
            Check("style: a missing style: the fallbacks", MarkdownStyle.Parse("").Length("h1", "font-size", 1.8), 1.8);
            Check("style: list-style-type none", MarkdownStyle.Parse("li { list-style-type: none; }").Bullet(0), null);
            Check("style: bar line and none", (MarkdownStyle.Parse("blockquote { bar: line; }").QuoteBar("|"), MarkdownStyle.Parse("blockquote { bar: none; }").QuoteBar("|")), ((string)null, ""));

            // The shipped defaults: read, stamped, recognized; a copy of one's own isn't
            foreach (var target in new[] { MarkdownStyle.Target.Rhino, MarkdownStyle.Target.Grasshopper })
            {
                var text = MarkdownStyle.DefaultCss(target);
                var name = MarkdownStyle.DefaultFileName(target);
                Check(name + ": inside the assembly", text.Length > 500, true);
                Check(name + ": nothing left out", string.Join("; ", MarkdownStyle.Parse(text).Problems), "");
                var body = text.Substring(text.IndexOf('\n') + 1);
                Check(name + ": stamped with its checksum (tools/stamp_styles.py)", MarkdownStyle.IsPristineDefault(text) ? "ok" : "checksum " + MarkdownStyle.Checksum(body), "ok");
                Check(name + ": stamped as its own target", MarkdownStyle.StampedAs(text), target);
                Check(name + ": an older version's untouched copy is a default too",
                    MarkdownStyle.IsPristineDefault(Restamp(text, "p { color: red; }\n" + body)), true);
                Check(name + ": changed, it's one's own", MarkdownStyle.IsPristineDefault(text.Replace("1.8em", "2em").Replace("1.6em", "2em")), false);
                Check(name + ": CRLF line breaks: still the default", MarkdownStyle.IsPristineDefault(text.Replace("\n", "\r\n")), true);
                var own = MarkdownStyle.AsOwn(text);
                Check(name + ": saved as one's own: no stamp, the rest as it was", (MarkdownStyle.IsPristineDefault(own), own.Substring(own.IndexOf('\n') + 1) == body), (false, true));
                var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "miniVivisection", "styles", name);
                if (File.Exists(file))
                    Check(name + ": the file is the embedded text", File.ReadAllText(file).Replace("\r\n", "\n"), text);
            }
            var rhino = MarkdownStyle.Default(MarkdownStyle.Target.Rhino);
            Check("Rhino default: today's sizes", (rhino.Length("h1", "font-size", 0), rhino.Length("h2", "font-size", 0), rhino.Length("body", "margin-bottom", 0), rhino.Length("task", "size", 0)), (1.8, 1.45, 0.7, 0.85));
            Check("Rhino default: drawn task boxes, the '|' bar, the link line", (rhino.TaskMark(false), rhino.QuoteBar(null), rhino.Length("a", "text-decoration-thickness", 0), rhino.Length("a", "text-underline-offset", 0)), ((string)null, "|", 0.10, 0.15));
            var gh = MarkdownStyle.Default(MarkdownStyle.Target.Grasshopper);
            Check("Grasshopper default: a line for the quote's bar, shaded code", (gh.QuoteBar("|"), gh.Keyword("pre", "background", "")), ((string)null, "shade"));
            Check("not a default: no stamp", MarkdownStyle.IsPristineDefault("/* mine */\np { }"), false);
        }

        // A default's text with another body, stamped as tools/stamp_styles.py would
        private static string Restamp(string text, string body)
        {
            var first = text.Substring(0, text.IndexOf('\n'));
            var at = first.IndexOf("checksum ", StringComparison.Ordinal) + "checksum ".Length;
            return first.Substring(0, at) + MarkdownStyle.Checksum(body) + first.Substring(at + 16) + "\n" + body;
        }
    }
}
