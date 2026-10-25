using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Eto.Drawing;

namespace MiniVivisection
{
    internal enum TokenKind
    {
        Keyword,
        String,
        Comment,
        Number,
        Variable,
    }

    /// <summary>
    /// Syntax colors for the plug-in's terminals (TerminalView: V Python, V PowerShell,
    /// V Claude's code blocks), in the Script Editor's colors: the categories and colors of
    /// Monaco's own Python and PowerShell rules and its "vs" / "vs-dark" themes -- keywords,
    /// strings, comments, numbers, and PowerShell's $variables. A small tokenizer, not
    /// Monaco's: good enough for lines typed at a prompt and short code blocks.
    /// </summary>
    internal static class SyntaxColors
    {
        private static readonly HashSet<string> PythonKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else",
            "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
            "return", "try", "while", "with", "yield", "match", "case", "print", "self",
        };

        private static readonly HashSet<string> PowerShellKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "begin", "break", "catch", "class", "continue", "data", "define", "do", "dynamicparam", "else", "elseif", "end", "exit",
            "filter", "finally", "for", "foreach", "from", "function", "if", "in", "param", "process", "return", "switch", "throw",
            "trap", "try", "until", "using", "var", "while", "workflow", "parallel", "sequence", "configuration",
        };

        private static readonly Regex Python = new Regex(
            @"(?<comment>\#[^\n]*)" +
            @"|(?<string>(?i:[rbuf]{0,2})(?:""""""[\s\S]*?(?:""""""|$)|'''[\s\S]*?(?:'''|$)|""(?:\\.|[^""\\\n])*""?|'(?:\\.|[^'\\\n])*'?))" +
            @"|(?<number>\b(?:0[xXoObB][0-9a-fA-F_]+|\d[\d_]*(?:\.\d*)?(?:[eE][+-]?\d+)?[jJ]?)\b|\.\d+(?:[eE][+-]?\d+)?\b)" +
            @"|(?<word>\b[A-Za-z_]\w*\b)", RegexOptions.Compiled);

        private static readonly Regex PowerShell = new Regex(
            @"(?<comment><\#[\s\S]*?(?:\#>|$)|\#[^\n]*)" +
            @"|(?<string>""(?:`.|[^""`])*""?|'(?:''|[^'])*'?)" +
            @"|(?<variable>\$\{[^}]*\}?|\$[\w:?]+)" +
            @"|(?<number>\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?(?:kb|mb|gb|tb)?\b)" +
            @"|(?<word>\b[A-Za-z_][\w-]*\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>"python", "powershell" and their aliases; null for anything else.</summary>
        internal static string Normalize(string language)
        {
            switch ((language ?? "").Trim().ToLowerInvariant())
            {
                case "python":
                case "py":
                case "python3":
                case "ipython":
                    return "python";
                case "powershell":
                case "ps1":
                case "pwsh":
                case "ps":
                    return "powershell";
                default:
                    return null;
            }
        }

        /// <summary>The colored tokens of <paramref name="text"/> in <paramref name="language"/>.</summary>
        internal static IEnumerable<(int Start, int Length, TokenKind Kind)> Tokens(string text, string language)
        {
            language = Normalize(language);
            if (string.IsNullOrEmpty(text) || language == null)
                yield break;
            var python = language == "python";
            foreach (Match match in (python ? Python : PowerShell).Matches(text))
            {
                if (match.Groups["comment"].Success)
                    yield return (match.Index, match.Length, TokenKind.Comment);
                else if (match.Groups["string"].Success)
                    yield return (match.Index, match.Length, TokenKind.String);
                else if (match.Groups["number"].Success)
                    yield return (match.Index, match.Length, TokenKind.Number);
                else if (!python && match.Groups["variable"].Success)
                    yield return (match.Index, match.Length, TokenKind.Variable);
                else if (match.Groups["word"].Success && (python ? PythonKeywords : PowerShellKeywords).Contains(match.Value))
                    yield return (match.Index, match.Length, TokenKind.Keyword);
            }
        }

        /// <summary>Monaco's colors for a token, in its light ("vs") or dark ("vs-dark") theme.</summary>
        internal static Color ColorOf(TokenKind kind, bool dark)
        {
            switch (kind)
            {
                case TokenKind.Keyword:
                    return dark ? Color.FromRgb(0x569CD6) : Color.FromRgb(0x0000FF);
                case TokenKind.String:
                    return dark ? Color.FromRgb(0xCE9178) : Color.FromRgb(0xA31515);
                case TokenKind.Comment:
                    return dark ? Color.FromRgb(0x6A9955) : Color.FromRgb(0x008000);
                case TokenKind.Number:
                    return dark ? Color.FromRgb(0xB5CEA8) : Color.FromRgb(0x098658);
                default:
                    return dark ? Color.FromRgb(0x9CDCFE) : Color.FromRgb(0x001080);
            }
        }

        /// <summary>Fenced code blocks (```python ... ```) complete in <paramref name="text"/>
        /// from <paramref name="from"/> on: each block's code (start, length) and language; and
        /// where scanning can go on next time (after the last complete block).</summary>
        internal static (List<(int Start, int Length, string Language)> Blocks, int Next) Fences(string text, int from)
        {
            var blocks = new List<(int, int, string)>();
            var next = from;
            var opening = new Regex(@"(?m)^[ \t]*```[ \t]*([A-Za-z0-9_+#-]*)[ \t]*\r?$");
            var at = Math.Max(0, Math.Min(from, text.Length));
            while (true)
            {
                var open = opening.Match(text, at);
                if (!open.Success)
                    break;
                var codeStart = open.Index + open.Length + 1; // after the newline
                if (codeStart > text.Length)
                    break;
                var close = new Regex(@"(?m)^[ \t]*```[ \t]*\r?$").Match(text, codeStart);
                if (!close.Success)
                    break; // still streaming: next time
                var language = open.Groups[1].Value;
                blocks.Add((codeStart, Math.Max(0, close.Index - codeStart), language));
                at = close.Index + close.Length;
                next = at;
            }
            return (blocks, next);
        }

        internal static bool IsDark(Color background) => background.A > 0 && background.ToHSL().L < 0.5f;

        internal static IReadOnlyList<string> KnownLanguages { get; } = new[] { "python", "powershell" }.ToList();
    }
}
