using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MiniVivisection
{
    /// <summary>
    /// What values were at the previous pause, for "was" in the tooltips and the colored
    /// inline values (option "What changed since the last pause"). Per debugger:
    /// - the paused frame's variables by name, as text (InlineValues reads them at every
    ///   pause); compared only within the same frame -- stepping into another function
    ///   starts afresh;
    /// - the tooltips shown during a pause, by expression and line, for expressions that
    ///   aren't plain names (<c>crv.PointAtStart</c>).
    /// Values are compared as the text the tooltip shows (HoverText), so "changed" means
    /// the shown text changed. Any thread.
    /// </summary>
    internal static class ValueHistory
    {
        private sealed class State
        {
            public string FrameKey;
            public int FirstLine;
            public Dictionary<string, string> Previous = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> Current = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> PreviousTips = new Dictionary<string, string>(StringComparer.Ordinal);
            public Dictionary<string, string> CurrentTips = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static readonly ConditionalWeakTable<object, State> s_states = new ConditionalWeakTable<object, State>();
        private static readonly Regex PlainName = new Regex(@"^[A-Za-z_]\w*$");

        /// <summary>A new pause of <paramref name="debugger"/>, in the frame
        /// <paramref name="frameKey"/> (starting at <paramref name="firstLine"/>), with its
        /// variables' texts.</summary>
        public static void NewPause(object debugger, string frameKey, int firstLine, Dictionary<string, string> variables)
        {
            var state = s_states.GetValue(debugger, _ => new State());
            lock (state)
            {
                var sameFrame = state.FrameKey == frameKey;
                state.Previous = sameFrame ? state.Current : new Dictionary<string, string>(StringComparer.Ordinal);
                state.PreviousTips = sameFrame ? state.CurrentTips : new Dictionary<string, string>(StringComparer.Ordinal);
                state.Current = new Dictionary<string, string>(variables, StringComparer.Ordinal);
                state.CurrentTips = new Dictionary<string, string>(StringComparer.Ordinal);
                state.FrameKey = frameKey;
                state.FirstLine = firstLine;
            }
        }

        /// <summary>The run ended: nothing to compare with any more.</summary>
        public static void Clear(object debugger) => s_states.Remove(debugger);

        /// <summary>Whether the variable <paramref name="name"/> shows another text than at
        /// the previous pause (false when it didn't exist then).</summary>
        public static bool Changed(object debugger, string name, string text)
        {
            if (!s_states.TryGetValue(debugger, out var state))
                return false;
            lock (state)
                return state.Previous.TryGetValue(name, out var before) && before != text;
        }

        /// <summary>What the tooltip of <paramref name="expression"/> at
        /// <paramref name="line"/> showed at the previous pause, when that was something
        /// else than <paramref name="text"/>; null otherwise. Remembers
        /// <paramref name="text"/> for the next pause.</summary>
        public static string WasFor(object debugger, string expression, int line, string text)
        {
            if (!s_states.TryGetValue(debugger, out var state))
                return null;
            lock (state)
            {
                var key = line + ":" + expression;
                state.CurrentTips[key] = text;
                string before = null;
                if (state.PreviousTips.TryGetValue(key, out var tip))
                    before = tip;
                else if (PlainName.IsMatch(expression) && line >= state.FirstLine && state.Previous.TryGetValue(expression, out var variable))
                    before = variable; // a variable of the paused frame, written in its code
                return before != null && before != text ? before : null;
            }
        }
    }

    /// <summary>The text of the inline values (InlineValues): which of the paused frame's
    /// variables each line shows.</summary>
    internal static class InlineText
    {
        private const int MaxLines = 300;
        private const int MaxPerLine = 4;
        private const int MaxValue = 40;

        private static readonly Regex Name = new Regex(@"(?<![\w.])[A-Za-z_]\w*");

        /// <summary>The values for the lines <paramref name="first"/> to
        /// <paramref name="current"/> of <paramref name="code"/>: the frame's variables
        /// written on each (not after a dot, not in a comment), up to four a line.</summary>
        internal static List<(int Line, string Text, bool Changed)> Lines(string code, int first, int current, IReadOnlyDictionary<string, string> texts, ICollection<string> changed)
        {
            var result = new List<(int, string, bool)>();
            var lines = (code ?? "").Replace("\r\n", "\n").Split('\n');
            for (var line = Math.Max(first, current - MaxLines); line <= current && line <= lines.Length; line++)
            {
                var source = WithoutCommentsAndStrings(lines[line - 1]);
                var names = new List<string>();
                foreach (Match match in Name.Matches(source))
                {
                    if (texts.ContainsKey(match.Value) && !HoverText.Keywords.Contains(match.Value) && !names.Contains(match.Value))
                        names.Add(match.Value);
                    if (names.Count == MaxPerLine)
                        break;
                }
                if (names.Count == 0)
                    continue;
                var text = string.Join(", ", names.Select(n => n + " = " + Cut(HoverText.Compact(texts[n]))));
                result.Add((line, text, names.Any(changed.Contains)));
            }
            return result;
        }

        /// <summary>A line with its comment cut off and its string literals blanked: their
        /// words aren't variables.</summary>
        private static string WithoutCommentsAndStrings(string line)
        {
            var text = new StringBuilder(line.Length);
            var quote = '\0';
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quote != '\0')
                {
                    if (c == '\\')
                        i++;
                    else if (c == quote)
                        quote = '\0';
                    text.Append(' ');
                }
                else if (c == '#')
                    break;
                else if (c == '\'' || c == '"')
                {
                    quote = c;
                    text.Append(' ');
                }
                else
                    text.Append(c);
            }
            return text.ToString();
        }

        private static string Cut(string text) => text.Length <= MaxValue ? text : text.Substring(0, MaxValue - 1) + "…";
    }
}
