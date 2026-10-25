using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino.Geometry;

namespace MiniVivisection
{
    /// <summary>
    /// The code view's tooltip text for a value while paused (HoverValues), above the
    /// editor's own (the type): the value as a syntax-colored line of code,
    /// <c>count = 5</c>, and for collections and Rhino geometry their key facts --
    /// "3 items", "closed · degree 3 · length 31.4". Compact: no space after commas,
    /// "(1,2,3)"; long collections as their first items and "… +247". Markdown, as
    /// Monaco shows hover contents.
    /// </summary>
    internal static class HoverText
    {
        private const int InlineLength = 80;
        private const int MassPropertiesFaceLimit = 50;
        private const int MaxLines = 16;

        public static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
            "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda",
            "nonlocal", "not", "or", "pass", "print", "raise", "return", "try", "while", "with", "yield", "self",
        };

        /// <summary>The name or member chain at <paramref name="index"/> of a code line:
        /// <c>crv</c>, <c>crv.PointAtStart</c>, <c>curves[0]</c> -- to the left letters,
        /// digits, <c>_ . [ ]</c>, to the right the rest of the word. Null over anything
        /// else, a keyword or a number.</summary>
        public static string ExpressionAt(string line, int index)
        {
            line = (line ?? "").TrimEnd('\r', '\n');
            bool IsWord(int i) => i >= 0 && i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_');
            if (!IsWord(index))
                index--; // the mouse just after the word's last letter
            if (!IsWord(index))
                return null;
            var start = index;
            while (start > 0 && (IsWord(start - 1) || line[start - 1] == '.' || line[start - 1] == '[' || line[start - 1] == ']'))
                start--;
            var end = index;
            while (IsWord(end))
                end++;
            var expression = line.Substring(start, end - start).Trim('.');
            // A leading "]" or "[" from an expression further left: only what parses alone.
            while (expression.Length > 0 && !(char.IsLetter(expression[0]) || expression[0] == '_'))
                expression = expression.Substring(1);
            if (expression.Length == 0 || Keywords.Contains(expression) || expression.Count(c => c == '[') != expression.Count(c => c == ']'))
                return null;
            return expression;
        }

        /// <summary>The whole call when the name at <paramref name="index"/> is called on this
        /// line -- <c>rg.Point3d(ri * math.cos(th), 0, h)</c> for <c>Point3d</c> or <c>rg</c> --
        /// up to its closing parenthesis (parentheses inside strings don't count). Null when
        /// it isn't called here, or the call goes on to the next line.</summary>
        public static string CallAt(string line, int index)
        {
            line = (line ?? "").TrimEnd('\r', '\n');
            var expression = ExpressionAt(line, index);
            if (expression == null)
                return null;
            bool IsWord(int i) => i >= 0 && i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_');
            if (!IsWord(index))
                index--;
            var end = index;
            while (IsWord(end))
                end++;
            // The hovered name's chain: rg -> rg.Point3d (the call is the chain's)
            while (end + 1 < line.Length && line[end] == '.' && IsWord(end + 1))
            {
                end++;
                while (IsWord(end))
                    end++;
            }
            var start = end;
            while (start > 0 && (IsWord(start - 1) || line[start - 1] == '.'))
                start--;
            var open = end;
            while (open < line.Length && line[open] == ' ')
                open++;
            if (open >= line.Length || line[open] != '(')
                return null;
            var depth = 0;
            char quote = '\0';
            for (var i = open; i < line.Length; i++)
            {
                var c = line[i];
                if (quote != '\0')
                {
                    if (c == '\\')
                        i++;
                    else if (c == quote)
                        quote = '\0';
                    continue;
                }
                if (c == '\'' || c == '"')
                    quote = c;
                else if (c == '#')
                    return null; // a comment before the call closes
                else if (c == '(')
                    depth++;
                else if (c == ')' && --depth == 0)
                    return line.Substring(start, i + 1 - start);
            }
            return null;
        }

        /// <summary>A call's short label: <c>rg.Point3d(…)</c>.</summary>
        public static string CallLabel(string call)
        {
            var open = call.IndexOf('(');
            return (open < 0 ? call : call.Substring(0, open).TrimEnd()) + "(…)";
        }

        /// <summary>What a tooltip shows of a value: its text (one line of code), key facts,
        /// the script's language, and the .NET value when it is one.</summary>
        internal sealed class Tip
        {
            public string Text;
            public string Facts;
            public string Language;
            public object Value;
        }

