using System;
using System.Collections.Generic;
using System.Linq;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MiniVivisection
{
    /// <summary>
    /// Markdown parsed by Markdig -- Rhino's own (Markdig.Signed.dll in Rhino's System folder),
    /// with the pipeline the Script Editor's preview uses (UseAdvancedExtensions) and precise
    /// source positions -- into <see cref="MarkdownBlocks"/>' blocks and runs, so the Panel
    /// draws it as it draws its own parser's (setting "Markdown in Panels: parser": Rhino).
    /// CommonMark's rules, nested lists and quotes, task lists (☐ ☑), footnotes, tables (as
    /// their source lines). Each run's line and column come from Markdig's positions (tabs
    /// expanded, as the editor counts). Linked into the .gha and the checks.
    /// </summary>
    internal static class MarkdownMarkdig
    {
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().UsePreciseSourceLocation().Build();

        public static List<MarkdownBlocks.Block> Parse(string text)
        {
            text = text ?? "";
            var parser = new Converter(text);
            parser.Blocks(Markdown.Parse(text, Pipeline), 0, 0);
            return parser.Result;
        }

        private sealed class Converter
        {
            private readonly string _text;
            private readonly List<int> _lineStarts = new List<int> { 0 };
            private readonly string[] _rawLines;
            public readonly List<MarkdownBlocks.Block> Result = new List<MarkdownBlocks.Block>();

            public Converter(string text)
            {
                _text = text;
                for (var i = 0; i < text.Length; i++)
                {
                    if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    if (text[i] == '\n' || text[i] == '\r')
                        _lineStarts.Add(i + 1);
                }
                _rawLines = ScriptTokens.Lines(text);
            }

            // An offset in the text: its line and column, tabs expanded
            private (int Line, int Column) At(int offset)
            {
                if (offset < 0)
                    return (-1, 0);
                var line = _lineStarts.BinarySearch(offset);
                if (line < 0)
                    line = ~line - 1;
                line = Math.Max(0, Math.Min(line, _rawLines.Length - 1));
                var raw = Math.Max(0, Math.Min(offset - _lineStarts[line], _rawLines[line].Length));
                return (line, ScriptTokens.ExpandTabs(_rawLines[line].Substring(0, raw)).Length);
            }

            private int LineOf(int offset) => offset < 0 ? 0 : At(offset).Line;

            public void Blocks(ContainerBlock container, int listLevel, int quoteDepth)
            {
                foreach (var block in container)
                    Block(block, listLevel, quoteDepth);
            }

            private void Block(Markdig.Syntax.Block block, int listLevel, int quoteDepth)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                    {
                        var result = New(MarkdownBlocks.BlockKind.Heading, block);
                        result.Level = heading.Level;
                        Inlines(heading.Inline, MarkdownBlocks.Style.None, result.Runs);
                        break;
                    }
                    case ParagraphBlock paragraph:
                    {
                        var result = New(quoteDepth > 0 ? MarkdownBlocks.BlockKind.Quote : MarkdownBlocks.BlockKind.Paragraph, block);
                        result.Level = quoteDepth;
                        Inlines(paragraph.Inline, MarkdownBlocks.Style.None, result.Runs);
                        break;
                    }
                    case ListBlock list:
                    {
                        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                        foreach (var child in list)
                        {
                            if (child is ListItemBlock item)
                                ListItem(item, list.IsOrdered ? number++ + (list.OrderedDelimiter == ')' ? ")" : ".") : "•", listLevel, quoteDepth);
                        }
                        break;
                    }
                    case QuoteBlock quote:
                        Blocks(quote, listLevel, quoteDepth + 1);
                        break;
                    case FencedCodeBlock _:
                    case CodeBlock _:
                    case HtmlBlock _:
                    {
                        var leaf = (LeafBlock)block;
                        var result = New(MarkdownBlocks.BlockKind.Code, block);
                        if (leaf.Lines.Lines != null)
                        {
                            for (var i = 0; i < leaf.Lines.Count; i++)
                            {
                                var line = leaf.Lines.Lines[i];
                                result.Lines.Add((ScriptTokens.ExpandTabs(line.Slice.ToString()), line.Line));
                            }
                        }
                        break;
                    }
                    case ThematicBreakBlock _:
                        New(MarkdownBlocks.BlockKind.Rule, block);
                        break;
                    case Table _:
                    {
                        // As its source lines, monospace (a grid: later)
                        var result = New(MarkdownBlocks.BlockKind.Table, block);
                        for (var line = result.FirstLine; line <= result.LastLine && line < _rawLines.Length; line++)
                            result.Lines.Add((ScriptTokens.ExpandTabs(_rawLines[line]), line));
                        break;
                    }
                    case FootnoteGroup footnotes:
                    {
                        New(MarkdownBlocks.BlockKind.Rule, block);
                        foreach (var footnote in footnotes.OfType<Footnote>())
                        {
                            var first = Result.Count;
                            Blocks(footnote, listLevel, quoteDepth);
                            if (Result.Count > first)
                                Result[first].Runs.Insert(0, new MarkdownBlocks.Run { Text = "[" + footnote.Order + "] ", Style = MarkdownBlocks.Style.Bold, Line = -1 });
                        }
                        break;
                    }
                    case LinkReferenceDefinitionGroup _:
                        break; // definitions: not shown
                    case ContainerBlock other:
                        Blocks(other, listLevel, quoteDepth); // custom containers, definition lists, figures, ...
                        break;
                    case LeafBlock leaf when leaf.Inline != null:
                    {
                        var result = New(MarkdownBlocks.BlockKind.Paragraph, block);
                        Inlines(leaf.Inline, MarkdownBlocks.Style.None, result.Runs);
                        break;
                    }
                    case LeafBlock leaf:
                    {
                        var result = New(MarkdownBlocks.BlockKind.Code, block); // math blocks and the like: as written
                        for (var i = 0; i < leaf.Lines.Count; i++)
                            result.Lines.Add((ScriptTokens.ExpandTabs(leaf.Lines.Lines[i].Slice.ToString()), leaf.Lines.Lines[i].Line));
                        break;
                    }
                }
            }

            // A list item: its first paragraph with the marker (a task list's box instead of a
            // bullet), the next ones without; nested lists one level in.
            private void ListItem(ListItemBlock item, string marker, int level, int quoteDepth)
            {
                var first = true;
                foreach (var child in item)
                {
                    if (child is ParagraphBlock paragraph)
                    {
                        var result = New(MarkdownBlocks.BlockKind.ListItem, child);
                        result.Level = level;
                        result.Marker = first ? marker : "";
                        var task = paragraph.Inline?.FirstChild as TaskList;
                        if (first && task != null)
                        {
                            result.Marker = task.Checked ? "☑" : "☐";
                            if (task.Span.Start >= 0)
                                (result.TaskLine, result.TaskColumn) = At(task.Span.Start + 1); // the ' ' or 'x' after '['
                        }
                        Inlines(paragraph.Inline, MarkdownBlocks.Style.None, result.Runs, skip: task);
                        first = false;
                    }
                    else if (child is ListBlock nested)
                        Block(nested, level + 1, quoteDepth);
                    else
                    {
                        if (first)
                        {
                            var empty = New(MarkdownBlocks.BlockKind.ListItem, item);
                            empty.Level = level;
                            empty.Marker = marker;
                            first = false;
                        }
                        Block(child, level + 1, quoteDepth);
                    }
                }
                if (first)
                {
                    var empty = New(MarkdownBlocks.BlockKind.ListItem, item); // "- " alone
                    empty.Level = level;
                    empty.Marker = marker;
                }
            }

            private MarkdownBlocks.Block New(MarkdownBlocks.BlockKind kind, Markdig.Syntax.Block source)
            {
                var block = new MarkdownBlocks.Block
                {
                    Kind = kind,
                    FirstLine = source.Line,
                    LastLine = source.Span.End >= 0 ? Math.Max(source.Line, LineOf(source.Span.End)) : source.Line,
                };
                Result.Add(block);
                return block;
            }

            private void Inlines(ContainerInline container, MarkdownBlocks.Style style, List<MarkdownBlocks.Run> runs, Inline skip = null)
            {
                if (container == null)
                    return;
                foreach (var inline in container)
                {
                    if (ReferenceEquals(inline, skip))
                        continue;
                    switch (inline)
                    {
                        case LiteralInline literal:
                            Add(runs, literal.Content.ToString(), style, literal.Span.Start); // the Content slice counts from the paragraph, the Span from the text
                            break;
                        case EmphasisInline emphasis:
                        {
                            var added = emphasis.DelimiterChar == '~' && emphasis.DelimiterCount >= 2 ? MarkdownBlocks.Style.Strike
                                : emphasis.DelimiterChar == '~' || emphasis.DelimiterChar == '^' || emphasis.DelimiterChar == '+' || emphasis.DelimiterChar == '=' ? MarkdownBlocks.Style.None
                                : emphasis.DelimiterCount >= 2 ? MarkdownBlocks.Style.Bold : MarkdownBlocks.Style.Italic;
                            Inlines(emphasis, style | added, runs);
                            break;
                        }
                        case CodeInline code:
                            Add(runs, code.Content, style | MarkdownBlocks.Style.Code, code.Span.Start >= 0 ? code.Span.Start + code.DelimiterCount : -1);
                            break;
                        case LinkInline link when link.IsImage:
                        {
                            var label = new List<MarkdownBlocks.Run>();
                            Inlines(link, style, label);
                            runs.Add(new MarkdownBlocks.Run
                            {
                                Text = "[" + string.Concat(label.Select(r => r.Text)) + "]",
                                Style = style | MarkdownBlocks.Style.Image,
                                Line = At(link.Span.Start).Line,
                                Column = At(link.Span.Start).Column,
                            });
                            break;
                        }
                        case LinkInline link:
                        {
                            var first = runs.Count;
                            Inlines(link, style | MarkdownBlocks.Style.Link, runs);
                            for (var i = first; i < runs.Count; i++)
                                runs[i].Url = link.Url;
                            break;
                        }
                        case AutolinkInline autolink:
                            Add(runs, autolink.Url, style | MarkdownBlocks.Style.Link, autolink.Span.Start >= 0 ? autolink.Span.Start + 1 : -1);
                            if (runs.Count > 0)
                                runs[runs.Count - 1].Url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
                            break;
                        case LineBreakInline lineBreak:
                            runs.Add(new MarkdownBlocks.Run { Text = lineBreak.IsHard ? "\n" : " ", Line = -1 });
                            break;
                        case HtmlEntityInline entity:
                            Add(runs, entity.Transcoded.ToString(), style, entity.Span.Start);
                            break;
                        case HtmlInline html:
                            Add(runs, html.Tag, style | MarkdownBlocks.Style.Code, html.Span.Start);
                            break;
                        case TaskList task:
                            Add(runs, task.Checked ? "☑ " : "☐ ", style, task.Span.Start);
                            break;
                        case FootnoteLink footnote when !footnote.IsBackLink:
                            Add(runs, "[" + footnote.Footnote.Order + "]", style | MarkdownBlocks.Style.Link, footnote.Span.Start);
                            break;
                        case ContainerInline other:
                            Inlines(other, style, runs);
                            break;
                        default:
                            // math and the like: their source text
                            if (inline.Span.Start >= 0 && inline.Span.End >= inline.Span.Start && inline.Span.End < _text.Length)
                                Add(runs, _text.Substring(inline.Span.Start, inline.Span.Length), style, inline.Span.Start);
                            break;
                    }
                }
            }

            private void Add(List<MarkdownBlocks.Run> runs, string text, MarkdownBlocks.Style style, int offset)
            {
                if (string.IsNullOrEmpty(text))
                    return;
                var (line, column) = At(offset);
                runs.Add(new MarkdownBlocks.Run { Text = text, Style = style, Line = line, Column = column });
            }
        }
    }
}
