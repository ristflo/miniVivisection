using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MiniVivisection
{
    /// <summary>
    /// Markdown in a Grasshopper Panel ("Show as" Markdown, miniVivisection.GH's MarkdownView):
    /// the text cut into blocks -- headings, paragraphs, list items, quotes, code, tables,
    /// rules -- and their text into runs of one style (bold, italic, code, link, ...). Every run
    /// knows where its text is in the source (line, column), so a double-click on the drawing
    /// finds the place to edit. CommonMark's common part, line by line; inline markup doesn't
    /// span lines. Columns count tabs as expanded (ScriptTokens.ExpandTabs), as the editor does.
    /// Compiled into the .rhp (its checks) and linked into the .gha.
    /// </summary>
    internal static class MarkdownBlocks
    {
        public enum BlockKind
        {
            Paragraph,
            Heading,
            ListItem,
            Quote,
            Code,
            Table,
            Rule,
        }

        [Flags]
        public enum Style
        {
            None = 0,
            Bold = 1,
            Italic = 2,
            Code = 4,
            Link = 8,
            Strike = 16,
            Image = 32,
        }

        /// <summary>Text of one style; <see cref="Column"/> is where its first character is on
        /// source line <see cref="Line"/> (0-based) -- its characters follow one by one there.
        /// Line -1: a space joining two source lines; Text "\n": a hard line break. A link's runs
        /// have its target in <see cref="Url"/> (angle brackets and a title left out).</summary>
        public sealed class Run
        {
            public string Text;
            public Style Style;
            public int Line;
            public int Column;
            public string Url;

            public override string ToString() => (Style == Style.None ? "" : Style + ":") + Text + "@" + Line + ":" + Column;
        }

        public sealed class Block
        {
            public BlockKind Kind;
            public int Level;          // heading: 1-6; list item: nesting (0, 1, ...)
            public string Marker;      // list item: "•", or its number ("1."); a task: "☐" or "☑"
            public int TaskLine = -1;  // a task's box: where its ' ' or 'x' is in the source (line, column)
            public int TaskColumn;
            public int FirstLine;      // its source lines (0-based)
            public int LastLine;
            public readonly List<Run> Runs = new List<Run>();          // paragraph, heading, list item, quote
            public readonly List<(string Text, int Line)> Lines = new List<(string, int)>(); // code, table: as they are

            public override string ToString() => Kind + (Level > 0 ? Level.ToString() : "") + ": " + string.Join("", Runs.Select(r => r.Text)) + string.Join("|", Lines.Select(l => l.Text));
        }

        // The closing #s are cut off afterwards (ClosingHashes): a lazy group before an optional
        // one backtracks quadratically over a long run of blanks
        private static readonly Regex Heading = new Regex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*))?$", RegexOptions.CultureInvariant);
        // "Title ##  " -> "Title": a heading's closing #s (after a blank) and the blanks around
        private static string WithoutClosingHashes(string content)
        {
            var end = content.Length;
            while (end > 0 && (content[end - 1] == ' ' || content[end - 1] == '\t'))
                end--;
            var hashes = end;
            while (hashes > 0 && content[hashes - 1] == '#')
                hashes--;
            if (hashes < end && (hashes == 0 || content[hashes - 1] == ' ' || content[hashes - 1] == '\t'))
                end = hashes;
            while (end > 0 && (content[end - 1] == ' ' || content[end - 1] == '\t'))
                end--;
            return content.Substring(0, end);
        }

        private static readonly Regex Rule = new Regex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.CultureInvariant);
        private static readonly Regex ListItem = new Regex(@"^( *)([-*+]|\d{1,9}[.)])( +|$)", RegexOptions.CultureInvariant);
        private static readonly Regex Quote = new Regex(@"^ {0,3}> ?", RegexOptions.CultureInvariant);
        private static readonly Regex Fence = new Regex(@"^ {0,3}(```+|~~~+)", RegexOptions.CultureInvariant);
        private static readonly Regex Task = new Regex(@"^\[([ xX])\](?: +|$)", RegexOptions.CultureInvariant);

        /// <summary><paramref name="text"/> with the task box at (<paramref name="line"/>,
        /// <paramref name="column"/>; tabs expanded) -- its ' ' or 'x' between brackets -- the
        /// other way: checked, unchecked. Null when no box is there (the text changed since).</summary>
        public static string ToggleTask(string text, int line, int column)
        {
            text = text ?? "";
            var start = 0;
            for (var l = 0; l < line; l++)
            {
                var next = text.IndexOf('\n', start);
                if (next < 0)
                    return null;
                start = next + 1;
            }
            // The column, tabs expanded, to an offset in the line
            var at = 0;
            var i = start;
            while (i < text.Length && text[i] != '\n' && at < column)
            {
                at += text[i] == '\t' ? ScriptTokens.TabSize - at % ScriptTokens.TabSize : 1;
                i++;
            }
            if (at != column || i < 1 || i + 1 >= text.Length || text[i - 1] != '[' || text[i + 1] != ']')
                return null;
            var mark = text[i];
            if (mark != ' ' && mark != 'x' && mark != 'X')
                return null;
            return text.Substring(0, i) + (mark == ' ' ? 'x' : ' ') + text.Substring(i + 1);
        }

        /// <summary>The blocks of <paramref name="text"/>, in order.</summary>
        public static List<Block> Parse(string text)
        {
            var lines = ScriptTokens.Lines(text ?? "").Select(ScriptTokens.ExpandTabs).ToArray();
            var blocks = new List<Block>();
            var i = 0;
            while (i < lines.Length)
            {
                var line = lines[i];
                if (line.Trim().Length == 0)
                {
                    i++;
                    continue;
                }
                var fence = Fence.Match(line);
                if (fence.Success)
                {
                    var block = new Block { Kind = BlockKind.Code, FirstLine = i };
                    var mark = fence.Groups[1].Value;
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith(mark, StringComparison.Ordinal))
                    {
                        block.Lines.Add((lines[i], i));
                        i++;
                    }
                    block.LastLine = Math.Min(i, lines.Length - 1);
                    i++; // the closing fence
                    blocks.Add(block);
                    continue;
                }
                var heading = Heading.Match(line);
                if (heading.Success)
                {
                    var block = new Block { Kind = BlockKind.Heading, Level = heading.Groups[1].Length, FirstLine = i, LastLine = i };
                    var content = heading.Groups[2];
                    if (content.Success && WithoutClosingHashes(content.Value).Length > 0)
                        block.Runs.AddRange(Inline(WithoutClosingHashes(content.Value), i, content.Index));
                    blocks.Add(block);
                    i++;
                    continue;
                }
                if (Rule.IsMatch(line))
                {
                    blocks.Add(new Block { Kind = BlockKind.Rule, FirstLine = i, LastLine = i });
                    i++;
                    continue;
                }
                if (line.TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    var block = new Block { Kind = BlockKind.Table, FirstLine = i };
                    while (i < lines.Length && lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                    {
                        block.Lines.Add((lines[i], i));
                        i++;
                    }
                    block.LastLine = i - 1;
                    blocks.Add(block);
                    continue;
                }
                var quote = Quote.Match(line);
                if (quote.Success)
                {
                    var block = new Block { Kind = BlockKind.Quote, FirstLine = i };
                    while (i < lines.Length && lines[i].Trim().Length > 0)
                    {
                        var each = Quote.Match(lines[i]);
                        var at = each.Success ? each.Length : 0;
                        if (block.Runs.Count > 0)
                            block.Runs.Add(new Run { Text = " ", Line = -1 });
                        block.Runs.AddRange(Inline(lines[i].Substring(at), i, at));
                        i++;
                    }
                    block.LastLine = i - 1;
                    blocks.Add(block);
                    continue;
                }
                var item = ListItem.Match(line);
                if (item.Success)
                {
                    var marker = item.Groups[2].Value;
                    var block = new Block
                    {
                        Kind = BlockKind.ListItem,
                        Level = item.Groups[1].Length / 2,
                        Marker = char.IsDigit(marker[0]) ? marker.TrimEnd(')').TrimEnd('.') + "." : "•",
                        FirstLine = i,
                    };
                    var rest = item.Length;
                    var task = Task.Match(line.Substring(item.Length));
                    if (task.Success && block.Marker == "•")
                    {
                        block.Marker = task.Groups[1].Value == " " ? "☐" : "☑";
                        block.TaskLine = i;
                        block.TaskColumn = item.Length + 1;
                        rest += task.Length;
                    }
                    block.Runs.AddRange(Inline(line.Substring(rest), i, rest));
                    i++;
                    // Lines that go on with it: not blank, and starting nothing new
                    while (i < lines.Length && lines[i].Trim().Length > 0 && !StartsBlock(lines[i]))
                    {
                        AddLine(block, lines[i], i);
                        i++;
                    }
                    block.LastLine = i - 1;
                    blocks.Add(block);
                    continue;
                }
                var paragraph = new Block { Kind = BlockKind.Paragraph, FirstLine = i };
                while (i < lines.Length && lines[i].Trim().Length > 0 && (i == paragraph.FirstLine || !StartsBlock(lines[i])))
                {
                    AddLine(paragraph, lines[i], i);
                    i++;
                }
                paragraph.LastLine = i - 1;
                blocks.Add(paragraph);
            }
            return blocks;
        }

        // A line of a paragraph or list item: after a space (or a hard break: two spaces or a
        // backslash at the end of the line before), its runs.
        private static void AddLine(Block block, string line, int index)
        {
            var start = line.Length - line.TrimStart().Length;
            var content = line.Substring(start);
            if (block.Runs.Count > 0)
                block.Runs.Add(new Run { Text = HardBreakBefore(block) ? "\n" : " ", Line = -1 });
            var hard = content.EndsWith("  ", StringComparison.Ordinal) || content.EndsWith("\\", StringComparison.Ordinal);
            var trimmed = hard ? content.TrimEnd(' ').TrimEnd('\\') : content.TrimEnd();
            block.Runs.AddRange(Inline(trimmed, index, start));
            if (hard)
                block.Runs.Add(new Run { Text = "", Line = -2 }); // the next line breaks
        }

        private static bool HardBreakBefore(Block block)
        {
            var last = block.Runs[block.Runs.Count - 1];
            if (last.Line != -2)
                return false;
            block.Runs.RemoveAt(block.Runs.Count - 1);
            return true;
        }

        private static bool StartsBlock(string line) =>
            Fence.IsMatch(line) || Heading.IsMatch(line) || Rule.IsMatch(line) || Quote.IsMatch(line) ||
            line.TrimStart().StartsWith("|", StringComparison.Ordinal) || ListItem.IsMatch(line);

        /// <summary>A link's target as written between its parentheses: without angle brackets
        /// (<c>&lt;gh:My Twist&gt;</c>, for spaces) or a title (<c>url "title"</c>).</summary>
        public static string LinkTarget(string destination)
        {
            var target = (destination ?? "").Trim();
            if (target.StartsWith("<", StringComparison.Ordinal))
            {
                var close = target.IndexOf('>');
                return close > 0 ? target.Substring(1, close - 1).Trim() : target.Substring(1).Trim();
            }
            var space = target.IndexOfAny(new[] { ' ', '\t' });
            return space > 0 ? target.Substring(0, space) : target;
        }

        /// <summary>The runs of <paramref name="text"/>, which starts at column
        /// <paramref name="column"/> of source line <paramref name="line"/>.</summary>
        public static List<Run> Inline(string text, int line, int column)
        {
            var runs = new List<Run>();
            var style = Style.None;
            var buffer = new System.Text.StringBuilder();
            var bufferStart = 0;

            void Flush(int at)
            {
                if (buffer.Length > 0)
                    runs.Add(new Run { Text = buffer.ToString(), Style = style, Line = line, Column = column + bufferStart });
                buffer.Clear();
                bufferStart = at;
            }

            var n = text.Length;
            var i = 0;
            while (i < n)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < n && char.IsPunctuation(text[i + 1]) || c == '\\' && i + 1 < n && char.IsSymbol(text[i + 1]))
                {
                    Flush(i + 1);
                    buffer.Append(text[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '`')
                {
                    var ticks = 1;
                    while (i + ticks < n && text[i + ticks] == '`')
                        ticks++;
                    var close = text.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                    if (close > 0)
                    {
                        Flush(i);
                        runs.Add(new Run { Text = text.Substring(i + ticks, close - i - ticks), Style = style | Style.Code, Line = line, Column = column + i + ticks });
                        i = close + ticks;
                        bufferStart = i;
                        continue;
                    }
                }
                if ((c == '*' || c == '_') && i + 1 < n && text[i + 1] == c && Toggles(text, i, 2, style.HasFlag(Style.Bold)))
                {
                    Flush(i + 2);
                    style ^= Style.Bold;
                    i += 2;
                    continue;
                }
                if (c == '~' && i + 1 < n && text[i + 1] == '~' && Toggles(text, i, 2, style.HasFlag(Style.Strike)))
                {
                    Flush(i + 2);
                    style ^= Style.Strike;
                    i += 2;
                    continue;
                }
                if ((c == '*' || c == '_') && Toggles(text, i, 1, style.HasFlag(Style.Italic)))
                {
                    Flush(i + 1);
                    style ^= Style.Italic;
                    i += 1;
                    continue;
                }
                var image = c == '!' && i + 1 < n && text[i + 1] == '[';
                if (c == '[' || image)
                {
                    var open = image ? i + 1 : i;
                    var middle = text.IndexOf("](", open + 1, StringComparison.Ordinal);
                    var close = middle < 0 ? -1 : text.IndexOf(')', middle + 2);
                    if (close > 0)
                    {
                        Flush(i);
                        var label = text.Substring(open + 1, middle - open - 1);
                        runs.Add(new Run
                        {
                            Text = image ? "[" + label + "]" : label, Style = style | (image ? Style.Image : Style.Link), Line = line, Column = column + open + (image ? 0 : 1),
                            Url = image ? null : LinkTarget(text.Substring(middle + 2, close - middle - 2)),
                        });
                        i = close + 1;
                        bufferStart = i;
                        continue;
                    }
                }
                if (c == '<')
                {
                    var close = text.IndexOf('>', i + 1);
                    var inner = close > 0 ? text.Substring(i + 1, close - i - 1) : "";
                    if (inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        Flush(i);
                        runs.Add(new Run { Text = inner, Style = style | Style.Link, Line = line, Column = column + i + 1, Url = inner });
                        i = close + 1;
                        bufferStart = i;
                        continue;
                    }
                }
                if (buffer.Length == 0)
                    bufferStart = i;
                buffer.Append(c);
                i++;
            }
            Flush(n);
            return runs;
        }

        // Whether the mark of length at i opens (a closing one follows, and text right after it) or
        // closes (on) emphasis. '_' inside a word (snake_case) is no mark.
        private static bool Toggles(string text, int i, int length, bool on)
        {
            var mark = text.Substring(i, length);
            if (mark[0] == '_')
            {
                var before = i > 0 ? text[i - 1] : ' ';
                var after = i + length < text.Length ? text[i + length] : ' ';
                if (char.IsLetterOrDigit(before) && char.IsLetterOrDigit(after))
                    return false;
            }
            if (on)
                return i > 0 && text[i - 1] != ' ';
            if (i + length >= text.Length || text[i + length] == ' ')
                return false;
            return text.IndexOf(mark, i + length + 1, StringComparison.Ordinal) > 0;
        }
    }
}
