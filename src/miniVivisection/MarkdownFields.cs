using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MiniVivisection
{
    /// <summary>
    /// Fields in Markdown: <c>{{protected}}</c> -- "protected" or "not protected" -- and
    /// <c>{{modified}}</c> -- "modified", "not modified" or "not tracked" --, the state of the file's
    /// protection (Rhino: the ProtectModel command, RhinoProtect; Grasshopper: the definition's
    /// V°Protect). Put in when the Markdown is laid out: a field's words have no place in the source
    /// (a click or an edit there goes to the field's place), and the layout is made again when the
    /// state changes. A link <c>protect:revert</c> puts the protected original back (MarkdownLinks).
    /// Shared: the .rhp, the .gha, the checks.
    /// </summary>
    internal static class MarkdownFields
    {
        public const string Protected = "protected";
        public const string Modified = "modified";

        private static readonly Regex Field = new Regex(@"\{\{\s*(protected|modified)\s*\}\}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>A file's protection, as the fields show it.</summary>
        public sealed class State
        {
            public bool IsProtected;
            public string Disk;        // "original", "modified" -- else not tracked (no archive, not saved, ...)

            public string ProtectedText => IsProtected ? "protected" : "not protected";

            public string ModifiedText => !IsProtected ? "not tracked" : Disk == "modified" ? "modified" : Disk == "original" ? "not modified" : "not tracked";

            /// <summary>The fields' words, in a word: changed -- laid out again.</summary>
            public string Signature => ProtectedText + "|" + ModifiedText;

            public static readonly State None = new State();
        }

        /// <summary>Whether <paramref name="markdown"/> has a field.</summary>
        public static bool Has(string markdown) => markdown != null && markdown.IndexOf("{{", StringComparison.Ordinal) >= 0 && Field.IsMatch(markdown);

        /// <summary>A field's words in <paramref name="state"/>.</summary>
        public static string Value(string name, State state) =>
            string.Equals(name, Protected, StringComparison.OrdinalIgnoreCase) ? (state ?? State.None).ProtectedText : (state ?? State.None).ModifiedText;

        /// <summary>The blocks with their fields' words put in (<paramref name="state"/>): a run with a
        /// field is split, the field's words a run of their own without a place in the source.</summary>
        public static List<MarkdownBlocks.Block> Expand(List<MarkdownBlocks.Block> blocks, State state)
        {
            foreach (var block in blocks)
            {
                if (block.Runs.Count == 0 || !block.Runs.Any(IsField))
                    continue;
                var runs = new List<MarkdownBlocks.Run>();
                foreach (var run in block.Runs)
                {
                    if (!IsField(run))
                    {
                        runs.Add(run);
                        continue;
                    }
                    var at = 0;
                    foreach (Match match in Field.Matches(run.Text))
                    {
                        if (match.Index > at)
                            runs.Add(Part(run, at, run.Text.Substring(at, match.Index - at)));
                        // The field's words: no place in the source, the run's style (bold, a link's) kept
                        runs.Add(new MarkdownBlocks.Run { Text = Value(match.Groups[1].Value, state), Style = run.Style, Line = -1, Column = 0, Url = run.Url });
                        at = match.Index + match.Length;
                    }
                    if (at < run.Text.Length)
                        runs.Add(Part(run, at, run.Text.Substring(at)));
                }
                block.Runs.Clear();
                block.Runs.AddRange(runs);
            }
            return blocks;
        }

        // A run with a field in it -- not `code`: there it is written as it is
        private static bool IsField(MarkdownBlocks.Run run) => run.Text != null && !run.Style.HasFlag(MarkdownBlocks.Style.Code) && Field.IsMatch(run.Text);

        // A piece of a run from its offset at: its characters' place in the source kept
        private static MarkdownBlocks.Run Part(MarkdownBlocks.Run run, int at, string text) =>
            new MarkdownBlocks.Run { Text = text, Style = run.Style, Line = run.Line, Column = run.Line >= 0 ? run.Column + at : run.Column, Url = run.Url };
    }
}
