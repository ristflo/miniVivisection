using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MiniVivisection
{
    /// <summary>Which of Claude's expressions only read (evaluated without asking) and which
    /// are code to approve ("Claude may run Python": Ask) -- apart from the tools, for the checks.</summary>
    internal static class CodeApproval
    {
        /// <summary>An expression that only reads: names, attributes, indexes, literals and
        /// operators -- no call, no dunder, no lambda or assignment. Claude evaluates such ones
        /// without asking (a property getter still runs, as the editor's own Variables do).</summary>
        public static bool IsPlain(string expression)
        {
            var text = expression ?? "";
            return text.IndexOf('(') < 0 && !text.Contains("__") && !text.Contains(":=") && text.IndexOf(';') < 0 &&
                   text.IndexOf('\n') < 0 && !Regex.IsMatch(text, @"\blambda\b|\bimport\b|(?<![=!<>])=(?!=)");
        }

        /// <summary>The {expressions} of a log message.</summary>
        public static IEnumerable<string> LogExpressions(string message) =>
            Regex.Matches(message ?? "", @"\{([^{}]*)\}").Cast<Match>().Select(m => m.Groups[1].Value);
    }
}
