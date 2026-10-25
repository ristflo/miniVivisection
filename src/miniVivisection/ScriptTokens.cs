using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MiniVivisection
{
    /// <summary>
    /// Scripts in Grasshopper Panels: which language a text is (Python or C#), and each line cut
    /// into colored pieces -- keywords, built-ins, strings, comments, numbers, names defined or
    /// called, decorators and preprocessor lines -- for the Panel's code display
    /// (miniVivisection.GH's ScriptPanels). A simple scanner, line by line, carrying what spans
    /// lines: Python's triple-quoted strings, C#'s block comments and verbatim strings. Compiled
    /// into the .rhp (its checks) and linked into the .gha.
    /// </summary>
    internal static class ScriptTokens
    {
        public enum Language
        {
            None,
            Python,
            CSharp,
            Markdown, // a Panel's "Show as" Markdown: its source, when edited
        }

        public enum Kind
        {
            Plain,
            Keyword,
            Builtin,
            String,
            Comment,
            Number,
            Definition, // a name defined (def, class) or called: name(
            Decorator,  // Python's @decorator, C#'s #region and other preprocessor lines
        }

        public readonly struct Token
        {
            public Token(int start, int length, Kind kind)
            {
                Start = start;
                Length = length;
                Kind = kind;
            }

            public int Start { get; }

            public int Length { get; }

            public Kind Kind { get; }

            public override string ToString() => Kind + "@" + Start + "+" + Length;
        }

        public const int TabSize = 4;

        // --- which language --------------------------------------------------------------------

        // Lines that only Python has: imports, def, class, a block's colon.
        private static readonly Regex PythonLine = new Regex(
            @"^(import [A-Za-z_][\w.]*(\s+as\s+\w+)?(\s*,\s*[A-Za-z_][\w.]*)*$|from [\w.]+ import |def \w+\s*\(.*\)\s*(->.*)?:$|class \w+(\(.*\))?:$|if __name__\s*==|for [\w, ()]+ in .+:$|while .+:$|elif .+:$|else:$|try:$|except\b.*:$|with .+:$)",
            RegexOptions.CultureInvariant);

        // Lines that only C# has: usings, namespaces, members, statements ending in ';', braces.
        private static readonly Regex CSharpLine = new Regex(
            @"^(using (static )?[\w.]+( = [\w.<>]+)?;$|namespace [\w.]+|(public|private|protected|internal)\s+[\w<>\[\],.?\s]+[({;=]|(var|int|double|string|bool|float|long)\s+\w+\s*=.*;$|foreach\s*\(.*\)|for\s*\(.*;.*;.*\)|if\s*\(.*\)\s*\{?$|return\b.*;$|\{|\}|\};?)$",
            RegexOptions.CultureInvariant);

        /// <summary>The language of <paramref name="text"/>: RhinoCode's own marker on its first
        /// line ("#! python 3", "#! python3", "#! python 2"; "// #! csharp"), else at least two
        /// lines only that language has, and more than of the other; else Markdown when it
        /// plainly is (<see cref="LooksLikeMarkdown"/>). None for anything else -- notes, data,
        /// lessons quoting a line of code.</summary>
        /// <param name="text">The text.</param>
        /// <param name="data">The text is a Panel's data (strings, numbers, JSON...), not its own
        /// text: a script only by its marker line (a brace alone is C#'s sign, and JSON has
        /// them), Markdown only when it opens with a heading, a list, a quote or a fence.</param>
        public static Language Detect(string text, bool data = false)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Language.None;
            var lines = Lines(text);
            var first = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            if (Regex.IsMatch(first, @"^#!\s*python", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return Language.Python;
            if (Regex.IsMatch(first, @"^//\s*#!\s*(csharp|c#)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                first.StartsWith("// Grasshopper Script Instance", StringComparison.Ordinal))
                return Language.CSharp;
            if (data)
            {
                var opens = MdHeadingLine.IsMatch(first) || MdListLine.IsMatch(first) || MdQuoteLine.IsMatch(first) || MdFenceLine.IsMatch(first);
                return opens && LooksLikeMarkdown(lines) ? Language.Markdown : Language.None;
            }
            int python = 0, csharp = 0;
            foreach (var line in lines.Take(400))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                    continue;
                if (PythonLine.IsMatch(trimmed))
                    python++;
                if (CSharpLine.IsMatch(trimmed))
                    csharp++;
            }
            if (python >= 2 && python > csharp)
                return Language.Python;
            if (csharp >= 2 && csharp > python)
                return Language.CSharp;
            return LooksLikeMarkdown(lines) ? Language.Markdown : Language.None;
        }

        private static readonly Regex MdFenceLine = new Regex(@"^\s{0,3}(```|~~~)", RegexOptions.CultureInvariant);
        private static readonly Regex MdHeadingLine = new Regex(@"^\s{0,3}#{1,6}\s+\S", RegexOptions.CultureInvariant);
        private static readonly Regex MdListLine = new Regex(@"^\s{0,3}([-*+]|\d{1,3}[.)])\s+\S", RegexOptions.CultureInvariant);
        private static readonly Regex MdQuoteLine = new Regex(@"^\s{0,3}>(?!>)\s?\S", RegexOptions.CultureInvariant);
        private static readonly Regex MdTableRule = new Regex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex MdLink = new Regex(@"!?\[[^\]\n]+\]\([^)\s]+\)", RegexOptions.CultureInvariant);
        // **bold** -- not Python's x**2 (no letter, digit or bracket right before the stars)
        private static readonly Regex MdBold = new Regex(@"(?<![\w)\]])(\*\*|__)(?=\S)[^\n]+?(?<=\S)\1(?![\w(])", RegexOptions.CultureInvariant);
        private static readonly Regex MdCodeSpan = new Regex(@"`[^`\s][^`\n]*`", RegexOptions.CultureInvariant);

        /// <summary>Whether lines are plainly Markdown: two or more kinds of strong signs -- a code
        /// fence, a link, **bold**, a `code span`, a table's rule, a quote, two list lines -- or
        /// one of them and a # heading. A heading alone isn't (a Python comment looks the same),
        /// nor a numbered list alone (a lesson's steps).</summary>
        public static bool LooksLikeMarkdown(IReadOnlyList<string> lines)
        {
            int headings = 0, listLines = 0;
            bool fence = false, link = false, bold = false, code = false, table = false, quote = false;
            foreach (var line in lines.Take(400))
            {
                if (MdFenceLine.IsMatch(line))
                    fence = true;
                if (MdHeadingLine.IsMatch(line))
                    headings++;
                if (MdListLine.IsMatch(line))
                    listLines++;
                if (MdQuoteLine.IsMatch(line))
                    quote = true;
                if (MdTableRule.IsMatch(line))
                    table = true;
                link |= MdLink.IsMatch(line);
                bold |= MdBold.IsMatch(line);
                code |= MdCodeSpan.IsMatch(line);
            }
            var strong = new[] { fence, link, bold, code, table, quote, listLines >= 2 }.Count(b => b);
            return strong >= 2 || (strong >= 1 && headings >= 1);
        }

        /// <summary>The text's lines (any line break), tabs not expanded.</summary>
        public static string[] Lines(string text) =>
            (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        /// <summary><paramref name="line"/> with its tabs as spaces, to the next multiple of
        /// <see cref="TabSize"/>: the columns the display draws.</summary>
        public static string ExpandTabs(string line)
        {
            if (line.IndexOf('\t') < 0)
                return line;
            var expanded = new StringBuilder(line.Length + 8);
            foreach (var c in line)
            {
                if (c == '\t')
                    expanded.Append(' ', TabSize - expanded.Length % TabSize);
                else
                    expanded.Append(c);
            }
            return expanded.ToString();
        }

        // --- colored pieces -------------------------------------------------------------------

        private static readonly HashSet<string> PythonKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
            "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal",
            "not", "or", "pass", "raise", "return", "try", "while", "with", "yield", "match", "case",
        };

        private static readonly HashSet<string> PythonBuiltins = new HashSet<string>(StringComparer.Ordinal)
        {
            "self", "cls", "print", "len", "range", "enumerate", "zip", "list", "dict", "set", "tuple", "str", "int", "float",
            "bool", "bytes", "object", "type", "isinstance", "issubclass", "super", "open", "map", "filter", "sum", "min", "max",
            "abs", "round", "sorted", "reversed", "any", "all", "iter", "next", "repr", "hasattr", "getattr", "setattr",
            "Exception", "ValueError", "TypeError", "KeyError", "IndexError", "RuntimeError", "StopIteration", "NotImplementedError",
        };

        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
            "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
            "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
            "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "virtual", "void", "volatile", "while", "var", "dynamic", "async", "await", "yield", "get", "set", "init",
            "value", "nameof", "when", "where", "record", "partial", "global",
        };

        private static readonly HashSet<string> CSharpBuiltins = new HashSet<string>(StringComparer.Ordinal)
        {
            "Math", "Console", "String", "Object", "List", "Dictionary", "HashSet", "Array", "Enumerable", "IEnumerable",
            "Exception", "DateTime", "TimeSpan", "Guid", "Color", "Rhino", "RhinoApp", "RhinoDoc", "RhinoMath", "Point3d",
            "Point3f", "Vector3d", "Vector3f", "Line", "Plane", "Curve", "NurbsCurve", "LineCurve", "PolylineCurve", "Polyline",
            "Circle", "Arc", "Brep", "Mesh", "Surface", "NurbsSurface", "Transform", "Interval", "BoundingBox", "Box", "TextDot",
            "DataTree", "GH_Path", "GH_ScriptInstance", "IGH_Component", "Grasshopper", "Print", "Component",
        };

        private static readonly HashSet<string> CSharpDefiners = new HashSet<string>(StringComparer.Ordinal)
        {
            "class", "struct", "interface", "enum", "record", "namespace",
        };

        /// <summary>The colored pieces of each line (tabs expanded first:
        /// <see cref="ExpandTabs"/>), in order, without the plain text between them.</summary>
        public static List<Token>[] Tokenize(IReadOnlyList<string> lines, Language language)
        {
            var result = new List<Token>[lines.Count];
            var state = 0; // what goes on from the line before (each language's own)
            for (var i = 0; i < lines.Count; i++)
            {
                var tokens = new List<Token>();
                var line = lines[i] ?? "";
                if (language == Language.Python)
                    state = PythonLineTokens(line, state, tokens);
                else if (language == Language.CSharp)
                    state = CSharpLineTokens(line, state, tokens);
                else if (language == Language.Markdown)
                    state = MarkdownLineTokens(line, state, tokens);
                result[i] = tokens;
            }
            return result;
        }

        private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';

        private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_';

        // A number at i: digits, a point, an exponent, 0x.., underscores, a type suffix.
        private static int NumberEnd(string line, int i)
        {
            var n = line.Length;
            var j = i;
            if (j + 1 < n && line[j] == '0' && (line[j + 1] == 'x' || line[j + 1] == 'X' || line[j + 1] == 'b' || line[j + 1] == 'B'))
            {
                j += 2;
                while (j < n && (Uri.IsHexDigit(line[j]) || line[j] == '_'))
                    j++;
            }
            else
            {
                while (j < n && (char.IsDigit(line[j]) || line[j] == '_'))
                    j++;
                if (j < n && line[j] == '.' && (j + 1 >= n || char.IsDigit(line[j + 1]) || !IsNameStart(line[j + 1])))
                {
                    j++;
                    while (j < n && (char.IsDigit(line[j]) || line[j] == '_'))
                        j++;
                }
                if (j < n && (line[j] == 'e' || line[j] == 'E'))
                {
                    var k = j + 1;
                    if (k < n && (line[k] == '+' || line[k] == '-'))
                        k++;
                    if (k < n && char.IsDigit(line[k]))
                    {
                        j = k;
                        while (j < n && char.IsDigit(line[j]))
                            j++;
                    }
                }
            }
            while (j < n && "jJlLfFdDmMuU".IndexOf(line[j]) >= 0)
                j++;
            return j;
        }

        // The next non-space character after j, or '\0'.
        private static char NextSignificant(string line, int j)
        {
            while (j < line.Length && line[j] == ' ')
                j++;
            return j < line.Length ? line[j] : '\0';
        }

        // Python: state 0, or the quote of an open triple-quoted string (''' 1, """ 2).
        private static int PythonLineTokens(string line, int state, List<Token> tokens)
        {
            var n = line.Length;
            var i = 0;
            if (state != 0)
            {
                var close = line.IndexOf(state == 1 ? "'''" : "\"\"\"", StringComparison.Ordinal);
                if (close < 0)
                {
                    if (n > 0)
                        tokens.Add(new Token(0, n, Kind.String));
                    return state;
                }
                tokens.Add(new Token(0, close + 3, Kind.String));
                i = close + 3;
                state = 0;
            }
            string previousWord = null;
            var firstSignificant = true;
            while (i < n)
            {
                var c = line[i];
                if (c == ' ')
                {
                    i++;
                    continue;
                }
                if (c == '#')
                {
                    tokens.Add(new Token(i, n - i, Kind.Comment));
                    break;
                }
                if (c == '@' && firstSignificant)
                {
                    var j = i + 1;
                    while (j < n && (IsNamePart(line[j]) || line[j] == '.'))
                        j++;
                    tokens.Add(new Token(i, j - i, Kind.Decorator));
                    i = j;
                    firstSignificant = false;
                    continue;
                }
                firstSignificant = false;
                if (c == '\'' || c == '"')
                {
                    i = PythonString(line, i, i, tokens, ref state);
                    previousWord = null;
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(line[i + 1])))
                {
                    var j = NumberEnd(line, i);
                    tokens.Add(new Token(i, j - i, Kind.Number));
                    i = j;
                    previousWord = null;
                    continue;
                }
                if (IsNameStart(c))
                {
                    var j = i + 1;
                    while (j < n && IsNamePart(line[j]))
                        j++;
                    var word = line.Substring(i, j - i);
                    // A string's prefix: r'..', f"..", rb'..'
                    if (j < n && (line[j] == '\'' || line[j] == '"') && word.Length <= 2 && word.All(p => "rRbBfFuU".IndexOf(p) >= 0))
                    {
                        i = PythonString(line, i, j, tokens, ref state);
                        previousWord = null;
                        continue;
                    }
                    Kind kind;
                    if (previousWord == "def" || previousWord == "class")
                        kind = Kind.Definition;
                    else if (PythonKeywords.Contains(word))
                        kind = Kind.Keyword;
                    else if (NextSignificant(line, j) == '(')
                        kind = Kind.Definition;
                    else if (PythonBuiltins.Contains(word))
                        kind = Kind.Builtin;
                    else
                        kind = Kind.Plain;
                    if (kind == Kind.Definition && PythonBuiltins.Contains(word))
                        kind = Kind.Builtin; // print(...), len(...): built-ins, also called
                    if (kind != Kind.Plain)
                        tokens.Add(new Token(i, j - i, kind));
                    previousWord = word;
                    i = j;
                    continue;
                }
                previousWord = c == '.' ? previousWord : null;
                i++;
            }
            return state;
        }

        // A Python string from start (its prefix) with its quote at quote: to its end on this line,
        // or open (state) for a triple-quoted one going on.
        private static int PythonString(string line, int start, int quote, List<Token> tokens, ref int state)
        {
            var n = line.Length;
            var q = line[quote];
            var triple = quote + 2 < n && line[quote + 1] == q && line[quote + 2] == q;
            if (triple)
            {
                var close = line.IndexOf(new string(q, 3), quote + 3, StringComparison.Ordinal);
                if (close < 0)
                {
                    tokens.Add(new Token(start, n - start, Kind.String));
                    state = q == '\'' ? 1 : 2;
                    return n;
                }
                tokens.Add(new Token(start, close + 3 - start, Kind.String));
                return close + 3;
            }
            var j = quote + 1;
            while (j < n && line[j] != q)
                j += line[j] == '\\' ? 2 : 1;
            j = Math.Min(n, j + 1);
            tokens.Add(new Token(start, j - start, Kind.String));
            return j;
        }

        // C#: state 0, 1 in a block comment, 2 in a verbatim string (@"...").
        private static int CSharpLineTokens(string line, int state, List<Token> tokens)
        {
            var n = line.Length;
            var i = 0;
            if (state == 1)
            {
                var close = line.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0)
                {
                    if (n > 0)
                        tokens.Add(new Token(0, n, Kind.Comment));
                    return 1;
                }
                tokens.Add(new Token(0, close + 2, Kind.Comment));
                i = close + 2;
                state = 0;
            }
            else if (state == 2)
            {
                var end = VerbatimEnd(line, 0);
                tokens.Add(new Token(0, end < 0 ? n : end, Kind.String));
                if (end < 0)
                    return 2;
                i = end;
                state = 0;
            }
            if (i == 0 && line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                var at = line.IndexOf('#');
                tokens.Add(new Token(at, n - at, Kind.Decorator)); // #region, #if, #r
                return state;
            }
            string previousWord = null;
            while (i < n)
            {
                var c = line[i];
                if (c == ' ')
                {
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && line[i + 1] == '/')
                {
                    tokens.Add(new Token(i, n - i, Kind.Comment));
                    break;
                }
                if (c == '/' && i + 1 < n && line[i + 1] == '*')
                {
                    var close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        tokens.Add(new Token(i, n - i, Kind.Comment));
                        return 1;
                    }
                    tokens.Add(new Token(i, close + 2 - i, Kind.Comment));
                    i = close + 2;
                    continue;
                }
                // Strings: @"..", $@"..", @$"..", $"..", ".."; chars '.'
                var verbatim = (c == '@' && i + 1 < n && line[i + 1] == '"') ||
                    ((c == '@' || c == '$') && i + 2 < n && (line[i + 1] == '@' || line[i + 1] == '$') && line[i + 2] == '"');
                if (verbatim)
                {
                    var open = line.IndexOf('"', i);
                    var end = VerbatimEnd(line, open + 1);
                    tokens.Add(new Token(i, (end < 0 ? n : end) - i, Kind.String));
                    if (end < 0)
                        return 2;
                    i = end;
                    previousWord = null;
                    continue;
                }
                if (c == '"' || (c == '$' && i + 1 < n && line[i + 1] == '"') || c == '\'')
                {
                    var q = c == '$' ? '"' : c;
                    var j = (c == '$' ? i + 1 : i) + 1;
                    while (j < n && line[j] != q)
                        j += line[j] == '\\' ? 2 : 1;
                    j = Math.Min(n, j + 1);
                    tokens.Add(new Token(i, j - i, Kind.String));
                    i = j;
                    previousWord = null;
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(line[i + 1])))
                {
                    var j = NumberEnd(line, i);
                    tokens.Add(new Token(i, j - i, Kind.Number));
                    i = j;
                    previousWord = null;
                    continue;
                }
                if (IsNameStart(c) || (c == '@' && i + 1 < n && IsNameStart(line[i + 1])))
                {
                    var j = i + 1;
                    while (j < n && IsNamePart(line[j]))
                        j++;
                    var word = line.Substring(i, j - i);
                    Kind kind;
                    if (previousWord != null && CSharpDefiners.Contains(previousWord))
                        kind = Kind.Definition;
                    else if (CSharpKeywords.Contains(word))
                        kind = Kind.Keyword;
                    else if (NextSignificant(line, j) == '(')
                        kind = Kind.Definition;
                    else if (CSharpBuiltins.Contains(word))
                        kind = Kind.Builtin;
                    else
                        kind = Kind.Plain;
                    if (kind == Kind.Definition && CSharpBuiltins.Contains(word) && (previousWord == null || !CSharpDefiners.Contains(previousWord)))
                        kind = Kind.Builtin; // Print(...): a built-in, also called
                    if (kind != Kind.Plain)
                        tokens.Add(new Token(i, j - i, kind));
                    previousWord = word;
                    i = j;
                    continue;
                }
                previousWord = c == '.' ? previousWord : null;
                i++;
            }
            return state;
        }

        private static readonly Regex MarkdownHeading = new Regex(@"^\s{0,3}#{1,6}(\s|$)", RegexOptions.CultureInvariant);
        private static readonly Regex MarkdownRule = new Regex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex MarkdownListMarker = new Regex(@"^\s*([-*+]|\d+[.)])\s", RegexOptions.CultureInvariant);

        // Markdown source: state 0, or 1 inside a fenced code block (``` or ~~~). Headings and
        // rules: Keyword; quotes: Comment; code (fenced, `inline`): String; list markers and
        // fences: Decorator; **bold**: Definition; *italic*: Builtin; [links](..): Number.
        private static int MarkdownLineTokens(string line, int state, List<Token> tokens)
        {
            var n = line.Length;
            var trimmed = line.TrimStart();
            var fence = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
            if (state == 1 || fence)
            {
                if (n > 0)
                    tokens.Add(new Token(0, n, fence ? Kind.Decorator : Kind.String));
                return fence ? 1 - state : state;
            }
            if (MarkdownHeading.IsMatch(line) || MarkdownRule.IsMatch(line))
            {
                if (n > 0)
                    tokens.Add(new Token(0, n, Kind.Keyword));
                return 0;
            }
            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                tokens.Add(new Token(0, n, Kind.Comment));
                return 0;
            }
            var i = 0;
            var marker = MarkdownListMarker.Match(line);
            if (marker.Success)
            {
                tokens.Add(new Token(marker.Groups[1].Index, marker.Groups[1].Length, Kind.Decorator));
                i = marker.Length;
            }
            while (i < n)
            {
                var c = line[i];
                if (c == '\\' && i + 1 < n)
                {
                    i += 2;
                    continue;
                }
                if (c == '`')
                {
                    var close = line.IndexOf('`', i + 1);
                    var end = close < 0 ? n : close + 1;
                    tokens.Add(new Token(i, end - i, Kind.String));
                    i = end;
                    continue;
                }
                if ((c == '*' || c == '_') && i + 1 < n && line[i + 1] == c)
                {
                    var close = line.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                    if (close > i + 2)
                    {
                        tokens.Add(new Token(i, close + 2 - i, Kind.Definition));
                        i = close + 2;
                        continue;
                    }
                }
                if ((c == '*' || c == '_') && i + 1 < n && line[i + 1] != ' ' && (c == '*' || i == 0 || !char.IsLetterOrDigit(line[i - 1])))
                {
                    var close = line.IndexOf(c, i + 1);
                    if (close > i + 1)
                    {
                        tokens.Add(new Token(i, close + 1 - i, Kind.Builtin));
                        i = close + 1;
                        continue;
                    }
                }
                if (c == '[' || (c == '!' && i + 1 < n && line[i + 1] == '['))
                {
                    var open = c == '!' ? i + 1 : i;
                    var middle = line.IndexOf("](", open + 1, StringComparison.Ordinal);
                    var close = middle < 0 ? -1 : line.IndexOf(')', middle + 2);
                    if (close > 0)
                    {
                        tokens.Add(new Token(i, close + 1 - i, Kind.Number));
                        i = close + 1;
                        continue;
                    }
                }
                i++;
            }
            return 0;
        }

        // The end (after the closing quote) of a verbatim string's text from j -- "" is a quote
        // in it -- or -1 when it goes on to the next line.
        private static int VerbatimEnd(string line, int j)
        {
            while (j < line.Length)
            {
                if (line[j] == '"')
                {
                    if (j + 1 < line.Length && line[j + 1] == '"')
                    {
                        j += 2;
                        continue;
                    }
                    return j + 1;
                }
                j++;
            }
            return -1;
        }
    }
}
