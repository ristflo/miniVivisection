using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Text.RegularExpressions;
using Grasshopper.Kernel;
using MiniVivisection;

namespace MiniVivisection.GH
{
    /// <summary>
    /// A Panel shown as Markdown ("Show as" Markdown): its text laid out as rendered Markdown in
    /// the Panel's width -- headings, paragraphs, lists, quotes, code, tables, rules; bold,
    /// italic, code, links -- and drawn on the canvas (ScriptPanels), as the Grasshopper Markdown
    /// style says (MarkdownStyle: sizes in em of the Panel's font size, fonts, colors, bullets,
    /// task boxes, the quote bar). Each placed piece of text keeps where it is in the source
    /// (MarkdownBlocks' runs), so a double-click on the drawing opens the source in the code box
    /// at that very place (<see cref="SourceAt"/>). A task's box (drawn: a square, a check mark;
    /// or the style's character) is clicked like a link: checked or unchecked.
    /// </summary>
    internal sealed class MarkdownView
    {
        private sealed class Piece
        {
            public RectangleF Bounds;  // in the layout (0, 0: the top left of the text area)
            public string Text;
            public Font Font;
            public MarkdownBlocks.Style Style;
            public int Line;           // -1: a joining space
            public int Column;
            public int Row;
            public string Url;         // a link's target
            public Color Color;        // its block's (Color.Empty: the Panel's text color)
        }

        private sealed class Row
        {
            public float Top, Bottom;
            public int Line;           // its source line, for a click beside its text
            public readonly List<Piece> Pieces = new List<Piece>();
        }

        private enum MarkKind
        {
            Bullet,     // a character: a bullet, a number, a task's character, a quote's bar character
            Rule,
            QuoteBar,
            CodeBack,
            CodeFrame,
            HeadingRule,
        }

        private sealed class Mark
        {
            public MarkKind Kind;
            public RectangleF Bounds;
            public string Text;
            public Font Font;
            public Color Color;
            public bool Table;         // a CodeBack of a table
        }

        private readonly List<Piece> _pieces = new List<Piece>();
        private readonly List<Row> _rows = new List<Row>();
        private readonly List<Mark> _marks = new List<Mark>();

        // The task boxes: where each is, where its ' ' or 'x' is in the source, checked or not;
        // drawn here (a square), or a character (a Mark)
        private readonly List<(RectangleF Box, int Line, int Column, bool Done, bool Drawn, Color Color)> _tasks = new List<(RectangleF, int, int, bool, bool, Color)>();

        /// <summary>A task box under the mouse, as LinkAt has it: task:line:column:done (0-based,
        /// done 1 when checked) -- a click checks or unchecks it (ScriptPanels).</summary>
        public const string TaskScheme = "task:";

        /// <summary>A task "link"'s place in the source and whether it's checked; null: no task.</summary>
        public static (int Line, int Column, bool Done)? TaskOf(string url)
        {
            var fields = url != null && url.StartsWith(TaskScheme, StringComparison.Ordinal) ? url.Substring(TaskScheme.Length).Split(':') : new string[0];
            return fields.Length == 3 && int.TryParse(fields[0], out var line) && int.TryParse(fields[1], out var column)
                ? (line, column, fields[2] == "1") : ((int, int, bool)?)null;
        }

        public string Text { get; private set; }

        public float Width { get; private set; }

        public float FontSize { get; private set; }

        /// <summary>Parsed by Markdig (Rhino's), or by miniVivisection's own parser.</summary>
        public bool Markdig { get; private set; }

        /// <summary>The style it was laid out in.</summary>
        public MarkdownStyle Style { get; private set; }

        /// <summary>The fields' state it was laid out with (MarkdownFields.State.Signature; null: none).</summary>
        public string Fields { get; private set; }

        /// <summary>The base font's family and style, and the DPI, it was laid out with.</summary>
        public string FontName { get; private set; }
        public FontStyle FontStyle { get; private set; }
        public float DpiX => _dpiX;

        public float Height { get; private set; }

        private float _dpiX = 96, _dpiY = 96; // the canvas's: a click measures as the layout did