        /// <summary>A Python value at <paramref name="line"/> (scoped as Python sees it
        /// there); throws when it can't be evaluated. Paused thread.</summary>
        public static Tip InfoForPython(string expression, int line, string filename)
        {
            var info = PythonBridge.HoverInfo(expression, line, filename);
            if (info[0] is string text)
                return new Tip { Text = text, Facts = info[1] as string, Language = "python" };
            return InfoForValue(info[2], "python"); // a .NET value
        }

        /// <summary>A .NET value (C# scripts, or .NET values in Python).</summary>
        public static Tip InfoForValue(object value, string language) =>
            new Tip { Text = ShortValue(value, language), Facts = Facts(value), Language = language, Value = value };

        /// <summary>The tooltip for a Python value at <paramref name="line"/>.</summary>
        public static string ForPython(string expression, int line, string filename) => Markdown(expression, InfoForPython(expression, line, filename), null, null);

        /// <summary>The tooltip for a .NET value.</summary>
        public static string ForValue(string expression, object value, string language = "csharp") => Markdown(expression, InfoForValue(value, language), null, null);

        public static string Markdown(string expression, string text, string facts, string language) =>
            Markdown(expression, new Tip { Text = text, Facts = facts, Language = language }, null, null);

        /// <summary>The value as a line of code, syntax-colored by Monaco
        /// (<c>count = 5</c>), its key facts and what it <paramref name="was"/> at the
        /// previous pause in italics below, then <paramref name="links"/> (label, address).
        /// No type: the editor's own tooltip, shown below this one, has it. A text starting
        /// with def / class / import is the whole line.</summary>
        public static string Markdown(string expression, Tip tip, string was, IList<(string Label, string Href)> links)
        {
            var text = Compact((tip.Text ?? "").Replace("\r\n", "\n").Replace('\n', ' ').Trim());
            var code = text.StartsWith("def ", StringComparison.Ordinal) || text.StartsWith("class ", StringComparison.Ordinal) || text.StartsWith("import ", StringComparison.Ordinal)
                ? text
                : expression + " = " + text;
            var markdown = "```" + tip.Language + "\n" + code.Replace("```", "` ` `") + "\n```";
            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(tip.Facts))
                notes.Add(tip.Facts.Trim());
            if (was != null)
                notes.Add("was " + Compact(was.Replace("\r\n", " ").Replace('\n', ' ').Trim()));
            if (notes.Count > 0)
                markdown += "\n\n*" + MarkdownText.Escape(string.Join(" \u00b7 ", notes)) + "*";
            if (links != null && links.Count > 0)
                markdown += "\n\n" + string.Join(" \u00b7 ", links.Select(l => "[" + MarkdownText.Escape(l.Label) + "](" + l.Href + " \"" +
                    LinkTitle(l.Href, l.Label.EndsWith("(\u2026)", StringComparison.Ordinal) ? SubjectOf(l.Href) : expression) + "\")"));
            return markdown;
        }

        // A link's expression (its e= parameter): a call's link is about the call, not the name.
        private static string SubjectOf(string href)
        {
            var start = href.IndexOf("?e=", StringComparison.Ordinal);
            if (start < 0)
                return "";
            start += 3;
            var end = href.IndexOf('&', start);
            return Uri.UnescapeDataString(end < 0 ? href.Substring(start) : href.Substring(start, end - start));
        }

        /// <summary>What a link does, as its title: shown when the mouse is over it, in place
        /// of its address (which TooltipLinks also takes off the rendered link).</summary>
        public static string LinkTitle(string href, string expression)
        {
            var action = href.StartsWith(LinkPrefix, StringComparison.Ordinal) ? href.Substring(LinkPrefix.Length).Split('?')[0] : "";
            var name = expression.Replace("\"", "'");
            switch (action)
            {
                case "watch":
                    return VNames.Text("Add " + name + " to the V Watch tab: drawn in Rhino at every pause");
                case "edit":
                    return "Edit " + name + " as Rhino objects: changes go back into the script";
                case "bake":
                    return "Bake a copy of " + name + " into the document";
                case "tree":
                    return "Show the members of " + name + " in a small window, updated at every pause";
            }
            return name;
        }

        /// <summary>Tooltip links' addresses: never loaded (TooltipLinks catches the clicks).</summary>
        public const string LinkPrefix = "https://minivivisection.invalid/";

        /// <summary>The address of a tooltip link: caught in the code view before Monaco
        /// opens it (TooltipLinks), never loaded.</summary>
        public static string LinkFor(string action, string expression, int line) =>
            LinkPrefix + action + "?e=" + Uri.EscapeDataString(expression) + "&l=" + line.ToString(CultureInfo.InvariantCulture) + "&t=" + LinkToken;

        /// <summary>This session's token in every tooltip link: a link with the prefix from
        /// anywhere else (a docstring the editor shows) isn't acted on.</summary>
        public static readonly string LinkToken = Guid.NewGuid().ToString("N").Substring(0, 12);

        private static string N(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);

        private static string P(Point3d p) => "(" + N(p.X) + "," + N(p.Y) + "," + N(p.Z) + ")";

        private static string P(Vector3d v) => "(" + N(v.X) + "," + N(v.Y) + "," + N(v.Z) + ")";

        private static string Box(BoundingBox box) => box.IsValid ? P(box.Min) + "\u2026" + P(box.Max) : "(empty)";

        /// <summary>A .NET value in one short line: points as (x,y,z), a curve from its
        /// start to its end, other geometry as its bounding box, lists as their first
        /// items and how many more.</summary>
        public static string ShortValue(object value, string language)
        {
            switch (value)
            {
                case null:
                    return language == "python" ? "None" : "null";
                case bool flag:
                    return language == "python" ? (flag ? "True" : "False") : (flag ? "true" : "false");
                case string text:
                    return language == "python" ? "'" + Shorten(text, 60).Replace("'", "\\'") + "'" : "\"" + Shorten(text, 60).Replace("\"", "\\\"") + "\"";
                case double number:
                    return number.ToString("R", CultureInfo.InvariantCulture);
                case float number:
                    return number.ToString("R", CultureInfo.InvariantCulture);
                case IFormattable number when value.GetType().IsPrimitive || value is decimal:
                    return number.ToString(null, CultureInfo.InvariantCulture);
                case Point3d p:
                    return P(p);
                case Point3f p:
                    return P(new Point3d(p));
                case Point p:
                    return P(p.Location);
                case Vector3d v:
                    return P(v);
                case Vector3f v:
                    return P(new Vector3d(v));
                case Line l:
                    return P(l.From) + "\u2192" + P(l.To);
                case Polyline polyline:
                    return polyline.Count > 0 ? P(polyline[0]) + "\u2192" + P(polyline[polyline.Count - 1]) : "(empty)";
                case Circle c:
                    return "center " + P(c.Center) + ", radius " + N(c.Radius);
                case Arc a:
                    return P(a.StartPoint) + "\u2192" + P(a.EndPoint);
                case Plane plane:
                    return "origin " + P(plane.Origin) + ", normal " + P(plane.ZAxis);
                case Interval i:
                    return N(i.T0) + ".." + N(i.T1);
                case BoundingBox box:
                    return Box(box);
                case Curve curve:
                    return P(curve.PointAtStart) + "\u2192" + P(curve.PointAtEnd);
                case GeometryBase geometry:
                    return Box(geometry.GetBoundingBox(false));
                case Guid id:
                    return id.ToString();
                case System.Drawing.Color color:
                    return color.A == 255 ? "rgb(" + color.R + "," + color.G + "," + color.B + ")" : "rgba(" + color.R + "," + color.G + "," + color.B + "," + color.A + ")";
                case IEnumerable items when !(value is string):
                {
                    var all = items.Cast<object>().Take(7).ToList();
                    var count = value is ICollection collection ? collection.Count : all.Count;
                    var shown = (count > 6 ? all.Take(3) : all).Select(v => ShortValue(v, language)).ToList();
                    if (count > 6)
                        shown.Add("\u2026 +" + (count - 3));
                    return "[" + string.Join(",", shown) + "]";
                }
            }
            return Shorten(ValueText.Describe(value).Split('\n')[0], 60);
        }

        /// <summary>The key facts of a .NET value -- "closed · degree 3 · length 31.4 · 12
        /// control points" -- or null.</summary>
        public static string Facts(object value)
        {
            var facts = new List<string>();
            switch (value)
            {
                case string text when text.Length > 20:
                    facts.Add(text.Length + " characters");
                    break;
                case Vector3d v:
                    facts.Add("length " + N(v.Length));
                    break;
                case Line l:
                    facts.Add("length " + N(l.Length));
                    break;
                case Polyline polyline:
                    facts.Add(polyline.IsClosed ? "closed" : "open");
                    facts.Add(polyline.Count + " points");
                    facts.Add("length " + N(polyline.Length));
                    break;
                case Circle c:
                    facts.Add("circumference " + N(c.Circumference));
                    break;
                case Arc a:
                    facts.Add("radius " + N(a.Radius));
                    facts.Add(N(a.AngleDegrees) + "\u00b0");
                    facts.Add("length " + N(a.Length));
                    break;
                case Interval i:
                    facts.Add("length " + N(i.Length));
                    break;
                case BoundingBox box when box.IsValid:
                    facts.Add("size " + N(box.Diagonal.X) + "\u00d7" + N(box.Diagonal.Y) + "\u00d7" + N(box.Diagonal.Z));
                    break;
                case Curve curve:
                    facts.Add(curve.IsClosed ? "closed" : "open");
                    facts.Add("degree " + curve.Degree);
                    facts.Add("length " + N(curve.GetLength()));
                    if (curve is NurbsCurve nurbs)
                        facts.Add(nurbs.Points.Count + " control points");
                    else if (curve is PolylineCurve polylineCurve)
                        facts.Add(polylineCurve.PointCount + " points");
                    else if (curve is PolyCurve poly)
                        facts.Add(poly.SegmentCount + " segments");
                    break;
                case Brep brep:
                    facts.Add(brep.IsSolid ? "solid" : "open");
                    facts.Add(brep.Faces.Count + (brep.Faces.Count == 1 ? " face" : " faces"));
                    // Mass properties only for light Breps: this runs on the paused thread, at
                    // every hover, and a tooltip waits 1.5 s at most.
                    if (brep.Faces.Count > MassPropertiesFaceLimit)
                        facts.Add("size " + Size(brep.GetBoundingBox(false)));
                    else if (brep.IsSolid)
                        facts.Add("volume " + N(brep.GetVolume()));
                    else
                        facts.Add("area " + N(brep.GetArea()));
                    break;
                case Extrusion extrusion:
                    facts.Add(extrusion.IsSolid ? "solid" : "open");
                    facts.Add("height " + N(extrusion.PathStart.DistanceTo(extrusion.PathEnd)));
                    break;
                case Surface surface:
                    facts.Add("degree " + surface.Degree(0) + "\u00d7" + surface.Degree(1));
                    facts.Add("size " + Size(surface.GetBoundingBox(false)));
                    break;
                case Mesh mesh:
                    facts.Add(mesh.IsClosed ? "closed" : "open");
                    facts.Add(mesh.Vertices.Count + " vertices");
                    facts.Add(mesh.Faces.Count + " faces");
                    break;
                case PointCloud cloud:
                    facts.Add(cloud.Count + " points");
                    break;
                case GeometryBase geometry:
                    facts.Add("size " + Size(geometry.GetBoundingBox(false)));
                    break;
                case ICollection collection when !(value is string):
                    facts.Add(collection.Count + (collection.Count == 1 ? " item" : " items"));
                    break;
            }
            return facts.Count == 0 ? null : string.Join(" \u00b7 ", facts);
        }

        private static string Size(BoundingBox box) => box.IsValid ? N(box.Diagonal.X) + "\u00d7" + N(box.Diagonal.Y) + "\u00d7" + N(box.Diagonal.Z) : "empty";

        private static string Shorten(string text, int limit) => text.Length <= limit ? text : text.Substring(0, limit - 1) + "\u2026";

        /// <summary>No space after a comma, except inside quoted text: "(1,2,3)",
        /// "['a, b',2]".</summary>
        public static string Compact(string text)
        {
            var result = new System.Text.StringBuilder(text.Length);
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                result.Append(c);
                if (quote != '\0')
                {
                    if (c == '\\' && i + 1 < text.Length)
                        result.Append(text[++i]);
                    else if (c == quote)
                        quote = '\0';
                }
                else if (c == '\'' || c == '"')
                    quote = c;
                else if (c == ',' && i + 1 < text.Length && text[i + 1] == ' ')
                    i++;
            }
            return result.ToString();
        }
    }

    /// <summary>Text from the script (values, messages) in Monaco's Markdown: shown as text --
    /// no link, image or emphasis made of it.</summary>
    internal static class MarkdownText
    {
        public static string Escape(string text)
        {
            var result = new System.Text.StringBuilder();
            foreach (var c in text ?? "")
            {
                if ("\\`*_{}[]()<>#+-.!|~".IndexOf(c) >= 0)
                    result.Append('\\');
                result.Append(c);
            }
            return result.ToString();
        }
    }
}
