using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MiniVivisection
{
    /// <summary>
    /// Text as Rhino objects, laid out in the world's XY from the origin -- the top left -- down
    /// (-Y); Placed moves them where they go. Three kinds:
    /// Markdown (MarkdownBlocks' blocks): headings as larger bold text (H1 and H2 with a line
    /// under them), paragraphs as wrapped rich text (bold, italic, code in Consolas; links with
    /// a bar under their words -- a solid hatch, under the descenders, by default 10 % of the text
    /// high -- and, for V Text's preview, in the link color, their letters filled over the text's:
    /// Rhino's rich text has no colors of its own; in a document MarkdownLinkLetters draws them),
    /// lists with bullets, numbers or task boxes (drawn: a square, a check mark), quotes with a
    /// bar ('|' on each line), code blocks
    /// framed, tables as a grid of cells, rules as lines. Each piece knows its block, the
    /// block's source lines, and -- a text -- its characters with their places in the source
    /// and its links: what MarkdownText needs to edit a part, take changes made to it in Rhino
    /// back into the Markdown, and follow a link.
    /// Code (Python, C#): Consolas, line numbers in a gutter, a frame, syntax colors -- a text
    /// object per color, the other colors' characters as spaces, so they line up.
    /// Plain text: its lines, wrapped.
    /// A width of 0 wraps nothing: the lines and frames are then as wide as the widest text.
    /// The base font: the dimension style's, or one named. Shared: the .rhp's MarkdownText
    /// (MarkdownRhinoText) and the .gha's V Text.
    /// </summary>
    internal sealed class RhinoTextLayout
    {
        public const string CodeFont = "Consolas";

        /// <summary>A Markdown text's objects keep its Markdown and its frame (user strings), so
        /// MarkdownText on any of them edits it again: made by MarkdownText or baked by V Text.</summary>
        public const string MarkdownKey = "minivivisection.markdown";
        public const string FrameKey = "minivivisection.markdown.frame";

        /// <summary>The text's id: its parts found by it, also ungrouped.</summary>
        public const string IdKey = "minivivisection.markdown.id";

        /// <summary>A part: "index;block;first line;last line;x;y;text height" -- its place in the
        /// layout (x, y in the frame; a text's plane origin, a curve's start).</summary>
        public const string PartKey = "minivivisection.markdown.part";

        /// <summary>Every part's block and its source lines, by index ("0/0/0,0/0/0,1/2/3"):
        /// which blocks lost all their parts, and where they were.</summary>
        public const string BlocksKey = "minivivisection.markdown.blocks";

        /// <summary>A text part's text as Rhino had it when made (its PlainText): changed since?</summary>
        public const string ShownKey = "minivivisection.markdown.shown";

        /// <summary>A text part's characters as laid out, and their offsets in the Markdown
        /// ("start:count" runs, -1: none).</summary>
        public const string PlainKey = "minivivisection.markdown.plain";
        public const string MapKey = "minivivisection.markdown.map";

        /// <summary>A text part's links: "start length url" lines, counted in its non-blank characters.</summary>
        public const string LinksKey = "minivivisection.markdown.links";

        /// <summary>A task box's curves: "line;column;done" -- where its ' ' or 'x' is in the
        /// Markdown (0-based, tabs expanded), and 1 when checked.</summary>
        public const string TaskKey = "minivivisection.markdown.task";

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>A frame as kept: the plane (origin: the top left), a paragraph's text height,
        /// the width text wraps in (0: none), the base font (none: the dimension style's).</summary>
        public static string FrameText(Plane plane, double height, double width, string font = null) => string.Join(";", new[]
        {
            plane.Origin.X, plane.Origin.Y, plane.Origin.Z, plane.XAxis.X, plane.XAxis.Y, plane.XAxis.Z,
            plane.YAxis.X, plane.YAxis.Y, plane.YAxis.Z, height, width,
        }.Select(v => v.ToString("R", Invariant))) + (string.IsNullOrWhiteSpace(font) ? "" : ";" + font.Replace(";", ""));

        /// <summary>One Rhino object of the text, its color (Color.Empty: its layer's), and what
        /// it shows.</summary>
        public sealed class Piece
        {
            public GeometryBase Geometry;
            public Color Color;
            public int Block = -1;           // the Markdown block it shows (its index); -1: none
            public int FirstLine, LastLine;  // that block's source lines (0-based)
            public string Plain;             // a text's characters as laid out ("\n": a line break)
            public int[] Source;             // each one's offset in the Markdown (-1: none -- a bullet, a joining space)
            public List<(int Start, int Length, string Url)> Links; // in Plain's non-blank characters
            public bool Code;                // a code block's text: its line breaks are the source's
            public int TaskLine = -1, TaskColumn; // a task's box (its curves): where its ' ' or 'x' is in the source
            public bool TaskDone;

            public Piece With(GeometryBase geometry) => new Piece
            {
                Geometry = geometry, Color = Color, Block = Block, FirstLine = FirstLine, LastLine = LastLine,
                Plain = Plain, Source = Source, Links = Links, Code = Code, TaskLine = TaskLine, TaskColumn = TaskColumn, TaskDone = TaskDone,
            };

            /// <summary>Where it is in the layout (world XY before Placed): a text's plane origin, a curve's start.</summary>
            public Point3d Anchor => AnchorOf(Geometry);
        }

        /// <summary>Where a piece is: a text's plane origin, a curve's start, a hatch's outline's start.</summary>
        public static Point3d AnchorOf(GeometryBase geometry)
        {
            if (geometry is TextEntity text)
                return text.Plane.Origin;
            if (geometry is Curve curve)
                return curve.PointAtStart;
            if (geometry is Hatch hatch)
            {
                var outline = hatch.Get3dCurves(true);
                return outline != null && outline.Length > 0 ? outline[0].PointAtStart : hatch.GetBoundingBox(true).Min;
            }
            return Point3d.Origin;
        }

        /// <summary>A piece's kind: 0 a text, 1 a curve, 2 a hatch (a link's bar), 3 another.</summary>
        public static int KindOf(GeometryBase geometry) => geometry is TextEntity ? 0 : geometry is Curve ? 1 : geometry is Hatch ? 2 : 3;

        // A text's content: its rich text, and its characters with their source offsets and links
        private sealed class Content
        {
            public string Rtf;
            public string Plain = "";
            public int[] Source = new int[0];
            public List<(int Start, int Length, string Url)> Links = new List<(int, int, string)>();
            public bool Code;
        }

        public readonly List<Piece> Pieces = new List<Piece>();

        /// <summary>The links' letters filled in the link color, to draw over the text -- for a
        /// preview (Markdown's letters: true); not pieces: never made into objects.</summary>
        public readonly List<Piece> Letters = new List<Piece>();

        /// <summary>All of it (world XY, before Placed).</summary>
        public BoundingBox Bounds { get; private set; } = BoundingBox.Empty;

        private readonly DimensionStyle _style;
        private readonly string _face;
        private readonly double _h;
        private readonly double _width; // > 0: text wraps in it
        private readonly MarkdownStyle _css; // how Markdown looks: sizes, fonts, colors, characters
        private readonly string _codeFace, _preFace; // `code` and code blocks' fonts
        private readonly Color _linkColor; // the links' bars and letters (Color.Empty: the text's, no letters)
        private readonly double _linkLine; // the links' bars' height, in text heights
        private readonly double _linkGap;  // their tops under the baseline, in text heights
        private readonly bool _underline;  // the links' bars at all
        private readonly bool _letters;    // the links' letters filled too (Letters)
        private double _y;              // the top of the next block: 0, then down (negative)
        private double _right;          // the right edge of the text laid out so far

        // The Markdown's lines: where each starts, and each as it is (for the source offsets)
        private int[] _lineStarts;
        private string[] _rawLines;

        // The block being laid out
        private int _block = -1, _first, _last;

        // Lines and frames as wide as all of it, made at the end (the width known then)
        private readonly List<(double X, double Top, double Bottom, double MinRight, bool Frame, int Block, int First, int Last)> _full =
            new List<(double, double, double, double, bool, int, int, int)>();

        private RhinoTextLayout(DimensionStyle style, double height, double width, string face = null, MarkdownStyle look = null, bool linksColored = false, bool letters = false)
        {
            _css = look ?? MarkdownStyle.Default(MarkdownStyle.Target.Rhino);
            _linkColor = linksColored ? _css.LinkColor : Color.Empty;
            var gap = _css.Length("a", "text-underline-offset", BarGap);
            _linkGap = gap >= 0 && gap <= 2 ? gap : BarGap;
            var line = _css.Length("a", "text-decoration-thickness", BarHeight);
            _linkLine = line > 0 && line <= 1 ? line : BarHeight;
            _underline = _css.Keyword("a", "text-decoration", "underline") != "none";
            _letters = letters;
            _style = style ?? new DimensionStyle();
            // The base font: the frame's (MarkdownText's dialog), else the style's, else the dimension style's
            _face = !string.IsNullOrWhiteSpace(face) ? face.Trim() : _css.Font("body") ?? _style.Font?.FamilyName ?? "Arial";
            _codeFace = _css.Font("code") ?? CodeFont;
            _preFace = _css.Font("pre") ?? CodeFont;
            _h = height > 0 && !double.IsInfinity(height) ? height : 1;
            _width = width > 0 && !double.IsInfinity(width) ? width : 0;
        }

        private double H => _h;

        /// <summary>The base font's name.</summary>
        public string Face => _face;

        /// <summary>The width lines and frames reach: the wrapping width, or the widest text.</summary>
        public double FullWidth => _width > 0 ? _width : _right;

        /// <summary>The pieces (or <paramref name="pieces"/>: the Letters) where <paramref name="xform"/>
        /// takes them (copies).</summary>
        public IEnumerable<Piece> Placed(Transform xform, IEnumerable<Piece> pieces = null)
        {
            foreach (var piece in pieces ?? Pieces)
            {
                var geometry = piece.Geometry.Duplicate();
                if (geometry is TextEntity text)
                {
                    // Its plane moved, not the text transformed (that would scale its height)
                    var plane = text.Plane;
                    plane.Transform(xform);
                    text.Plane = plane;
                }
                else
                {
                    geometry.Transform(xform);
                }
                yield return piece.With(geometry);
            }
        }

        // --- Markdown --------------------------------------------------------------------------

        /// <summary>Markdown's blocks: <paramref name="height"/> a paragraph's text height (one em
        /// of <paramref name="look"/>), <paramref name="width"/> the width text wraps in (0: none),
        /// <paramref name="face"/> the base font (null: the look's, else the dimension style's);
        /// <paramref name="source"/>, the Markdown they were parsed from, gives the texts' characters
        /// their source offsets; <paramref name="look"/> how it looks (null: the Rhino default);
        /// <paramref name="linksColored"/>: the links' words and bars in its link color (else the
        /// text's); <paramref name="letters"/>: their letters filled in the link color too
        /// (Letters, for a preview -- in a document MarkdownLinkLetters draws them).</summary>
        public static RhinoTextLayout Markdown(IEnumerable<MarkdownBlocks.Block> blocks, DimensionStyle style, double height, double width, string face = null, string source = null,
            MarkdownStyle look = null, bool linksColored = true, bool letters = false)
        {
            var layout = new RhinoTextLayout(style, height, width, face, look, linksColored, letters);
            layout.Lines(source);
            layout.Blocks(blocks);
            layout.Finish();
            return layout;
        }

        private void Lines(string source)
        {
            if (source == null)
                return;
            var starts = new List<int> { 0 };
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] == '\n')
                    starts.Add(i + 1);
            }
            _lineStarts = starts.ToArray();
            _rawLines = new string[starts.Count];
            for (var l = 0; l < starts.Count; l++)
            {
                var end = l + 1 < starts.Count ? starts[l + 1] - 1 : source.Length;
                _rawLines[l] = source.Substring(starts[l], end - starts[l]).TrimEnd('\r');
            }
        }

        // The source offset of a column (tabs expanded, as the parsers count) of a line; -1: none
        private int Offset(int line, int column)
        {
            if (_lineStarts == null || line < 0 || line >= _lineStarts.Length || column < 0)
                return -1;
            var raw = _rawLines[line];
            var at = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                if (at >= column)
                    return _lineStarts[line] + i;
                at += raw[i] == '\t' ? ScriptTokens.TabSize - at % ScriptTokens.TabSize : 1;
            }
            return at >= column ? _lineStarts[line] + raw.Length : -1;
        }

        // The width a text wraps in, from x on (0: none)
        private double WrapFrom(double x) => _width > 0 ? Math.Max(H * 2, _width - x) : 0;

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

        // A text block's rich text as its element says: bold, italic, its font
        private Content Rich(IEnumerable<MarkdownBlocks.Run> runs, string element, bool bold = false, bool italic = false) =>
            Rich(runs, _css.Bold(element, bold), _css.Italic(element, italic), _css.Font(element));

        private void Blocks(IEnumerable<MarkdownBlocks.Block> blocks)
        {
            var first = true;
            var index = -1;
            var gap = _css.Length("body", "margin-bottom", 0.7);
            foreach (var block in blocks)
            {
                _block = ++index;
                _first = block.FirstLine;
                _last = block.LastLine;
                var element = ElementOf(block);
                var color = _css.ColorOf(element);
                switch (block.Kind)
                {
                    case MarkdownBlocks.BlockKind.Heading:
                    {
                        if (!first)
                            _y -= H * _css.Length(element, "margin-top", 0.5);
                        var height = H * _css.Length(element, "font-size", block.Level == 1 ? 1.8 : block.Level == 2 ? 1.45 : block.Level == 3 ? 1.2 : 1.05);
                        _y = Text(Rich(block.Runs, element, bold: true), 0, _y, height, WrapFrom(0), color);
                        if (_css.Keyword(element, "border-bottom", block.Level <= 2 ? "line" : "none") == "line")
                        {
                            _y -= H * _css.Length(element, "padding-bottom", 0.3);
                            Full(_y, _y, 0, false);
                        }
                        break;
                    }
                    case MarkdownBlocks.BlockKind.Paragraph:
                        _y = Text(Rich(block.Runs, element), 0, _y, H * _css.Length(element, "font-size", 1), WrapFrom(0), color);
                        break;
                    case MarkdownBlocks.BlockKind.ListItem:
                    {
                        var size = H * _css.Length("li", "font-size", 1);
                        var indent = H * (_css.Length("li", "margin-left", 0.5) + _css.Length("li", "padding-left", 1.6) * block.Level);
                        var task = block.Marker == "☐" || block.Marker == "☑";
                        var done = block.Marker == "☑";
                        // The marker: a task's box (drawn, or the style's character), the style's bullet, or the number
                        var marker = task ? _css.TaskMark(done) : block.Marker == "•" ? _css.Bullet(block.Level) : block.Marker;
                        var markerWidth = H * (task ? _css.Length("task", "marker-width", 1.6) : Math.Max(_css.Length("li", "marker-width", 1.4), 0.65 * (marker ?? "").Length + 0.7));
                        if (task && marker == null)
                            TaskBox(indent, _y, done, block.TaskLine, block.TaskColumn, H * _css.Length("task", "size", 0.85), color);
                        else if (!string.IsNullOrEmpty(marker))
                            Text(Marker(marker), indent, _y, size, 0, color, task ? (block.TaskLine, block.TaskColumn, done) : ((int, int, bool)?)null);
                        _y = Math.Min(_y - size, Text(Rich(block.Runs, element), indent + markerWidth, _y, size, WrapFrom(indent + markerWidth), color));
                        break;
                    }
                    case MarkdownBlocks.BlockKind.Quote:
                    {
                        var depth = Math.Max(1, block.Level);
                        var step = H * _css.Length(element, "padding-left", 1.2);
                        var size = H * _css.Length(element, "font-size", 1);
                        var top = _y;
                        _y = Text(Rich(block.Runs, element, italic: true), step * depth, _y, size, WrapFrom(step * depth), color);
                        // The bar: a character on each of its lines, a line, or none
                        var bar = _css.QuoteBar("|");
                        var at = H * _css.Length(element, "margin-left", 0.3);
                        var bars = string.IsNullOrEmpty(bar) ? null : QuoteBars(top - _y, bar, _css.Bold(element, true, "bar-weight"), size);
                        for (var d = 0; d < depth; d++)
                        {
                            if (bar == null)
                                Line(at + step * d + H * 0.1, top + H * 0.2, at + step * d + H * 0.1, _y, color);
                            else if (bars != null)
                                Place(Entity(bars.Rtf, size, 0), at + step * d, top, color, bars);
                        }
                        break;
                    }
                    case MarkdownBlocks.BlockKind.Code:
                    {
                        var top = _y;
                        var right = _right;
                        _right = 0;
                        var padLeft = H * _css.Length(element, "padding-left", 0.6);
                        var bottom = Text(CodeBlock(block.Lines), padLeft, _y - H * _css.Length(element, "padding-top", 0.5), H * _css.Length(element, "font-size", 0.95), 0, color);
                        var codeRight = _right + H * _css.Length(element, "padding-right", 0.6);
                        _right = Math.Max(right, codeRight);
                        _y = bottom - H * _css.Length(element, "padding-bottom", 0.5);
                        if (_css.Keyword(element, "border", "frame") == "frame")
                            Full(top, _y, codeRight, true); // wider than the rest when its code is
                        break;
                    }
                    case MarkdownBlocks.BlockKind.Table:
                        Table(block.Lines, color);
                        break;
                    case MarkdownBlocks.BlockKind.Rule:
                        _y -= H * _css.Length(element, "margin-top", 0.4);
                        Full(_y, _y, 0, false);
                        break;
                }
                _y -= H * _css.Length(element, "margin-bottom", block.Kind == MarkdownBlocks.BlockKind.Rule ? gap + 0.4 : gap);
                first = false;
            }
            _block = -1;
        }

        // A task's box at a list item's marker (x, y: its text's top left): a square with sharp
        // corners, side long, centered on the capitals, a check mark in it when done -- curves, so
        // every box is alike whatever the font. Each keeps the box's place in the source: a click
        // checks it (MarkdownTextMouse).
        private void TaskBox(double x, double y, bool done, int line, int column, double side, Color color)
        {
            var bottom = y - H * 0.5 - side / 2;
            Point3d At(double u, double v) => new Point3d(x + u * side, bottom + v * side, 0);
            var square = new PolylineCurve(new[] { At(0, 0), At(1, 0), At(1, 1), At(0, 1), At(0, 0) });
            Pieces.Add(new Piece { Geometry = square, Color = color, Block = _block, FirstLine = _first, LastLine = _last, TaskLine = line, TaskColumn = column, TaskDone = done });
            if (done)
            {
                var check = new PolylineCurve(new[] { At(0.2, 0.52), At(0.42, 0.26), At(0.8, 0.76) });
                Pieces.Add(new Piece { Geometry = check, Color = color, Block = _block, FirstLine = _first, LastLine = _last, TaskLine = line, TaskColumn = column, TaskDone = done });
            }
            _right = Math.Max(_right, x + side);
        }

        // A quote's bar: mark on each of its lines (bold or not), as many as fit in its text's
        // height (at text height size)
        private Content QuoteBars(double height, string mark, bool bold, double size)
        {
            var one = Measure(RtfPlain("A", code: false), size).Height;
            var pitch = Measure(RtfPlain("A\nA", code: false), size).Height - one;
            var lines = pitch > 0 ? Math.Max(1, (int)Math.Round((height - one) / pitch) + 1) : 1;
            var plain = string.Join("\n", Enumerable.Repeat(mark, lines));
            var rtf = new StringBuilder(Header());
            for (var i = 0; i < lines; i++)
                rtf.Append(bold ? @"{\f0 \b " : @"{\f0 ").Append(Escape(mark)).Append(i < lines - 1 ? @"\par}" : "}");
            return new Content { Rtf = rtf.Append('}').ToString(), Plain = plain, Source = Enumerable.Repeat(-1, plain.Length).ToArray() };
        }

        private void Full(double top, double bottom, double minRight, bool frame) => _full.Add((0, top, bottom, minRight, frame, _block, _first, _last));

        private static readonly Regex TableRule = new Regex(@"^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$", RegexOptions.CultureInvariant);

        // A pipe table as a grid: a cell's text in its column (as wide as its widest), the
        // header row as th says (bold), lines between all (border: grid)
        private void Table(List<(string Text, int Line)> lines, Color color)
        {
            var rows = lines.Where(l => !TableRule.IsMatch(l.Text)).Select(l => (Cells: Cells(l.Text), l.Line)).ToList();
            if (rows.Count == 0)
                return;
            var header = lines.Count > 1 && TableRule.IsMatch(lines[1].Text);
            var columns = rows.Max(r => r.Cells.Count);
            var pad = H * _css.Length("table", "padding-left", 0.4);
            var padY = H * _css.Length("table", "padding-top", 0.4);
            var size = H * _css.Length("table", "font-size", 1);
            var headerColor = _css.ColorOf("th").IsEmpty ? color : _css.ColorOf("th");
            // Each cell made first, to measure it
            var cells = rows.Select((row, r) => row.Cells.Select(cell =>
            {
                var head = header && r == 0;
                var runs = MarkdownBlocks.Inline(cell.Text, row.Line, cell.Column);
                var content = head ? Rich(runs, "th", bold: true) : Rich(runs, "table");
                var (entity, width, height) = Measure(content.Rtf, size);
                return (Entity: entity, Width: width, Height: height, Content: content, Color: head ? headerColor : color);
            }).ToList()).ToList();
            var widths = Enumerable.Range(0, columns).Select(c => cells.Max(row => c < row.Count ? row[c].Width : 0) + 2 * pad).ToArray();
            var grid = _css.Keyword("table", "border", "grid") == "grid";
            var top = _y;
            var y = _y;
            foreach (var row in cells)
            {
                var rowHeight = row.Max(c => c.Height) + 2 * padY;
                var x = 0.0;
                for (var c = 0; c < columns; c++)
                {
                    if (c < row.Count)
                        Place(row[c].Entity, x + pad, y - padY, row[c].Color, row[c].Content);
                    x += widths[c];
                }
                y -= rowHeight;
                if (grid)
                    Line(0, y, widths.Sum(), y);
            }
            if (grid)
            {
                Line(0, top, widths.Sum(), top);
                var at = 0.0;
                for (var c = 0; c <= columns; c++)
                {
                    Line(at, top, at, y);
                    if (c < columns)
                        at += widths[c];
                }
            }
            _right = Math.Max(_right, widths.Sum());
            _y = y;
        }

        // A table line's cells, trimmed, each with the column it starts at (tabs expanded)
        private static List<(string Text, int Column)> Cells(string line)
        {
            var expanded = ScriptTokens.ExpandTabs(line);
            var cells = new List<(string, int)>();
            var start = 0;
            var trimmed = expanded.TrimEnd();
            var begin = expanded.Length - expanded.TrimStart().Length;
            if (begin < trimmed.Length && trimmed[begin] == '|')
                begin++;
            var end = trimmed.Length;
            if (end > begin && trimmed[end - 1] == '|' && (end < 2 || trimmed[end - 2] != '\\'))
                end--;
            start = begin;
            for (var i = begin; i <= end; i++)
            {
                if (i < end && (trimmed[i] != '|' || (i > 0 && trimmed[i - 1] == '\\')))
                    continue;
                var raw = trimmed.Substring(start, i - start);
                var lead = raw.Length - raw.TrimStart().Length;
                cells.Add((raw.Trim(), start + lead));
                start = i + 1;
            }
            return cells;
        }

        // --- code and plain text -----------------------------------------------------------------

        /// <summary>A script as the Panel shows it: Consolas, <paramref name="height"/> its text
        /// height, the line numbers in a gutter (<paramref name="numbers"/>' color), a frame;
        /// each token in <paramref name="colorOf"/>'s color (Color.Empty: the layer's). Not wrapped.</summary>
        public static RhinoTextLayout Code(string text, ScriptTokens.Language language, DimensionStyle style, double height, Func<ScriptTokens.Kind, Color> colorOf, Color numbers)
        {
            var layout = new RhinoTextLayout(style, height, 0);
            layout.CodeLines(text, language, colorOf, numbers);
            layout.Finish();
            return layout;
        }

        private void CodeLines(string text, ScriptTokens.Language language, Func<ScriptTokens.Kind, Color> colorOf, Color numbers)
        {
            var lines = ScriptTokens.Lines(text ?? "").Select(ScriptTokens.ExpandTabs).ToArray();
            if (lines.Length == 0)
                lines = new[] { "" };
            var tokens = ScriptTokens.Tokenize(lines, language);
            var pad = H * 0.5;
            var sample = Measure(RtfPlain(new string('0', 20), code: true));
            var charWidth = sample.Width / 20;
            var digits = Math.Max(2, lines.Length.ToString().Length);
            var gutter = (digits + 1.5) * charWidth;
            var codeLeft = gutter + charWidth * 0.8;

            // The line numbers, right-aligned at the gutter's edge
            var numberText = Entity(RtfPlain(string.Join("\n", Enumerable.Range(1, lines.Length)), code: true), H, 0);
            if (numberText != null)
            {
                numberText.TextHorizontalAlignment = TextHorizontalAlignment.Right;
                Place(numberText, gutter - charWidth * 0.5, -pad, numbers);
            }

            // One text per color: its characters in place, the others as spaces (Consolas: each
            // as wide), trailing spaces dropped
            var kinds = new Dictionary<Color, char[][]>();
            char[][] LayerOf(Color color)
            {
                if (!kinds.TryGetValue(color, out var layer))
                    kinds[color] = layer = lines.Select(l => Enumerable.Repeat(' ', l.Length).ToArray()).ToArray();
                return layer;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var kindAt = new Color?[line.Length];
                foreach (var token in tokens[i])
                {
                    for (var c = token.Start; c < Math.Min(line.Length, token.Start + token.Length); c++)
                        kindAt[c] = colorOf(token.Kind);
                }
                for (var c = 0; c < line.Length; c++)
                {
                    if (line[c] != ' ')
                        LayerOf(kindAt[c] ?? Color.Empty)[i][c] = line[c];
                }
            }
            var all = Measure(RtfPlain(string.Join("\n", lines.Select(l => l.Length == 0 ? " " : l)), code: true));
            foreach (var pair in kinds.OrderBy(k => k.Key.IsEmpty ? 0 : 1))
            {
                var layerText = string.Join("\n", pair.Value.Select(l => new string(l).TrimEnd()));
                if (layerText.Trim().Length == 0)
                    continue;
                var entity = Entity(RtfPlain(layerText, code: true), H, 0);
                if (entity != null)
                    Place(entity, codeLeft, -pad, pair.Key);
            }
            var textHeight = Math.Max(H * lines.Length, all.Height);
            _right = Math.Max(_right, codeLeft + all.Width + pad);
            _y = -pad - textHeight - pad;
            Line(gutter, 0, gutter, _y);
            Full(0, _y, 0, true);
        }

        /// <summary>Plain text: its lines, <paramref name="height"/> their text height, wrapped
        /// in <paramref name="width"/> (0: not), in <paramref name="face"/> (null: the dimension style's font).</summary>
        public static RhinoTextLayout Plain(string text, DimensionStyle style, double height, double width, string face = null)
        {
            var layout = new RhinoTextLayout(style, height, width, face);
            layout._y = layout.Text(new Content { Rtf = layout.RtfPlain(text ?? "", code: false), Plain = text ?? "" }, 0, 0, layout.H, layout.WrapFrom(0));
            layout.Finish();
            return layout;
        }

        // The lines and frames to the full width, and the bounds
        private void Finish()
        {
            var full = FullWidth;
            foreach (var (x, top, bottom, minRight, frame, block, first, last) in _full)
            {
                var right = Math.Max(full, minRight);
                if (right - x < H * 0.01)
                    continue;
                (_block, _first, _last) = (block, first, last);
                if (frame)
                    Rectangle(x, top, right, bottom);
                else
                    Line(x, top, right, top);
            }
            _block = -1;
            _full.Clear();
            var bounds = BoundingBox.Empty;
            foreach (var piece in Pieces)
                bounds.Union(piece.Geometry.GetBoundingBox(true));
            Bounds = bounds;
        }

        // --- Rhino objects ----------------------------------------------------------------------

        private TextEntity Entity(string rtf, double height, double wrapWidth)
        {
            var entity = TextEntity.CreateWithRichText(rtf, Plane.WorldXY, _style, wrapWidth > 0, wrapWidth > 0 ? wrapWidth : 0, 0);
            if (entity == null)
                return null;
            // The layout's units are the model's: not the dimension style's scale (annotation
            // scale 10 would make the text ten times what the layout measured), nor its mask
            entity.DimensionScale = 1;
            entity.MaskEnabled = false;
            entity.TextHeight = height;
            entity.TextHorizontalAlignment = TextHorizontalAlignment.Left;
            entity.TextVerticalAlignment = TextVerticalAlignment.Top;
            if (wrapWidth > 0)
            {
                entity.TextIsWrapped = true;
                entity.FormatWidth = wrapWidth; // the wrapping rectangle
                entity.WrapText();
            }
            return entity;
        }

        private (TextEntity Entity, double Width, double Height) Measure(string rtf, double height = 0)
        {
            height = height > 0 ? height : H;
            var entity = Entity(rtf, height, 0);
            if (entity == null)
                return (null, 0, height);
            var box = entity.GetBoundingBox(Plane.WorldXY);
            return (entity, box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);
        }

        // At (x, y), its top left there (its top right when right-aligned); a task's box (a
        // character) knows its place in the source
        private void Place(TextEntity entity, double x, double y, Color color, Content content = null, (int Line, int Column, bool Done)? task = null)
        {
            if (entity == null)
                return;
            entity.Plane = new Plane(new Point3d(x, y, 0), Vector3d.XAxis, Vector3d.YAxis);
            Pieces.Add(new Piece
            {
                Geometry = entity, Color = color, Block = _block, FirstLine = _first, LastLine = _last,
                Plain = content?.Plain, Source = content?.Source, Links = content?.Links, Code = content?.Code == true,
                TaskLine = task?.Line ?? -1, TaskColumn = task?.Column ?? 0, TaskDone = task?.Done == true,
            });
            if (content?.Links != null && content.Links.Count > 0)
                LinkBars(entity, content.Links);
        }

        // --- links' bars --------------------------------------------------------------------------

        /// <summary>A link's bar by default (settings "Markdown links: line thickness" and "line
        /// distance"), in text heights: its height, and how far under the baseline its top is (an
        /// underline's place; about 0.3 clears the descenders of g, p, y).</summary>
        public const double BarHeight = 0.10, BarGap = 0.15;

        /// <summary>How far a link's filled letters lie in front of the text (in text heights): over
        /// it also in a perspective view, where the draw order doesn't count.</summary>
        public const double LetterLift = 0.002;

        // Each link's bar under its words, one per line of the text it is on (Rhino wraps the text,
        // a hard break starts a line): its glyphs' boxes -- one per non-blank character, as the links
        // count them, in the text's own style (its height: a heading's isn't the dimension style's) --,
        // every glyph in the line it is on (a line ends where the next glyph is back to the left or
        // below it), each line's baseline from all of its glyphs. In the link color, for a preview:
        // its letters filled too.
        private void LinkBars(TextEntity entity, List<(int Start, int Length, string Url)> links)
        {
            List<Curve[]> glyphs;
            List<BoundingBox> boxes;
            try
            {
                glyphs = entity.CreateCurvesGrouped(entity.GetDimensionStyle(_style) ?? _style, false, 1.0, 0.0);
                if (glyphs == null)
                    return;
                boxes = glyphs.Select(glyph =>
                {
                    var box = BoundingBox.Empty;
                    foreach (var curve in glyph ?? new Curve[0])
                        box.Union(curve.GetBoundingBox(true));
                    return box;
                }).ToList();
            }
            catch (Exception)
            {
                return; // no bars, the text as it is
            }
            if (boxes.Count != Shown(entity).Count(c => !char.IsWhiteSpace(c)))
                return; // its glyphs aren't its characters: where the links are isn't known
            var h = entity.TextHeight;
            var lineOf = Lines(boxes, h);
            var baselines = Enumerable.Range(0, lineOf.Length == 0 ? 0 : lineOf.Max() + 1)
                .Select(l => Baseline(Enumerable.Range(0, boxes.Count).Where(i => lineOf[i] == l && boxes[i].IsValid).Select(i => boxes[i]).ToList(), h))
                .ToArray();
            foreach (var (start, length, _) in links)
            {
                var on = Enumerable.Range(Math.Max(0, start), Math.Max(0, Math.Min(start + length, boxes.Count) - Math.Max(0, start))).Where(i => boxes[i].IsValid).ToList();
                if (_letters && !_linkColor.IsEmpty)
                {
                    foreach (var i in on)
                        Letter(glyphs[i], h);
                }
                if (!_underline)
                    continue;
                foreach (var line in on.GroupBy(i => lineOf[i]))
                    Bar(line.Select(i => boxes[i]).ToList(), baselines[line.Key], h);
            }
        }

        // Each glyph's line (0, 1, ...): a new one where a glyph is back to the left of the one
        // before, or below it
        private static int[] Lines(List<BoundingBox> boxes, double h)
        {
            var lines = new int[boxes.Count];
            var line = 0;
            var last = BoundingBox.Empty;
            for (var i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box.IsValid)
                {
                    if (last.IsValid && (box.Min.X < last.Min.X - h * 0.01 || box.Max.Y < last.Min.Y))
                        line++;
                    last = box;
                }
                lines[i] = line;
            }
            return lines;
        }

        // A link's letter, filled in its color over the text's, a hair in front of it
        private void Letter(Curve[] glyph, double h)
        {
            foreach (var hatch in LetterHatches(glyph, h))
            {
                hatch.Translate(new Vector3d(0, 0, h * LetterLift));
                Letters.Add(new Piece { Geometry = hatch, Color = _linkColor, Block = _block, FirstLine = _first, LastLine = _last });
            }
        }

        /// <summary>A glyph's outlines filled (solid hatches): a letter with a hole one hatch, its
        /// hole an inner loop; i, j or an accent a hatch per part. Empty when they can't be.</summary>
        public static Hatch[] LetterHatches(Curve[] glyph, double h)
        {
            if (glyph == null || glyph.Length == 0)
                return new Hatch[0];
            try
            {
                return Hatch.Create(glyph.Where(c => c != null && c.IsClosed), SolidPattern(), 0, 1, Math.Max(h * 1e-4, 1e-9)) ?? new Hatch[0];
            }
            catch (Exception)
            {
                return new Hatch[0];
            }
        }

        // The line's baseline: the bottom most of its glyphs sit on (the highest of equals) -- not
        // a descender's (g, p, y), nor a comma's or a dash's
        private static double Baseline(List<BoundingBox> line, double h)
        {
            var bottoms = line.Select(b => b.Min.Y).ToList();
            return bottoms.Count == 0 ? 0 : bottoms.OrderByDescending(y => bottoms.Count(o => Math.Abs(o - y) <= h * 0.04)).ThenByDescending(y => y).First();
        }

        // A link's glyphs on one line: the bar under them, from the first's left to the last's
        // right, its top the line distance under the line's baseline
        private void Bar(List<BoundingBox> line, double baseline, double h)
        {
            if (line.Count == 0)
                return;
            var top = baseline - h * _linkGap;
            var bottom = top - h * _linkLine;
            var x1 = line.Min(b => b.Min.X);
            var x2 = line.Max(b => b.Max.X);
            var z = line[0].Min.Z;
            var outline = new PolylineCurve(new[] { new Point3d(x1, top, z), new Point3d(x2, top, z), new Point3d(x2, bottom, z), new Point3d(x1, bottom, z), new Point3d(x1, top, z) });
            Hatch[] hatches;
            try
            {
                hatches = Hatch.Create(outline, SolidPattern(), 0, 1, Math.Max(h * 1e-4, 1e-9));
            }
            catch (Exception)
            {
                return;
            }
            if (hatches == null || hatches.Length == 0)
                return;
            Pieces.Add(new Piece { Geometry = hatches[0], Color = _linkColor, Block = _block, FirstLine = _first, LastLine = _last });
        }

        /// <summary>The solid hatch pattern's index in the active document's table (0 without one: Solid is the first).</summary>
        public static int SolidPattern()
        {
            try
            {
                var pattern = Rhino.RhinoDoc.ActiveDoc?.HatchPatterns.FindName("Solid");
                return pattern != null && pattern.Index >= 0 ? pattern.Index : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Text at (x, y), its top left there, in color (Color.Empty: the layer's); returns the y below it
        private double Text(Content content, double x, double y, double height, double wrapWidth, Color color = default, (int Line, int Column, bool Done)? task = null)
        {
            var entity = Entity(content.Rtf, height, wrapWidth);
            if (entity == null)
                return y - height;
            var box = entity.GetBoundingBox(Plane.WorldXY);
            var textHeight = Math.Max(height, box.Max.Y - box.Min.Y);
            _right = Math.Max(_right, x + box.Max.X);
            Place(entity, x, y, color, content, task);
            return y - textHeight;
        }

        private void Line(double x1, double y1, double x2, double y2, Color color = default) =>
            Pieces.Add(new Piece { Geometry = new LineCurve(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0)), Color = color, Block = _block, FirstLine = _first, LastLine = _last });

        private void Rectangle(double x1, double y1, double x2, double y2) =>
            Pieces.Add(new Piece
            {
                Geometry = new PolylineCurve(new[] { new Point3d(x1, y1, 0), new Point3d(x2, y1, 0), new Point3d(x2, y2, 0), new Point3d(x1, y2, 0), new Point3d(x1, y1, 0) }),
                Color = Color.Empty, Block = _block, FirstLine = _first, LastLine = _last,
            });

        // --- rich text --------------------------------------------------------------------------

        // The fonts: f0 the base font (or a block's own), f1 `code`'s, f2 code blocks'
        private string Header(string face = null) => @"{\rtf1\ansi\deff0{\fonttbl{\f0 " + Escape(face ?? _face) + @";}{\f1 " + Escape(_codeFace ?? CodeFont) + @";}{\f2 " + Escape(_preFace ?? CodeFont) + @";}}\f0 ";

        // A bullet, a number or a task box: no source
        private Content Marker(string marker) => new Content
        {
            Rtf = Header() + Escape(marker) + "}",
            Plain = marker,
            Source = Enumerable.Repeat(-1, marker.Length).ToArray(),
        };

        // Rhino's text breaks a line at \par only at the end of a group -- "{\f0 one\par}{\f0 two}",
        // as Rhino writes it --, keeping a line's spaces and empty lines: each line its group
        private string RtfPlain(string text, bool code, bool block = false)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var rtf = new StringBuilder(Header());
            for (var i = 0; i < lines.Length; i++)
                rtf.Append('{').Append(!code ? @"\f0 " : block ? @"\f2 " : @"\f1 ").Append(Escape(lines[i])).Append(i < lines.Length - 1 ? @"\par}" : "}");
            return rtf.Append('}').ToString();
        }

        // A code block: its lines in its font (pre's: Consolas), each character at its place in the source
        private Content CodeBlock(List<(string Text, int Line)> lines)
        {
            var plain = new StringBuilder();
            var source = new List<int>();
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    plain.Append('\n');
                    source.Add(-1);
                }
                var (text, line) = lines[i];
                var start = -1;
                if (_rawLines != null && line >= 0 && line < _rawLines.Length)
                {
                    // The code's text ends its source line (after the indentation)
                    var expanded = ScriptTokens.ExpandTabs(_rawLines[line]);
                    var column = expanded.EndsWith(text, StringComparison.Ordinal) ? expanded.Length - text.Length : expanded.IndexOf(text, StringComparison.Ordinal);
                    start = column >= 0 ? Offset(line, column) : -1;
                }
                plain.Append(text);
                for (var k = 0; k < text.Length; k++)
                    source.Add(start >= 0 ? start + k : -1);
            }
            var all = plain.ToString();
            return new Content { Rtf = RtfPlain(all, code: true, block: true), Plain = all, Source = source.ToArray(), Code = true };
        }

        private Content Rich(IEnumerable<MarkdownBlocks.Run> runs, bool bold, bool italic, string face)
        {
            var content = new Content();
            var plain = new StringBuilder();
            var source = new List<int>();
            var nonBlank = 0;
            void Characters(string text, int line, int column, string url)
            {
                var start = line >= 0 ? Offset(line, column) : -1;
                var linkStart = nonBlank;
                for (var k = 0; k < text.Length; k++)
                {
                    plain.Append(text[k]);
                    source.Add(start >= 0 ? start + k : -1);
                    if (!char.IsWhiteSpace(text[k]))
                        nonBlank++;
                }
                if (!string.IsNullOrEmpty(url) && nonBlank > linkStart)
                    content.Links.Add((linkStart, nonBlank - linkStart, url));
            }

            var rtf = new StringBuilder(Header(face));
            var open = "{" + @"\f0 " + (bold ? @"\b " : "") + (italic ? @"\i " : "");
            rtf.Append(open);
            // Spaces go outside a formatted group (Rhino drops them at its edges) -- except before
            // code: a space right before a change of font is drawn next to nothing ("run
            // MarkdownText" looked "runMarkdownText"), in the code's group it's kept. So a run's
            // trailing spaces wait for the next run.
            var spaces = "";
            foreach (var run in runs)
            {
                if (run.Text == "\n")
                {
                    rtf.Append(Escape(spaces)).Append(@"\par}").Append(open); // a hard break: the next line's group
                    spaces = "";
                    Characters("\n", -1, 0, null);
                    continue;
                }
                if (string.IsNullOrEmpty(run.Text))
                    continue;
                Characters(run.Text, run.Line, run.Column, run.Url);
                var style = run.Style;
                var text = run.Text;
                if (style == MarkdownBlocks.Style.None || text.Trim().Length == 0)
                {
                    var kept = text.TrimEnd(' ');
                    rtf.Append(Escape(spaces + kept));
                    spaces = text.Substring(kept.Length);
                    continue;
                }
                var leading = text.Length - text.TrimStart().Length;
                var trailing = text.Length - text.TrimEnd().Length;
                var before = spaces + text.Substring(0, leading);
                spaces = text.Substring(text.Length - trailing);
                var code = style.HasFlag(MarkdownBlocks.Style.Code);
                rtf.Append(code ? "" : Escape(before)).Append('{');
                if (style.HasFlag(MarkdownBlocks.Style.Bold))
                    rtf.Append(@"\b ");
                if (style.HasFlag(MarkdownBlocks.Style.Italic) || style.HasFlag(MarkdownBlocks.Style.Image))
                    rtf.Append(@"\i ");
                if (style.HasFlag(MarkdownBlocks.Style.Strike))
                    rtf.Append(@"\strike ");
                if (code)
                    rtf.Append(@"\f1 ").Append(Escape(before));
                rtf.Append(Escape(text.Substring(leading, text.Length - leading - trailing))).Append('}');
            }
            content.Rtf = rtf.Append(Escape(spaces)).Append("}}").ToString(); // the line's group, the text
            content.Plain = plain.ToString();
            content.Source = source.ToArray();
            return content;
        }

        /// <summary>The width of <paramref name="text"/> (one line, the base font) at text height
        /// <paramref name="height"/>: V Text measures a Panel's width in it.</summary>
        public static double PlainWidth(string text, DimensionStyle style, double height, string face = null)
        {
            var layout = new RhinoTextLayout(style, height, 0, face);
            return layout.Measure(layout.RtfPlain(text ?? "", code: false)).Width;
        }

        internal static string Escape(string text)
        {
            var escaped = new StringBuilder(text.Length + 8);
            foreach (var c in text)
            {
                if (c == '\\' || c == '{' || c == '}')
                    escaped.Append('\\').Append(c);
                else if (c > 127)
                    escaped.Append(@"\u").Append((int)(short)c).Append('?');
                else if (c == '\t')
                    escaped.Append(@"\tab ");
                else
                    escaped.Append(c);
            }
            return escaped.ToString();
        }

        // --- a part's user strings --------------------------------------------------------------

        /// <summary>What a placed piece keeps (beside the Markdown and the frame): the text's id,
        /// the part (its index, block, source lines, place in the layout <paramref name="local"/>
        /// and text height), every part's block, and -- a text -- what Rhino shows, its characters,
        /// their source offsets, its links.</summary>
        public static IEnumerable<(string Key, string Value)> PartStrings(Piece piece, Piece local, int index, Guid id, string blocks)
        {
            var at = local.Anchor;
            var height = piece.Geometry is TextEntity t ? t.TextHeight : 0;
            yield return (IdKey, id.ToString("D"));
            yield return (PartKey, string.Join(";", index, piece.Block, piece.FirstLine, piece.LastLine,
                at.X.ToString("R", Invariant), at.Y.ToString("R", Invariant), height.ToString("R", Invariant)));
            yield return (BlocksKey, blocks);
            if (piece.TaskLine >= 0)
                yield return (TaskKey, piece.TaskLine + ";" + piece.TaskColumn + ";" + (piece.TaskDone ? 1 : 0));
            if (piece.Geometry is TextEntity text)
            {
                yield return (ShownKey, Shown(text));
                if (piece.Plain != null)
                {
                    yield return (PlainKey, piece.Plain);
                    yield return (MapKey, MapText(piece.Source));
                }
                if (piece.Links != null && piece.Links.Count > 0)
                    yield return (LinksKey, string.Join("\n", piece.Links.Select(l => l.Start + " " + l.Length + " " + l.Url)));
            }
        }

        /// <summary>A text's characters as Rhino shows them, line breaks as "\n".</summary>
        public static string Shown(TextEntity text) => (text?.PlainText ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>The blocks of all pieces, by index, with their source lines: "block/first/last,...".</summary>
        public string BlocksText() => string.Join(",", Pieces.Select(p => p.Block + "/" + p.FirstLine + "/" + p.LastLine));

        /// <summary>Source offsets as "start:count" runs (each run counting up from start; -1: none).</summary>
        public static string MapText(int[] source)
        {
            var parts = new List<string>();
            var i = 0;
            while (source != null && i < source.Length)
            {
                var start = source[i];
                var count = 1;
                while (i + count < source.Length && (start < 0 ? source[i + count] < 0 : source[i + count] == start + count))
                    count++;
                parts.Add(start + ":" + count);
                i += count;
            }
            return string.Join(",", parts);
        }

        /// <summary>A text part can't show more characters than this: a map claiming more (a
        /// damaged or forged user string) is no map.</summary>
        public const int MapLimit = 1 << 20;

        public static int[] ParseMap(string text)
        {
            var result = new List<int>();
            foreach (var part in (text ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split(':');
                if (pair.Length != 2 || !int.TryParse(pair[0], NumberStyles.Integer, Invariant, out var start) || !int.TryParse(pair[1], NumberStyles.Integer, Invariant, out var count))
                    return null;
                if (count < 0 || count > MapLimit - result.Count)
                    return null;
                for (var k = 0; k < count; k++)
                    result.Add(start < 0 ? -1 : start + k);
            }
            return result.ToArray();
        }

        public static List<(int Start, int Length, string Url)> ParseLinks(string text)
        {
            var links = new List<(int, int, string)>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                var parts = line.Split(new[] { ' ' }, 3);
                if (parts.Length == 3 && int.TryParse(parts[0], out var start) && int.TryParse(parts[1], out var length))
                    links.Add((start, length, parts[2]));
            }
            return links;
        }
    }
}
