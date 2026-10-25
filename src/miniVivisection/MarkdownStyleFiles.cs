using System;
using System.Collections.Generic;
using System.IO;
using Rhino;

namespace MiniVivisection
{
    /// <summary>
    /// Where the Markdown styles (MarkdownStyle) come from. The defaults: styles/ next to the
    /// plug-in (read-only here: never saved over -- Save as makes one's own), and inside the
    /// assemblies. The settings "Markdown style for Rhino" and "... for Grasshopper": a file of
    /// one's own (empty: the default). A Rhino file keeps one style -- a copy of the CSS, in its
    /// document strings -- for all its Markdown texts: a file without one uses the Rhino setting's
    /// style and keeps it with its first Markdown text. A file's copy of a default (unchanged, any
    /// version: MarkdownStyle.IsPristineDefault) is replaced by the plug-in's default when the
    /// file is opened, its texts drawn again (MarkdownRhinoText.InstallStyleDefault).
    /// </summary>
    internal static class MarkdownStyleFiles
    {
        /// <summary>The document's style (its CSS) and where it came from (a file's name, or "default").</summary>
        public const string DocKey = "minivivisection.markdown.css";
        public const string DocSourceKey = "minivivisection.markdown.css.source";

        /// <summary>The default styles' folder: styles/ next to the plug-in.</summary>
        public static string Folder => Path.Combine(Path.GetDirectoryName(typeof(MarkdownStyleFiles).Assembly.Location) ?? "", "styles");

        public static string DefaultPath(MarkdownStyle.Target target) => Path.Combine(Folder, MarkdownStyle.DefaultFileName(target));

        /// <summary>Whether <paramref name="path"/> is a default's file (or anything in the defaults'
        /// folder): never written.</summary>
        public static bool IsDefaultFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
                var folder = Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The setting's file for <paramref name="target"/> (null: the default).</summary>
        public static string SettingPath(MarkdownStyle.Target target)
        {
            var path = (target == MarkdownStyle.Target.Rhino ? MiniVivisectionSettings.MarkdownStyleRhino : MiniVivisectionSettings.MarkdownStyleGrasshopper).Trim().Trim('"');
            return path.Length == 0 ? null : Environment.ExpandEnvironmentVariables(path);
        }

        private static readonly Dictionary<string, (DateTime Written, long Checked, string Css)> s_files = new Dictionary<string, (DateTime, long, string)>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> s_reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The setting's style for <paramref name="target"/>: its file's CSS (read again
        /// when it changed, looked at once a second at most), the default when none is set or it
        /// can't be read (said once).</summary>
        public static string SettingCss(MarkdownStyle.Target target)
        {
            var path = SettingPath(target);
            return path == null ? MarkdownStyle.DefaultCss(target) : FileCss(path) ?? MarkdownStyle.DefaultCss(target);
        }

        /// <summary>A style file's CSS (cached; null: it can't be read -- said once).</summary>
        public static string FileCss(string path)
        {
            var now = Environment.TickCount64;
            lock (s_files)
            {
                if (s_files.TryGetValue(path, out var known) && now - known.Checked < 1000)
                    return known.Css;
                try
                {
                    var written = File.GetLastWriteTimeUtc(path);
                    if (!File.Exists(path))
                        throw new FileNotFoundException("no such file");
                    if (known.Css != null && known.Written == written)
                    {
                        s_files[path] = (written, now, known.Css);
                        return known.Css;
                    }
                    var css = File.ReadAllText(path).Replace("\r\n", "\n");
                    s_files[path] = (written, now, css);
                    s_reported.Remove(path);
                    Report(css, Path.GetFileName(path));
                    return css;
                }
                catch (Exception ex)
                {
                    s_files[path] = (DateTime.MinValue, now, null);
                    if (s_reported.Add(path))
                        UserLog.Warning("Markdown style {0}: can't be read ({1}) -- the default instead.", path, ex.Message);
                    return null;
                }
            }
        }

        /// <summary>What was left out of a style, in V Log (once per text).</summary>
        public static void Report(string css, string name)
        {
            var style = MarkdownStyle.Parse(css);
            lock (s_reportedStyles)
            {
                if (style.Problems.Count == 0 || !s_reportedStyles.Add(css))
                    return;
            }
            UserLog.Warning("Markdown style {0}: left out -- {1}", name, string.Join("; ", style.Problems));
        }

        private static readonly HashSet<string> s_reportedStyles = new HashSet<string>();

        // --- a document's style ---------------------------------------------------------------------

        /// <summary>The style <paramref name="doc"/> keeps; null: none yet.</summary>
        public static string Kept(RhinoDoc doc) => doc?.Strings.GetValue(DocKey) is string css && css.Length > 0 ? css : null;

        /// <summary>The CSS <paramref name="doc"/>'s Markdown texts are drawn with: its own, else
        /// the Rhino setting's.</summary>
        public static string DocumentCss(RhinoDoc doc) => Kept(doc) ?? SettingCss(MarkdownStyle.Target.Rhino);

        public static MarkdownStyle ForDocument(RhinoDoc doc) => MarkdownStyle.Parse(DocumentCss(doc));

        /// <summary>Where the document's style came from, for the dialog: "the default", a file's name.</summary>
        public static string NameOf(RhinoDoc doc, string css)
        {
            if (MarkdownStyle.IsPristineDefault(css))
                return "miniVivisection's default";
            var kept = Kept(doc);
            var source = doc?.Strings.GetValue(DocSourceKey);
            if (kept != null && css == kept)
                return string.IsNullOrEmpty(source) || source == "default" ? "this file's own" : "this file's own (from " + source + ")";
            var setting = SettingPath(MarkdownStyle.Target.Rhino);
            return setting != null && css == FileCss(setting) ? Path.GetFileName(setting) + " (V Settings)" : "changed (not saved yet)";
        }

        /// <summary><paramref name="css"/> kept as <paramref name="doc"/>'s style (<paramref name="source"/>:
        /// a file's name, or "default"). False: it had it already.</summary>
        public static bool Keep(RhinoDoc doc, string css, string source)
        {
            if (doc == null || css == null || Kept(doc) == css)
                return false;
            doc.Strings.SetString(DocKey, css);
            doc.Strings.SetString(DocSourceKey, MarkdownStyle.IsPristineDefault(css) ? "default" : source ?? "");
            return true;
        }

        /// <summary>The document keeps the style its texts are drawn with (when it has none yet):
        /// before a Markdown text is made or edited.</summary>
        public static void KeepCurrent(RhinoDoc doc)
        {
            if (doc == null || Kept(doc) != null)
                return;
            var setting = SettingPath(MarkdownStyle.Target.Rhino);
            Keep(doc, DocumentCss(doc), setting == null ? "default" : Path.GetFileName(setting));
        }
    }
}
