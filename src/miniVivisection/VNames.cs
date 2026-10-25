using System.Linq;
using System.Text.RegularExpressions;

namespace MiniVivisection
{
    /// <summary>
    /// miniVivisection's names -- V°Watch, V°Python, V°Edit... -- with the mark between the V and
    /// the name that "Mark in the V names" chooses (° by default; | or ∣, also with thin
    /// spaces). The code writes them "V Watch"; <see cref="Text"/> puts the mark in wherever
    /// text reaches the user (tabs, pages, V Log, the settings, tooltips, overlays).
    /// </summary>
    internal static class VNames
    {
        private static readonly string Degree = ((char)0x00B0).ToString();   // °
        private static readonly string OnTheLine = ((char)0x2223).ToString(); // U+2223: a bar without a tail below the line
        private static readonly string Thin = ((char)0x2009).ToString();      // a thin space

        /// <summary>The setting's choices and the text each puts between the V and the name.</summary>
        public static readonly (string Choice, string Mark)[] Marks =
        {
            (Degree, Degree),                                        // V°Watch (the default)
            ("|", "|"),                                              // V|Watch
            (OnTheLine + " (on the line)", OnTheLine),               // V∣Watch
            ("| with thin spaces", Thin + "|" + Thin),               // V | Watch
            (OnTheLine + " with thin spaces", Thin + OnTheLine + Thin), // V ∣ Watch
        };

        public static string DefaultChoice => Degree;

        /// <summary>The mark now.</summary>
        public static string Mark
        {
            get
            {
                var choice = MiniVivisectionSettings.NameMark;
                var found = Marks.FirstOrDefault(m => m.Choice == choice);
                return found.Mark ?? Degree;
            }
        }

        /// <summary>"V" + the mark + <paramref name="name"/>: V°Watch.</summary>
        public static string Of(string name) => "V" + Mark + name;

        /// <summary>A Grasshopper menu item of the plug-in: "V° " + <paramref name="item"/> (the chosen mark).</summary>
        public static string Menu(string item) => "V" + Mark + " " + item;

        // A name written with a space (the code), or with any of the marks (the documents use °);
        // the longest marks first.
        private static readonly Regex s_names = new Regex(
            @"\bV(?: |" + string.Join("|", Marks.Select(m => m.Mark).OrderByDescending(m => m.Length).Select(Regex.Escape)) + ")" +
            @"(Watch|Python|PowerShell|Claude|Threads|Log|Settings|Help|Edit|Text|Debug|AI)\b",
            RegexOptions.CultureInvariant);

        /// <summary><paramref name="text"/> with every V name in the chosen mark.</summary>
        public static string Text(string text) =>
            string.IsNullOrEmpty(text) ? text : s_names.Replace(text, m => "V" + Mark + m.Groups[1].Value);
    }
}