        /// <summary>Where the text area was at the last drawing (canvas coordinates) and how far
        /// it was scrolled: for a click.</summary>
        public PointF Origin;
        public float ScrolledBy;

        private static readonly StringFormat Measure = MakeMeasure();

        private static StringFormat MakeMeasure()
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            return format;
        }

        private static readonly Dictionary<(string, float, FontStyle), Font> s_fonts = new Dictionary<(string, float, FontStyle), Font>();

        private static Font FontOf(FontFamily family, float size, FontStyle style)
        {
            lock (s_fonts)
            {
                var key = (family.Name, (float)Math.Round(size, 2), style);
                if (!s_fonts.TryGetValue(key, out var font))
                {
                    try
                    {
                        s_fonts[key] = font = new Font(family, size, style);
                    }
                    catch (ArgumentException)
                    {
                        s_fonts[key] = font = new Font(family, size, FontStyle.Regular); // a family without that style
                    }
                }
                return font;
            }
        }

        private static readonly Dictionary<string, FontFamily> s_families = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        // A font family by its name (the style's); fallback when there's none of that name
        private static FontFamily FamilyOf(string name, FontFamily fallback)
        {
            if (string.IsNullOrWhiteSpace(name))
                return fallback;
            lock (s_families)
            {
                if (!s_families.TryGetValue(name, out var family))
                {
                    try
                    {
                        family = new FontFamily(name);
                    }
                    catch (ArgumentException)
                    {
                        family = null; // not installed
                    }
                    s_families[name] = family;
                }
                return family ?? fallback;
            }
        }

        /// <summary>The layout of <paramref name="text"/> in <paramref name="width"/>, in the Panel's
        /// font <paramref name="baseFont"/>, parsed by Markdig (<paramref name="markdig"/>: Rhino's,
        /// as the Script Editor's preview) or by miniVivisection's own parser, drawn as
        /// <paramref name="style"/> says (null: the Grasshopper default).</summary>
        public static MarkdownView Layout(Graphics g, string text, float width, Font baseFont, bool markdig, MarkdownStyle style = null, MarkdownFields.State fields = null)
        {
            var view = new MarkdownView
            {
                Text = text, Width = width, FontSize = baseFont.SizeInPoints, FontName = baseFont.Name, FontStyle = baseFont.Style, Markdig = markdig,
                Style = style ?? MarkdownStyle.Default(MarkdownStyle.Target.Grasshopper), Fields = fields?.Signature,
                _dpiX = g.DpiX, _dpiY = g.DpiY,
            };
            var blocks = Parse(text, markdig);
            view.Build(g, fields == null ? blocks : MarkdownFields.Expand(blocks, fields), width, baseFont);
            return view;
        }

        private static bool s_reportedMarkdig;

        private static List<MarkdownBlocks.Block> Parse(string text, bool markdig)
        {
            if (markdig)
            {
                try
                {
                    return MarkdownMarkdig.Parse(text);
                }
                catch (Exception ex)
                {
                    // Markdig missing or changed: the plug-in's own parser
                    if (!s_reportedMarkdig)
                    {
                        s_reportedMarkdig = true;
                        Rhino.RhinoApp.WriteLine("miniVivisection: Markdown in Panels: Rhino's Markdig couldn't parse ({0}) -- miniVivisection's parser instead.", ex.Message);
                    }
                }
            }
            return MarkdownBlocks.Parse(text);
        }

        // A block's element in the style
        private static string ElementOf(MarkdownBlocks.Block block)
        {
            switch (block.Kind)
            {
                case MarkdownBlocks.BlockKind.Heading:
                    return MarkdownStyle.Heading(block.Level);
                case MarkdownBlocks.BlockKind.ListItem:
                    return "li";
                case MarkdownBlocks.BlockKind.Quote:
                    return "blockquote";
                case MarkdownBlocks.BlockKind.Code:
                    return "pre";
                case MarkdownBlocks.BlockKind.Table:
                    return "table";
                case MarkdownBlocks.BlockKind.Rule:
                    return "hr";
                default:
                    return "p";
            }
        }

