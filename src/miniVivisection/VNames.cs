using System.Linq;
using System.Text.RegularExpressions;

namespace MiniVivisection
{
    /// <summary>
    /// miniVivisection's names -- V°Watch, V°Python, V°Edit... -- with a degree sign between the V
    /// and the name. The code writes them "V Watch"; <see cref="Text"/> puts the ° in wherever
    /// text reaches the user (tabs, pages, V Log, the settings, tooltips, overlays).
    /// </summary>
    internal static class VNames
    {
        /// <summary>The mark between the V and the name: °.</summary>
        public static readonly string Mark = ((char)0x00B0).ToString();

        // The marks earlier versions could choose (a setting, now gone): text written with them
        // -- a V Edit's nickname in an older definition -- still shows with the °
        private static readonly string OnTheLine = ((char)0x2223).ToString(); // U+2223: a bar without a tail below the line
        private static readonly string Thin = ((char)0x2009).ToString();      // a thin space
        private static readonly string[] s_earlierMarks = { "|", OnTheLine, Thin + "|" + Thin, Thin + OnTheLine + Thin };

        /// <summary>"V" + the mark + <paramref name="name"/>: V°Watch.</summary>
        public static string Of(string name) => "V" + Mark + name;

        /// <summary>A Grasshopper menu item of the plug-in: "V° " + <paramref name="item"/>.</summary>
        public static string Menu(string item) => "V" + Mark + " " + item;

        // A name written with a space (the code), the ° or an earlier mark; the longest marks first.
        private static readonly Regex s_names = new Regex(
            @"\bV(?: |" + string.Join("|", s_earlierMarks.Concat(new[] { Mark }).OrderByDescending(m => m.Length).Select(Regex.Escape)) + ")" +
            @"(Watch|Python|PowerShell|Claude|Threads|Log|Settings|Help|Edit|Text|Debug|AI)\b",
            RegexOptions.CultureInvariant);

        /// <summary><paramref name="text"/> with every V name written with the °.</summary>
        public static string Text(string text) =>
            string.IsNullOrEmpty(text) ? text : s_names.Replace(text, m => "V" + Mark + m.Groups[1].Value);
    }
}
