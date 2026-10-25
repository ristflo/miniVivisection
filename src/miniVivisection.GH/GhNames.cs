using System;
using System.Text.RegularExpressions;

namespace MiniVivisection.GH
{
    /// <summary>
    /// V Edit's name with the mark the plug-in's setting "Mark in the V names" chooses (° by
    /// default): read from miniVivisection's saved settings, as the .gha doesn't reference the
    /// .rhp. The marks are the plug-in's (VNames); taken when a component is made.
    /// </summary>
    internal static class GhNames
    {
        private static readonly Guid PlugInId = new Guid("c198ec17-3078-46a0-8f9b-94a40e9ba298"); // miniVivisection.rhp
        private static readonly string Degree = ((char)0x00B0).ToString();
        private static readonly string OnTheLine = ((char)0x2223).ToString();
        private static readonly string Thin = ((char)0x2009).ToString();

        public static string Mark
        {
            get
            {
                string choice = null;
                try
                {
                    choice = Rhino.PlugIns.PlugIn.GetPluginSettings(PlugInId, false)?.GetString("NameMark", Degree);
                }
                catch (Exception)
                {
                    // not there: the default
                }
                if (choice == "|")
                    return "|";
                if (choice == OnTheLine + " (on the line)")
                    return OnTheLine;
                if (choice == "| with thin spaces")
                    return Thin + "|" + Thin;
                if (choice == OnTheLine + " with thin spaces")
                    return Thin + OnTheLine + Thin;
                return Degree;
            }
        }

        public static string Of(string name) => "V" + Mark + name;

        /// <summary>The components' tab (their category).</summary>
        public const string Category = "miniVivisection";

        /// <summary>The tab's short name, which Grasshopper shows when its tabs don't fit: "V°" (the
        /// chosen mark, without thin spaces).</summary>
        public static string TabShort => "V" + Mark.Trim();

        /// <summary>A Grasshopper menu item of the plug-in: "V° " + <paramref name="item"/> (the chosen mark).</summary>
        public static string Menu(string item) => "V" + Mark + " " + item;

        private static readonly Regex s_names = new Regex(@"\bV (Edit|Help|Text|Settings|Watch)\b", RegexOptions.CultureInvariant);

        /// <summary><paramref name="text"/> (written "V Edit", "V Text", "V Settings") with the mark.</summary>
        public static string Text(string text) => string.IsNullOrEmpty(text) ? text : s_names.Replace(text, m => "V" + Mark + m.Groups[1].Value);
    }
}
