using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MiniVivisection
{
    /// <summary>
    /// How Markdown is drawn -- the sizes, fonts, colors, spacing and characters of its elements --
    /// read from a small part of CSS (styles/markdown_rhino.css, markdown_grasshopper.css: the
    /// defaults, also inside the assembly): rules of element names (h1..h6, p, li, task,
    /// blockquote, pre, code, a, table, th, hr, body; several with commas) and the properties in
    /// <see cref="Kinds"/>. No classes, no nesting; body's font-family and color are inherited.
    /// What isn't known is left out and said in <see cref="Problems"/>. Lengths in em (one base
    /// text height: RhinoTextLayout's text height, MarkdownView's font size); a plain number is
    /// em, 120% 1.2em. A value missing: the caller's fallback -- the default's -- so an empty
    /// style draws as before. Shared: the .rhp (MarkdownText), the .gha (Panels, V Text), the checks.
    ///
    /// A default file's first line says so with a checksum of the rest (tools/stamp_styles.py):
    /// a text that still matches it is an unchanged default (<see cref="IsPristineDefault"/>),
    /// whatever version it is from.
    /// </summary>
    internal sealed class MarkdownStyle
    {
        public enum Target
        {
            Rhino,
            Grasshopper,
        }

        private enum ValueKind
        {
            Length,
            Lengths,   // 1 to 4 lengths: padding, margin
            Weight,
            FontStyle,
            Font,
            Color,
            Characters, // strings, or one of the keywords
            Keyword,
        }

        public static readonly string[] Elements = { "body", "h1", "h2", "h3", "h4", "h5", "h6", "p", "li", "task", "blockquote", "pre", "code", "a", "table", "th", "hr" };

        private static readonly Dictionary<string, (ValueKind Kind, string[] Keywords)> Kinds = new Dictionary<string, (ValueKind, string[])>
        {
            ["font-family"] = (ValueKind.Font, null),
            ["font-size"] = (ValueKind.Length, null),
            ["font-weight"] = (ValueKind.Weight, null),
            ["font-style"] = (ValueKind.FontStyle, null),
            ["color"] = (ValueKind.Color, null),
            ["background"] = (ValueKind.Keyword, new[] { "shade", "none" }),
            ["margin"] = (ValueKind.Lengths, null),
            ["margin-top"] = (ValueKind.Length, null),
            ["margin-bottom"] = (ValueKind.Length, null),
            ["margin-left"] = (ValueKind.Length, null),
            ["padding"] = (ValueKind.Lengths, null),
            ["padding-top"] = (ValueKind.Length, null),
            ["padding-bottom"] = (ValueKind.Length, null),
            ["padding-left"] = (ValueKind.Length, null),
            ["padding-right"] = (ValueKind.Length, null),
            ["border"] = (ValueKind.Keyword, new[] { "frame", "grid", "none" }),
            ["border-bottom"] = (ValueKind.Keyword, new[] { "line", "none" }),
            ["list-style-type"] = (ValueKind.Characters, new[] { "none" }),
            ["marker-width"] = (ValueKind.Length, null),
            ["checked"] = (ValueKind.Characters, new[] { "drawn" }),
            ["unchecked"] = (ValueKind.Characters, new[] { "drawn" }),
            ["size"] = (ValueKind.Length, null),
            ["bar"] = (ValueKind.Characters, new[] { "line", "none" }),
            ["bar-width"] = (ValueKind.Length, null),
            ["bar-weight"] = (ValueKind.Weight, null),
            ["text-decoration"] = (ValueKind.Keyword, new[] { "underline", "none" }),
            ["text-decoration-thickness"] = (ValueKind.Length, null),
            ["text-underline-offset"] = (ValueKind.Length, null),
        };

        // Inherited from body when an element doesn't say
        private static readonly HashSet<string> Inherited = new HashSet<string> { "font-family", "color" };

        private readonly Dictionary<string, Dictionary<string, string>> _rules = new Dictionary<string, Dictionary<string, string>>();
        private readonly List<string> _problems = new List<string>();

        /// <summary>The CSS it was read from.</summary>
        public string Css { get; }

        /// <summary>What was left out, one line each ("line 12: h7: no such element").</summary>
        public IReadOnlyList<string> Problems => _problems;

        private MarkdownStyle(string css)
        {
            Css = css ?? "";
        }

        // --- reading ------------------------------------------------------------------------------

        private static readonly Dictionary<string, MarkdownStyle> s_parsed = new Dictionary<string, MarkdownStyle>();

        /// <summary>The style of <paramref name="css"/> (the same object for the same text: the
        /// last few kept).</summary>
        public static MarkdownStyle Parse(string css)
        {
            css = css ?? "";
            lock (s_parsed)
            {
                if (s_parsed.TryGetValue(css, out var known))
                    return known;
                var style = new MarkdownStyle(css);
                style.Read();
                if (s_parsed.Count >= 16)
                    s_parsed.Clear();
                s_parsed[css] = style;
                return style;
            }
        }

        private void Read()
        {
            var text = Css.Replace("\r\n", "\n");
            var i = 0;
            var line = 1;
            string Take(Func<char, bool> until)
            {
                var b = new StringBuilder();
                char quote = '\0';
                while (i < text.Length)
                {
                    var c = text[i];
                    if (quote == '\0' && c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                    {
                        var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        end = end < 0 ? text.Length : end + 2;
                        line += text.Substring(i, end - i).Count(ch => ch == '\n');
                        i = end;
                        b.Append(' ');
                        continue;
                    }
                    if (quote == '\0' && until(c))
                        break;
                    if (c == '\n')
                        line++;
                    if (quote != '\0' && c == '\\' && i + 1 < text.Length)
                    {
                        b.Append(c).Append(text[i + 1]);
                        i += 2;
                        continue;
                    }
                    if (c == '"' || c == '\'')
                        quote = quote == '\0' ? c : quote == c ? '\0' : quote;
                    b.Append(c);
                    i++;
                }
                return b.ToString();
            }

            while (i < text.Length)
            {
                var at = line;
                var selector = Take(c => c == '{' || c == '}').Trim();
                if (i >= text.Length)
                {
                    if (selector.Length > 0)
                        _problems.Add("line " + at + ": \"" + Short(selector) + "\" has no { ... }");
                    break;
                }
                if (text[i] == '}')
                {
                    _problems.Add("line " + line + ": a } too many");
                    i++;
                    continue;
                }
                i++; // {
                var body = new List<(string Text, int Line)>();
                while (i < text.Length && text[i] != '}')
                {
                    while (i < text.Length && char.IsWhiteSpace(text[i]))
                    {
                        if (text[i] == '\n')
                            line++;
                        i++;
                    }
                    var declarationLine = line;
                    var declaration = Take(c => c == ';' || c == '}');
                    if (declaration.Trim().Length > 0)
                        body.Add((declaration.Trim(), declarationLine));
                    if (i < text.Length && text[i] == ';')
                        i++;
                }
                if (i >= text.Length)
                    _problems.Add("line " + at + ": \"" + Short(selector) + "\": no closing }");
                i++; // }
                var elements = new List<string>();
                foreach (var name in selector.Split(','))
                {
                    var element = name.Trim().ToLowerInvariant();
                    if (element.Length == 0)
                        continue;
                    if (Elements.Contains(element))
                        elements.Add(element);
                    else
                        _problems.Add("line " + at + ": " + element + ": no such element (" + string.Join(", ", Elements) + ")");
                }
                foreach (var (declaration, where) in body)
                {
                    var colon = declaration.IndexOf(':');
                    if (colon <= 0)
                    {
                        _problems.Add("line " + where + ": \"" + Short(declaration) + "\" -- property: value");
                        continue;
                    }
                    var property = declaration.Substring(0, colon).Trim().ToLowerInvariant();
                    var value = declaration.Substring(colon + 1).Trim();
                    if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase))
                        value = value.Substring(0, value.Length - "!important".Length).Trim();
                    if (!Kinds.TryGetValue(property, out var kind))
                    {
                        _problems.Add("line " + where + ": " + property + ": no such property");
                        continue;
                    }
                    if (!Valid(kind.Kind, kind.Keywords, value, out var why))
                    {
                        _problems.Add("line " + where + ": " + property + ": " + value + " -- " + why);
                        continue;
                    }
                    foreach (var element in elements)
                    {
                        if (!_rules.TryGetValue(element, out var rule))
                            _rules[element] = rule = new Dictionary<string, string>();
                        foreach (var (name, part) in Expand(property, value))
                            rule[name] = part;
                    }
                }
            }
        }

        private static string Short(string text)
        {
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 40 ? text.Substring(0, 40) + "..." : text;
        }

        // padding and margin: 1 to 4 lengths, as CSS has them (top right bottom left)
        private static IEnumerable<(string, string)> Expand(string property, string value)
        {
            if (property != "padding" && property != "margin")
            {
                yield return (property, value);
                yield break;
            }
            var v = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var (top, right, bottom, left) = v.Length == 1 ? (v[0], v[0], v[0], v[0]) : v.Length == 2 ? (v[0], v[1], v[0], v[1]) : v.Length == 3 ? (v[0], v[1], v[2], v[1]) : (v[0], v[1], v[2], v[3]);
            yield return (property + "-top", top);
            yield return (property + "-bottom", bottom);
            yield return (property + "-left", left);
            if (property == "padding")
                yield return (property + "-right", right);
        }

        private static bool Valid(ValueKind kind, string[] keywords, string value, out string why)
        {
            why = null;
            var lower = value.ToLowerInvariant();
            switch (kind)
            {
                case ValueKind.Length:
                    why = "a length: 1.2em, 1.2 or 120%";
                    return ParseLength(value).HasValue;
                case ValueKind.Lengths:
                {
                    var parts = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    why = "one to four lengths: 0.5em 0.6em";
                    return parts.Length >= 1 && parts.Length <= 4 && parts.All(p => ParseLength(p).HasValue);
                }
                case ValueKind.Weight:
                    why = "bold or normal (or 100 to 900)";
                    return lower == "bold" || lower == "bolder" || lower == "normal" || lower == "lighter" || (int.TryParse(lower, out var w) && w >= 1 && w <= 1000);
                case ValueKind.FontStyle:
                    why = "italic or normal";
                    return lower == "italic" || lower == "oblique" || lower == "normal";
                case ValueKind.Font:
                    why = "a font's name, or inherit";
                    return Unquote(value.Split(',')[0]).Trim().Length > 0;
                case ValueKind.Color:
                    why = "a color: a name, #RRGGBB, #RGB, rgb(r, g, b), or none";
                    return lower == "none" || lower == "inherit" || ParseColor(value).HasValue;
                case ValueKind.Keyword:
                    why = string.Join(" or ", keywords);
                    return keywords.Contains(lower);
                case ValueKind.Characters:
                    why = "characters in quotes (\"-\", \"\\2022\"), or " + string.Join(" or ", keywords);
                    return keywords.Contains(lower) || Strings(value) != null;
            }
            return false;
        }

        // --- the values ---------------------------------------------------------------------------

        /// <summary>The value as written for <paramref name="element"/> (body's for the inherited
        /// font-family and color); null: none.</summary>
        public string Raw(string element, string property)
        {
            if (_rules.TryGetValue(element, out var rule) && rule.TryGetValue(property, out var value))
                return value;
            if (element != "body" && Inherited.Contains(property) && _rules.TryGetValue("body", out var body) && body.TryGetValue(property, out var inherited))
                return inherited;
            return null;
        }

        /// <summary>A length in em (a plain number too; 120%: 1.2).</summary>
        public double Length(string element, string property, double fallback) => ParseLength(Raw(element, property)) ?? fallback;

        /// <summary>A heading's element: h1 .. h6.</summary>
        public static string Heading(int level) => "h" + Math.Max(1, Math.Min(6, level));

        public bool Bold(string element, bool fallback, string property = "font-weight")
        {
            var value = Raw(element, property)?.ToLowerInvariant();
            if (value == null)
                return fallback;
            return value == "bold" || value == "bolder" || (int.TryParse(value, out var w) && w >= 600);
        }

        public bool Italic(string element, bool fallback)
        {
            var value = Raw(element, "font-style")?.ToLowerInvariant();
            return value == null ? fallback : value == "italic" || value == "oblique";
        }

        /// <summary>A font's name (the first of a list); null: inherit -- the base font.</summary>
        public string Font(string element)
        {
            var value = Raw(element, "font-family");
            if (value == null)
                return null;
            var name = Unquote(value.Split(',')[0]).Trim();
            return name.Length == 0 || name.Equals("inherit", StringComparison.OrdinalIgnoreCase) ? null : name;
        }

        /// <summary>A color; Color.Empty: none (the layer's, the Panel's text color).</summary>
        public Color ColorOf(string element) => ParseColor(Raw(element, "color")) ?? Color.Empty;

        /// <summary>A keyword, lower case; <paramref name="fallback"/> when none.</summary>
        public string Keyword(string element, string property, string fallback) => Raw(element, property)?.ToLowerInvariant() ?? fallback;

        /// <summary>A property of characters: its strings (null: a keyword -- see Keyword).</summary>
        public List<string> Characters(string element, string property)
        {
            var value = Raw(element, property);
            return value == null ? null : Strings(value);
        }

        /// <summary>The bullet of a list item <paramref name="level"/> deep (0: the top), or null:
        /// none (list-style-type: none).</summary>
        public string Bullet(int level, string fallback = "\u2022")
        {
            if (Keyword("li", "list-style-type", null) == "none")
                return null;
            var bullets = Characters("li", "list-style-type");
            return bullets == null || bullets.Count == 0 ? fallback : bullets[Math.Min(Math.Max(0, level), bullets.Count - 1)];
        }

        /// <summary>A task's box: null when drawn, else its character(s).</summary>
        public string TaskMark(bool done)
        {
            var property = done ? "checked" : "unchecked";
            var marks = Characters("task", property);
            return marks == null || marks.Count == 0 ? null : string.Concat(marks);
        }

        /// <summary>A quote's bar: null -- a line; "" -- none; else its character(s).</summary>
        public string QuoteBar(string fallback)
        {
            var value = Raw("blockquote", "bar");
            if (value == null)
                return fallback;
            var lower = value.ToLowerInvariant();
            if (lower == "line")
                return null;
            if (lower == "none")
                return "";
            var marks = Strings(value);
            return marks == null ? fallback : string.Concat(marks);
        }

        /// <summary>The links' color: their style's (the default's without one).</summary>
        public Color LinkColor
        {
            get
            {
                var color = ColorOf("a");
                return color.IsEmpty ? DefaultLinkColor : color;
            }
        }

        public static readonly Color DefaultLinkColor = Color.FromArgb(26, 77, 143);

        // --- parsing values -----------------------------------------------------------------------

        public static double? ParseLength(string value)
        {
            if (value == null)
                return null;
            var v = value.Trim().ToLowerInvariant();
            var scale = 1.0;
            if (v.EndsWith("em", StringComparison.Ordinal))
                v = v.Substring(0, v.Length - 2);
            else if (v.EndsWith("%", StringComparison.Ordinal))
            {
                v = v.Substring(0, v.Length - 1);
                scale = 0.01;
            }
            return double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && !double.IsNaN(number) && !double.IsInfinity(number) && number >= -100 && number <= 100 / scale
                ? number * scale : (double?)null;
        }

        public static Color? ParseColor(string value)
        {
            if (value == null)
                return null;
            var v = value.Trim();
            var rgb = Regex.Match(v, @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)$", RegexOptions.IgnoreCase);
            if (rgb.Success)
            {
                var parts = new[] { rgb.Groups[1], rgb.Groups[2], rgb.Groups[3] }.Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
                return parts.All(p => p <= 255) ? Color.FromArgb(parts[0], parts[1], parts[2]) : (Color?)null;
            }
            if (Regex.IsMatch(v, "^#[0-9a-fA-F]{3}$"))
                v = "#" + v[1] + v[1] + v[2] + v[2] + v[3] + v[3];
            if (Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$"))
                return Color.FromArgb(255, Color.FromArgb(int.Parse(v.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            if (Regex.IsMatch(v, "^[A-Za-z]+$"))
            {
                var named = Color.FromName(v);
                if (named.IsKnownColor && !named.IsSystemColor)
                    return Color.FromArgb(named.R, named.G, named.B);
            }
            return null;
        }

        // "a" "\2022" 'b' -> its strings, CSS escapes read (\hex then an optional space; \" a quote); null: not strings
        private static List<string> Strings(string value)
        {
            var result = new List<string>();
            var i = 0;
            while (i < value.Length)
            {
                var c = value[i];
                if (char.IsWhiteSpace(c) || c == ',')
                {
                    i++;
                    continue;
                }
                if (c != '"' && c != '\'')
                    return null;
                var quote = c;
                var b = new StringBuilder();
                i++;
                var closed = false;
                while (i < value.Length)
                {
                    var d = value[i];
                    if (d == quote)
                    {
                        closed = true;
                        i++;
                        break;
                    }
                    if (d == '\\' && i + 1 < value.Length)
                    {
                        var hex = 0;
                        while (hex < 6 && i + 1 + hex < value.Length && Uri.IsHexDigit(value[i + 1 + hex]))
                            hex++;
                        if (hex > 0)
                        {
                            var code = int.Parse(value.Substring(i + 1, hex), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            b.Append(code > 0 && code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF) ? char.ConvertFromUtf32(code) : "\uFFFD");
                            i += 1 + hex;
                            if (i < value.Length && value[i] == ' ')
                                i++;
                            continue;
                        }
                        b.Append(value[i + 1]);
                        i += 2;
                        continue;
                    }
                    b.Append(d);
                    i++;
                }
                if (!closed)
                    return null;
                result.Add(b.ToString());
            }
            return result.Count > 0 ? result : null;
        }

        private static string Unquote(string value)
        {
            value = (value ?? "").Trim();
            return value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0] ? value.Substring(1, value.Length - 2) : value;
        }

        // --- the defaults -------------------------------------------------------------------------

        /// <summary>The default style's file name (styles/ next to the plug-in, and inside the assembly).</summary>
        public static string DefaultFileName(Target target) => target == Target.Rhino ? "markdown_rhino.css" : "markdown_grasshopper.css";

        private static readonly Dictionary<Target, string> s_defaults = new Dictionary<Target, string>();

        /// <summary>The default style's CSS, from inside the assembly.</summary>
        public static string DefaultCss(Target target)
        {
            lock (s_defaults)
            {
                if (s_defaults.TryGetValue(target, out var known))
                    return known;
                var css = "";
                try
                {
                    using var stream = typeof(MarkdownStyle).Assembly.GetManifestResourceStream(DefaultFileName(target));
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        css = reader.ReadToEnd().Replace("\r\n", "\n");
                    }
                }
                catch (Exception)
                {
                    // an empty style: the code's fallbacks, which are the default's
                }
                s_defaults[target] = css;
                return css;
            }
        }

        public static MarkdownStyle Default(Target target) => Parse(DefaultCss(target));

        private static readonly Regex Stamp = new Regex(@"^/\* miniVivisection default Markdown style for (Rhino|Grasshopper) -- checksum ([0-9a-f]{16})\b", RegexOptions.CultureInvariant);

        /// <summary>The checksum of a default style's text after its first line (tools/stamp_styles.py: the same).</summary>
        public static string Checksum(string body)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes((body ?? "").Replace("\r\n", "\n").TrimEnd()));
            return string.Concat(hash.Take(8).Select(b => b.ToString("x2")));
        }

        /// <summary>Whether <paramref name="css"/> is a default style as it was shipped -- of any
        /// version: its first line's checksum is its text's.</summary>
        public static bool IsPristineDefault(string css)
        {
            var text = (css ?? "").Replace("\r\n", "\n");
            var newline = text.IndexOf('\n');
            var match = Stamp.Match(newline < 0 ? text : text.Substring(0, newline));
            return match.Success && newline >= 0 && Checksum(text.Substring(newline + 1)) == match.Groups[2].Value;
        }

        /// <summary>Which default <paramref name="css"/> is stamped as (null: none).</summary>
        public static Target? StampedAs(string css)
        {
            var match = Stamp.Match((css ?? "").TrimStart());
            return !match.Success ? (Target?)null : match.Groups[1].Value == "Rhino" ? Target.Rhino : Target.Grasshopper;
        }

        /// <summary><paramref name="css"/> as one's own: a default's stamp line (read-only, the
        /// checksum) replaced by a plain note -- for a copy saved as a file of one's own.</summary>
        public static string AsOwn(string css)
        {
            var text = (css ?? "").Replace("\r\n", "\n");
            var target = StampedAs(text);
            if (target == null)
                return text;
            var newline = text.IndexOf('\n');
            return "/* Markdown style for " + target + ", made from miniVivisection's default */" + (newline < 0 ? "" : text.Substring(newline));
        }
    }
}
