using System;
using System.Text.RegularExpressions;

namespace MiniVivisection.GH
{
    /// <summary>
    /// The plug-in's Grasshopper names -- V°Edit, V°Text, its menu items -- with the degree sign
    /// between the V and the name, as the .rhp's VNames writes them.
    /// </summary>
    internal static class GhNames
    {
        /// <summary>The mark between the V and the name: °.</summary>
        public static readonly string Mark = ((char)0x00B0).ToString();

        public static string Of(string name) => "V" + Mark + name;

        /// <summary>The components' tab (their category).</summary>
        public const string Category = "miniVivisection";

        /// <summary>The tab's short name, which Grasshopper shows when its tabs don't fit: "V°".</summary>
        public static string TabShort => "V" + Mark;

        /// <summary>A Grasshopper menu item of the plug-in: "V° " + <paramref name="item"/>.</summary>
        public static string Menu(string item) => "V" + Mark + " " + item;

        private static readonly Regex s_names = new Regex(@"\bV (Edit|Help|Text|Settings|Watch)\b", RegexOptions.CultureInvariant);

        /// <summary><paramref name="text"/> (written "V Edit", "V Text", "V Settings") with the °.</summary>
        public static string Text(string text) => string.IsNullOrEmpty(text) ? text : s_names.Replace(text, m => "V" + Mark + m.Groups[1].Value);
    }
}