        private void Build(Graphics g, List<MarkdownBlocks.Block> blocks, float width, Font baseFont)
        {
            var css = Style;
            var baseFamily = FamilyOf(css.Font("body"), baseFont.FontFamily);
            var size = baseFont.SizeInPoints;
            var spacing = baseFamily.GetLineSpacing(baseFont.Style);
            var line = baseFont.GetHeight(g);
            var em = spacing > 0 ? line * baseFamily.GetEmHeight(baseFont.Style) / spacing : line / 1.2f; // the font size, in the layout's units
            float Em(string element, string property, double fallback) => em * (float)css.Length(element, property, fallback);

            // A block's font: its family, size and weight as the style says (the Panel's font's style else)
            Font BlockFont(string element, double scale, bool bold, bool italic)
            {
                var fontStyle = baseFont.Style & ~(FontStyle.Bold | FontStyle.Italic);
                if (css.Bold(element, bold || baseFont.Bold))
                    fontStyle |= FontStyle.Bold;
                if (css.Italic(element, italic || baseFont.Italic))
                    fontStyle |= FontStyle.Italic;
                var family = FamilyOf(css.Font(element), baseFamily);
                var points = size * (float)css.Length(element, "font-size", scale);
                return family.Name == baseFont.FontFamily.Name && Math.Abs(points - size) < 0.01f && fontStyle == baseFont.Style ? baseFont : FontOf(family, points, fontStyle);
            }

            var consoleFamily = GH_FontServer.FamilyConsole;
            var code = FontOf(FamilyOf(css.Font("code"), consoleFamily), size * (float)css.Length("code", "font-size", 0.95), FontStyle.Regular);
            var gap = css.Length("body", "margin-bottom", 0.42);
            var y = 0f;
            var first = true;
            foreach (var block in blocks)
            {
                var element = ElementOf(block);
                var color = css.ColorOf(element);
                switch (block.Kind)
                {
                    case MarkdownBlocks.BlockKind.Heading:
                    {
                        var font = BlockFont(element, block.Level == 1 ? 1.6 : block.Level == 2 ? 1.35 : block.Level == 3 ? 1.15 : 1, true, false);
                        if (!first)
                            y += Em(element, "margin-top", 0.6);
                        y = Flow(g, block.Runs, 0, width, y, font, font.FontFamily, code, block.FirstLine, font, color);
                        if (css.Keyword(element, "border-bottom", block.Level <= 2 ? "line" : "none") == "line")
                        {
                            y += Em(element, "padding-bottom", 0.15);
                            _marks.Add(new Mark { Kind = MarkKind.HeadingRule, Bounds = new RectangleF(0, y, width, 1), Color = color });
                            y += 1;
                        }
                        break;
                    }
                    case MarkdownBlocks.BlockKind.Paragraph:
                    {
                        var font = BlockFont(element, 1, false, false);
                        y = Flow(g, block.Runs, 0, width, y, font, font.FontFamily, code, block.FirstLine, null, color);
                        break;
                    }
                    case MarkdownBlocks.BlockKind.ListItem:
                    {
                        var font = BlockFont(element, 1, false, false);
                        var rowLine = font.GetHeight(g);
                        var indent = Em("li", "margin-left", 0.48) + Em("li", "padding-left", 1.44) * block.Level;
                        var task = block.Marker == "☐" || block.Marker == "☑";
                        var done = block.Marker == "☑";
                        // The marker: a task's box (drawn, or the style's character), the style's bullet, or the number
                        var marker = task ? css.TaskMark(done) : block.Marker == "•" ? css.Bullet(block.Level) : block.Marker;
                        var markerWidth = task ? Em("task", "marker-width", 1.32)
                            : Math.Max(g.MeasureString((marker ?? "") + " ", font, PointF.Empty, Measure).Width, Em("li", "marker-width", 1.08));
                        var bulletAt = indent;
                        var textAt = indent + markerWidth;
                        var top = y;
                        y = Flow(g, block.Runs, textAt, width, y, font, font.FontFamily, code, block.FirstLine, null, color);
                        if (task && marker == null)
                            _tasks.Add((TaskBox(font, rowLine, bulletAt, top, Em("task", "size", 0.6)), block.TaskLine, block.TaskColumn, done, true, color));
                        else if (!string.IsNullOrEmpty(marker))
                        {
                            var bounds = new RectangleF(bulletAt, top, textAt - bulletAt, rowLine);
                            _marks.Add(new Mark { Kind = MarkKind.Bullet, Bounds = bounds, Text = marker, Font = font, Color = color });
                            if (task)
                                _tasks.Add((new RectangleF(bulletAt, top, Math.Max(1f, g.MeasureString(marker, font, PointF.Empty, Measure).Width), rowLine), block.TaskLine, block.TaskColumn, done, false, color));
                        }
                        AddEmptyRowIfNone(top, y == top ? top + rowLine : y, block.FirstLine);
                        if (y == top)
                            y += rowLine;
                        break;
                    }
                    case MarkdownBlocks.BlockKind.Quote:
                    {
                        var top = y;
                        var font = BlockFont(element, 1, false, true);
                        var depth = Math.Max(1, block.Level); // a quote in a quote: one step in
                        var step = Em(element, "padding-left", 1.08);
                        var firstRow = _rows.Count;
                        y = Flow(g, block.Runs, step * depth, width, y, font, font.FontFamily, code, block.FirstLine, null, color, quote: true);
                        // The bar: a line, a character on each of its rows, or none
                        var bar = css.QuoteBar(null);
                        var at = Em(element, "margin-left", 0.3);
                        var barFont = bar == null ? null : FontOf(font.FontFamily, font.SizeInPoints, css.Bold(element, true, "bar-weight") ? FontStyle.Bold : FontStyle.Regular);
                        for (var d = 0; d < depth; d++)
                        {
                            if (bar == null)
                                _marks.Add(new Mark { Kind = MarkKind.QuoteBar, Bounds = new RectangleF(at + d * step, top, Math.Max(2f, Em(element, "bar-width", 0.18)), y - top), Color = color });
                            else if (bar.Length > 0)
                            {
                                for (var r = firstRow; r < _rows.Count; r++)
                                    _marks.Add(new Mark { Kind = MarkKind.Bullet, Bounds = new RectangleF(at + d * step, _rows[r].Top, step, _rows[r].Bottom - _rows[r].Top), Text = bar, Font = barFont, Color = color });
                            }
                        }
                        break;
                    }
                    case MarkdownBlocks.BlockKind.Code:
                    case MarkdownBlocks.BlockKind.Table:
                    {
                        var codeFont = FontOf(FamilyOf(css.Font(element), consoleFamily), size * (float)css.Length(element, "font-size", 0.95), FontStyle.Regular);
                        var codeLine = codeFont.GetHeight(g);
                        var top = y;
                        var pad = Em(element, "padding-left", 0.34);
                        y += Em(element, "padding-top", 0.34);
                        foreach (var (text, source) in block.Lines)
                        {
                            var row = NewRow(y, y + codeLine, source);
                            if (text.Length > 0)
                                AddPiece(row, new RectangleF(pad, y, g.MeasureString(text, codeFont, PointF.Empty, Measure).Width, codeLine), text, codeFont, MarkdownBlocks.Style.Code, source, 0, null, color);
                            y += codeLine;
                        }
                        if (block.Lines.Count == 0)
                        {
                            NewRow(y, y + codeLine, block.FirstLine);
                            y += codeLine;
                        }
                        y += Em(element, "padding-bottom", 0.34);
                        var table = block.Kind == MarkdownBlocks.BlockKind.Table;
                        var box = new RectangleF(0, top, width, y - top);
                        if (css.Keyword(element, "background", "shade") == "shade")
                            _marks.Add(new Mark { Kind = MarkKind.CodeBack, Bounds = box, Table = table });
                        if (css.Keyword(element, "border", "none") == "frame")
                            _marks.Add(new Mark { Kind = MarkKind.CodeFrame, Bounds = box, Color = color });
                        break;
                    }
                    case MarkdownBlocks.BlockKind.Rule:
                    {
                        var top = y;
                        y += Em(element, "margin-top", 0.6);
                        _marks.Add(new Mark { Kind = MarkKind.Rule, Bounds = new RectangleF(0, y, width, 1), Color = color });
                        NewRow(top, y + 1, block.FirstLine);
                        break;
                    }
                }
                y += Em(element, "margin-bottom", block.Kind == MarkdownBlocks.BlockKind.Rule ? gap + 0.6 : gap);
                first = false;
            }
            Height = Math.Max(line, y);
        }

