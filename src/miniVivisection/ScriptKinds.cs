using System;
using System.Collections.Concurrent;
using System.IO;

namespace MiniVivisection
{
    /// <summary>What a script runs in.</summary>
    internal enum ScriptKind
    {
        /// <summary>Python 3, Rhino's CPython (pythonnet): everything of the plug-in works.</summary>
        Python3,

        /// <summary>IronPython 2.7 (<c>#! python2</c>): the debugger, drawing of plain names
        /// and breakpoint hit counts work; whatever needs the paused Python 3 frame
        /// doesn't, and is left out quietly.</summary>
        IronPython,

        CSharp,
        Other,
    }

    /// <summary>
    /// Which scripts the plug-in's Python 3 features apply to. PythonBridge reaches Rhino's
    /// CPython only; an IronPython 2 script (<c>#! python2</c>) is a <c>.py</c> file too, but
    /// its frames aren't CPython's. The kind comes from the editor's language for the
    /// script ("IronPython 2.7.12 (mcneel.ironpython.python)", "Python 3.9.10
    /// (mcneel.pythonnet.python)"), learned whenever the plug-in sees the script open
    /// (Learn), else from the file: <c>.cs</c>, or a <c>.py</c> whose first line asks for
    /// <c>python2</c>. Any thread.
    /// </summary>
    internal static class ScriptKinds
    {
        private static readonly ConcurrentDictionary<string, ScriptKind> s_byFile = new ConcurrentDictionary<string, ScriptKind>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The kind for an editor language text (ScriptEditorAccess.Language).</summary>
        public static ScriptKind FromLanguage(string language)
        {
            var text = language ?? "";
            if (text.IndexOf("ironpython", StringComparison.OrdinalIgnoreCase) >= 0)
                return ScriptKind.IronPython;
            if (text.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0)
                return text.StartsWith("Python 2", StringComparison.OrdinalIgnoreCase) ? ScriptKind.IronPython : ScriptKind.Python3;
            if (text.IndexOf("csharp", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("C#", StringComparison.Ordinal) >= 0)
                return ScriptKind.CSharp;
            return ScriptKind.Other;
        }

        /// <summary>Remembers what the editor says the script at <paramref name="uri"/> is.</summary>
        public static void Learn(Uri uri, string language)
        {
            if (uri == null || string.IsNullOrEmpty(language))
                return;
            var kind = FromLanguage(language);
            if (kind != ScriptKind.Other)
                s_byFile[BreakpointSpecs.Key(uri)] = kind;
        }

        /// <summary>The kind of the script at <paramref name="uri"/>: the editor's language
        /// when given, else what was learned, else from the file.</summary>
        public static ScriptKind Of(Uri uri, string language = null)
        {
            if (!string.IsNullOrEmpty(language))
            {
                var kind = FromLanguage(language);
                if (kind != ScriptKind.Other)
                    return kind;
            }
            if (uri == null)
                return ScriptKind.Other;
            return s_byFile.GetOrAdd(BreakpointSpecs.Key(uri), _ => FromFile(uri));
        }

        public static bool IsPython3(Uri uri, string language = null) => Of(uri, language) == ScriptKind.Python3;

        private static ScriptKind FromFile(Uri uri)
        {
            var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return ScriptKind.CSharp;
            if (!path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                return ScriptKind.Other;
            return AsksForPython2(FirstLine(uri)) ? ScriptKind.IronPython : ScriptKind.Python3;
        }

        // RhinoCode's own rule (ExecSpecifierParser): within the first 32 lines, a line
        // "# async: true" or "// async: true" -- the script runs on a thread of its own.
        private static readonly System.Text.RegularExpressions.Regex s_async =
            new System.Text.RegularExpressions.Regex(@"^(//|#)\s+async:\s*(?<result>(true|false))$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Whether the script asks to run off Rhino's UI thread (<c># async:true</c>);
        /// null: it doesn't say (it runs on the UI thread then).</summary>
        internal static bool? AsksForAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length && i < 32; i++)
            {
                var match = s_async.Match(lines[i].TrimEnd());
                if (match.Success)
                    return match.Groups["result"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            return null;
        }

        /// <summary><see cref="AsksForAsync"/> for a script file; null when it can't be read.</summary>
        internal static bool? AsksForAsync(Uri uri)
        {
            try
            {
                return uri != null && uri.IsFile && File.Exists(uri.LocalPath) ? AsksForAsync(File.ReadAllText(uri.LocalPath)) ?? false : (bool?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>"#! python2", "#!python 2", "#! python2.7" (the editor's own marker).</summary>
        internal static bool AsksForPython2(string firstLine)
        {
            var line = (firstLine ?? "").Trim();
            if (!line.StartsWith("#!", StringComparison.Ordinal))
                return false;
            var rest = line.Substring(2).Replace(" ", "").ToLowerInvariant();
            return rest.StartsWith("python2") || rest.StartsWith("ironpython");
        }

        private static string FirstLine(Uri uri)
        {
            try
            {
                if (!uri.IsFile || !File.Exists(uri.LocalPath))
                    return null;
                using (var reader = new StreamReader(uri.LocalPath))
                    return reader.ReadLine();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