        // A task's box (as in Rhino): a square side long, centered on the capitals of font, at x
        // in the row from top
        private static RectangleF TaskBox(Font font, float line, float x, float top, float side)
        {
            var family = font.FontFamily;
            var spacing = family.GetLineSpacing(font.Style);
            var ascent = spacing > 0 ? line * family.GetCellAscent(font.Style) / spacing : line * 0.8f;
            var capitals = spacing > 0 ? line * family.GetEmHeight(font.Style) / spacing * 0.7f : line * 0.55f;
            return new RectangleF(x, top + ascent - capitals / 2 - side / 2, side, side);
        }

        private Row NewRow(float top, float bottom, int line)
        {
            var row = new Row { Top = top, Bottom = bottom, Line = line };
            _rows.Add(row);
            return row;
        }

        private void AddEmptyRowIfNone(float top, float bottom, int line)
        {
            if (!_rows.Any(r => r.Top >= top - 0.01f && r.Top < bottom))
                NewRow(top, bottom, line);
        }

        private void AddPiece(Row row, RectangleF bounds, string text, Font font, MarkdownBlocks.Style style, int line, int column, string url, Color color)
        {
            var piece = new Piece { Bounds = bounds, Text = text, Font = font, Style = style, Line = line, Column = column, Row = _rows.IndexOf(row), Url = url, Color = color };
            row.Pieces.Add(piece);
            _pieces.Add(piece);
        }

        private static readonly Regex Words = new Regex(@"\S+\s*|\s+", RegexOptions.CultureInvariant);

        // Runs flowed into rows from x = left to width, words wrapped; returns the y below them.
        private float Flow(Graphics g, List<MarkdownBlocks.Run> runs, float left, float width, float y, Font font, FontFamily family,
            Font code, int firstLine, Font fixedFont, Color color, bool quote = false)
        {
            var rowHeight = font.GetHeight(g);
            var row = NewRow(y, y + rowHeight, firstLine);
            var x = left;
            var lastLine = firstLine;
            foreach (var run in runs)
            {
                if (run.Text == "\n")
                {
                    y = row.Bottom;
                    row = NewRow(y, y + rowHeight, lastLine + 1);
                    x = left;
                    continue;
                }
                if (string.IsNullOrEmpty(run.Text))
                    continue;
                var runFont = RunFont(run.Style, font, family, code, fixedFont);
                var height = runFont.GetHeight(g);
                foreach (Match word in Words.Matches(run.Text))
                {
                    var text = word.Value;
                    var full = g.MeasureString(text, runFont, PointF.Empty, Measure).Width;
                    var ink = text.TrimEnd().Length == text.Length ? full : g.MeasureString(text.TrimEnd(), runFont, PointF.Empty, Measure).Width;
                    if (x > left && x + ink > width && text.Trim().Length > 0)
                    {
                        y = row.Bottom;
                        row = NewRow(y, y + rowHeight, run.Line >= 0 ? run.Line : lastLine);
                        x = left;
                    }
                    if (x == left && text.Trim().Length == 0)
                        continue; // no space at a row's start
                    if (height > row.Bottom - row.Top)
                        row.Bottom = row.Top + height;
                    AddPiece(row, new RectangleF(x, row.Top, full, height), text, runFont, run.Style, run.Line, run.Column + word.Index, run.Url, color);
                    x += full;
                }
                if (run.Line >= 0)
                    lastLine = run.Line;
            }
            return row.Bottom;
        }

        private static Font RunFont(MarkdownBlocks.Style style, Font font, FontFamily family, Font code, Font fixedFont)
        {
            if (style.HasFlag(MarkdownBlocks.Style.Code))
                return code;
            if (fixedFont != null)
                return fixedFont; // a heading: its own
            var fontStyle = font.Style;
            if (style.HasFlag(MarkdownBlocks.Style.Bold))
                fontStyle |= FontStyle.Bold;
            if (style.HasFlag(MarkdownBlocks.Style.Italic) || style.HasFlag(MarkdownBlocks.Style.Image))
                fontStyle |= FontStyle.Italic;
            if (style.HasFlag(MarkdownBlocks.Style.Strike))
                fontStyle |= FontStyle.Strikeout;
            return fontStyle == font.Style ? font : FontOf(family, font.SizeInPoints, fontStyle);
        }

        // --- drawing ----------------------------------------------------------------------------

        /// <summary>Drawn at <paramref name="origin"/> (the text area's top left on the canvas),
        /// scrolled up by <paramref name="scrolledBy"/>.</summary>
        public void Draw(Graphics g, PointF origin, float scrolledBy, RectangleF clip, Color fore, Color back, ScriptPanels.Palette palette)
        {
            Origin = origin;
            ScrolledBy = scrolledBy;
            float X(float x) => origin.X + x;
            float Y(float y) => origin.Y + y - scrolledBy;
            Color Of(Color color) => color.IsEmpty ? fore : color;
            var dark = ScriptPanels.IsDark(back);
            float scale;
            using (var transform = g.Transform)
                scale = Math.Abs(transform.Elements[0]);
            var pixel = scale > 0 ? 1f / scale : 1f;
            foreach (var mark in _marks)
            {
                var bounds = mark.Bounds;
                var r = new RectangleF(X(bounds.X), Y(bounds.Y), bounds.Width, bounds.Height);
                if (r.Bottom < clip.Top || r.Top > clip.Bottom)
                    continue;
                switch (mark.Kind)
                {
                    case MarkKind.CodeBack:
                        using (var fill = new SolidBrush(Color.FromArgb(dark ? 40 : 28, fore)))
                            g.FillRectangle(fill, r);
                        break;
                    case MarkKind.CodeFrame:
                        using (var pen = new Pen(Color.FromArgb(120, Of(mark.Color)), pixel))
                            g.DrawRectangle(pen, r.X, r.Y, r.Width - pixel, r.Height);
                        break;
                    case MarkKind.QuoteBar:
                        using (var bar = new SolidBrush(Color.FromArgb(110, Of(mark.Color))))
                            g.FillRectangle(bar, r);
                        break;
                    case MarkKind.Rule:
                    case MarkKind.HeadingRule:
                        using (var pen = new Pen(Color.FromArgb(mark.Kind == MarkKind.Rule ? 120 : 60, Of(mark.Color))))
                            g.DrawLine(pen, r.Left, r.Top, r.Right, r.Top);
                        break;
                    case MarkKind.Bullet:
                        using (var brush = new SolidBrush(Of(mark.Color)))
                            g.DrawString(mark.Text, mark.Font, brush, r.X, r.Y, ScriptPanels.Typographic);
                        break;
                }
            }
            // The drawn task boxes: square, sharp corners; a check mark in it when done -- strokes as a glyph's, at least a pixel
            foreach (var (box, _, _, done, drawn, color) in _tasks)
            {
                var r = new RectangleF(X(box.X), Y(box.Y), box.Width, box.Height);
                if (!drawn || r.Bottom < clip.Top || r.Top > clip.Bottom)
                    continue;
                using var pen = new Pen(Of(color), Math.Max(pixel, box.Width * 0.09f)) { LineJoin = LineJoin.Miter };
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                if (done)
                {
                    PointF At(float u, float v) => new PointF(r.X + u * r.Width, r.Bottom - v * r.Height);
                    g.DrawLines(pen, new[] { At(0.2f, 0.52f), At(0.42f, 0.26f), At(0.8f, 0.76f) });
                }
            }
            var brushes = new Dictionary<Color, SolidBrush>();
            var linkColor = ScriptPanels.LinksColored ? Style.LinkColor : Color.Empty;
            var underline = Style.Keyword("a", "text-decoration", "underline") != "none";
            var codeShade = Style.Keyword("code", "background", "shade") == "shade";
            try
            {
                for (var i = 0; i < _pieces.Count; i++)
                {
                    var piece = _pieces[i];
                    var r = new RectangleF(X(piece.Bounds.X), Y(piece.Bounds.Y), piece.Bounds.Width, piece.Bounds.Height);
                    if (r.Bottom < clip.Top || r.Top > clip.Bottom || r.Left > clip.Right)
                        continue;
                    var link = piece.Style.HasFlag(MarkdownBlocks.Style.Link);
                    var text = Of(piece.Color);
                    var color = link ? (linkColor.IsEmpty ? text : linkColor) // "Markdown links in color" off: the text's
                        : piece.Style.HasFlag(MarkdownBlocks.Style.Image) ? Color.FromArgb(150, text)
                        : text;
                    if (codeShade && piece.Style.HasFlag(MarkdownBlocks.Style.Code) && !IsBlockCode(piece))
                    {
                        using (var codeBack = new SolidBrush(Color.FromArgb(dark ? 45 : 30, fore)))
                            g.FillRectangle(codeBack, r.X, r.Y, g.MeasureString(piece.Text.TrimEnd(), piece.Font, PointF.Empty, Measure).Width, r.Height);
                    }
                    if (!brushes.TryGetValue(color, out var brush))
                        brushes[color] = brush = new SolidBrush(color);
                    g.DrawString(piece.Text, piece.Font, brush, r.X, r.Y, ScriptPanels.Typographic);
                    if (link && underline)
                    {
                        // Its bar (as in Rhino): to the next piece of the same link, else to its last letter
                        var next = i + 1 < _pieces.Count ? _pieces[i + 1] : null;
                        var joined = next != null && next.Row == piece.Row && next.Url == piece.Url && next.Style.HasFlag(MarkdownBlocks.Style.Link);
                        LinkBar(g, piece, r, brush, joined ? X(next.Bounds.X) - r.X : g.MeasureString(piece.Text.TrimEnd(), piece.Font, PointF.Empty, Measure).Width, pixel);
                    }
                }
            }
            finally
            {
                foreach (var brush in brushes.Values)
                    brush.Dispose();
            }
        }

        // A link's bar under its words, as the style says: its thickness (by default 10 %) and its
        // top under the baseline (15 %) of the text's capitals' height -- about 0.7 of the font's
        // size, as Rhino's text height --, at least a pixel high
        private void LinkBar(Graphics g, Piece piece, RectangleF r, Brush brush, float width, float pixel)
        {
            if (!(width > 0))
                return;
            var family = piece.Font.FontFamily;
            var style = piece.Font.Style;
            var line = piece.Font.GetHeight(g);
            var spacing = family.GetLineSpacing(style);
            if (!(line > 0) || spacing <= 0)
                return;
            var ascent = line * family.GetCellAscent(style) / spacing;
            var textHeight = line * family.GetEmHeight(style) / spacing * 0.7f;
            var thick = Math.Max(textHeight * (float)Style.Length("a", "text-decoration-thickness", RhinoTextLayout.BarHeight), pixel);
            var drop = textHeight * (float)Style.Length("a", "text-underline-offset", RhinoTextLayout.BarGap);
            g.FillRectangle(brush, r.X, r.Y + ascent + drop, width, thick);
        }

        // A piece of a code block (or table) -- its row holds just it, at the block's padding.
        private bool IsBlockCode(Piece piece) => _marks.Any(m => (m.Kind == MarkKind.CodeBack || m.Kind == MarkKind.CodeFrame) && m.Bounds.Contains(piece.Bounds.X + 0.5f, piece.Bounds.Y + 0.5f)) &&
                                                  _rows[piece.Row].Pieces.Count == 1 && piece.Column == 0;

        // --- a click ----------------------------------------------------------------------------

        /// <summary>For a test: where the first piece of a link to <paramref name="url"/> was drawn
        /// last (canvas coordinates); empty: none.</summary>
        internal RectangleF LinkPlace(string url)
        {
            var piece = _pieces.FirstOrDefault(p => p.Url == url);
            return piece == null ? RectangleF.Empty : new RectangleF(Origin.X + piece.Bounds.X, Origin.Y + piece.Bounds.Y - ScrolledBy, piece.Bounds.Width, piece.Bounds.Height);
        }

        /// <summary>For a test: where the task boxes were drawn last (canvas coordinates), with their
        /// task links (task:line:column:done) and whether they're drawn (else a character).</summary>
        internal List<(RectangleF Box, string Url, bool Drawn)> TaskPlaces() =>
            _tasks.Select(t => (new RectangleF(Origin.X + t.Box.X, Origin.Y + t.Box.Y - ScrolledBy, t.Box.Width, t.Box.Height), TaskScheme + t.Line + ":" + t.Column + ":" + (t.Done ? 1 : 0), t.Drawn)).ToList();

        /// <summary>The target of the link at <paramref name="canvasPoint"/>, as drawn last -- a
        /// task box's: task:line:column:done (<see cref="TaskScheme"/>); null: none there.</summary>
        public string LinkAt(PointF canvasPoint)
        {
            var x = canvasPoint.X - Origin.X;
            var y = canvasPoint.Y - Origin.Y + ScrolledBy;
            foreach (var (box, line, column, done, _, _) in _tasks)
            {
                var around = RectangleF.Inflate(box, box.Width * 0.3f, box.Height * 0.3f);
                if (line >= 0 && around.Contains(x, y))
                    return TaskScheme + line + ":" + column + ":" + (done ? 1 : 0);
            }
            foreach (var piece in _pieces)
            {
                if (piece.Url != null && x >= piece.Bounds.Left && x < piece.Bounds.Left + piece.Bounds.Width && y >= piece.Bounds.Top && y < piece.Bounds.Bottom)
                    return piece.Url;
            }
            return null;
        }

        /// <summary>The source place (line, column; 0-based) of <paramref name="canvasPoint"/>, as
        /// drawn last: the character under it -- or the nearest row's, at its start or end.</summary>
        public (int Line, int Column) SourceAt(PointF canvasPoint)
        {
            if (_rows.Count == 0)
                return (0, 0);
            var x = canvasPoint.X - Origin.X;
            var y = canvasPoint.Y - Origin.Y + ScrolledBy;
            var row = _rows.FirstOrDefault(r => y >= r.Top && y < r.Bottom)
                ?? _rows.OrderBy(r => Math.Min(Math.Abs(y - r.Top), Math.Abs(y - r.Bottom))).First();
            var pieces = row.Pieces.Where(p => p.Line >= 0).ToList();
            if (pieces.Count == 0)
                return (Math.Max(0, row.Line), 0);
            var piece = pieces.FirstOrDefault(p => x >= p.Bounds.Left && x < p.Bounds.Right);
            if (piece == null)
            {
                var last = pieces[pieces.Count - 1];
                if (x >= last.Bounds.Right)
                    return (last.Line, last.Column + last.Text.TrimEnd().Length);
                var first = pieces[0];
                return (first.Line, first.Column);
            }
            // The character under x: measured, prefix by prefix
            using (var bitmap = new Bitmap(1, 1))
            {
                bitmap.SetResolution(_dpiX, _dpiY);
                using var g = Graphics.FromImage(bitmap);
                var offset = x - piece.Bounds.Left;
                var previous = 0f;
                for (var i = 1; i <= piece.Text.Length; i++)
                {
                    var w = g.MeasureString(piece.Text.Substring(0, i), piece.Font, PointF.Empty, Measure).Width;
                    if (offset < (previous + w) / 2)
                        return (piece.Line, piece.Column + i - 1);
                    previous = w;
                }
                return (piece.Line, piece.Column + piece.Text.TrimEnd().Length);
            }
        }
    }
}
