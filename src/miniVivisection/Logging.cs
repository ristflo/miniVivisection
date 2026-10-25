using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Rhino;

namespace MiniVivisection
{
    internal enum LogLevel
    {
        Off,
        Error,
        Warning,
        Info,
        Debug,
        Trace,
    }

    /// <summary>The plug-in's parts, each with its own level in the detailed log.</summary>
    internal enum LogArea
    {
        General,
        Startup,
        Scan,
        Runs,
        Pause,
        FreePause,
        Python,
        Watch,
        Draw,
        Edit,
        Selection,
        Tabs,
        Menus,
        Hover,
        Claude,
        Mcp,
        PowerShell,
        Settings,
        Breakpoints,
    }

    /// <summary>
    /// The detailed log, for development and diagnosis: every part of the plug-in writes
    /// what it does, by area and level (Options page: a level, per-area levels, Off).
    /// Written to %TEMP%\rhinovivisection\minivivisection\minivivisection.log -- new for
    /// each Rhino session, the last one kept as minivivisection.previous.log, rolled over at
    /// 10 MB -- by a background thread, so logging never waits for the disk on the UI
    /// or the paused thread; and kept in memory (the last 2000 lines) for Claude's
    /// get_plugin_log. A disabled level costs one comparison: the text is only
    /// formatted when it's written.
    ///
    /// For a crash: <see cref="Now"/> writes its line, and every line before it, before it returns
    /// (V°Claude's steps in Grasshopper), and a session that ended without Rhino's shutdown -- its
    /// last line isn't <see cref="ShutdownLine"/> -- is kept as minivivisection.crashed-(time).log (the
    /// last five), not overwritten by the next sessions.
    /// </summary>
    internal static class Log
    {
        private const int MaxFileBytes = 10 * 1024 * 1024;
        private const int MemoryLines = 2000;

        private static readonly LogLevel[] s_levels = new LogLevel[Enum.GetValues(typeof(LogArea)).Length];
        private static readonly BlockingCollection<string> s_queue = new BlockingCollection<string>();
        private static readonly Queue<string> s_memory = new Queue<string>();
        private static Thread s_writer;
        private static long s_queued, s_written; // lines given to the writer, and written

        /// <summary>The last line of a session that shut down (OnShutdown).</summary>
        public const string ShutdownLine = "-- Rhino shut down --";

        static Log()
        {
            Configure(LogLevel.Info, "");
        }

        /// <summary>%TEMP%\rhinovivisection\minivivisection, or MINIVIVISECTION_LOGDIR (the
        /// checks write their own logs there, not over Rhino's).</summary>
        public static string Folder { get; } = (Environment.GetEnvironmentVariable("MINIVIVISECTION_LOGDIR") ?? Environment.GetEnvironmentVariable("SCRIPTEDITORWATCHVIZ_LOGDIR")) is string folder && folder.Length > 0
            ? folder
            : Path.Combine(Path.GetTempPath(), "rhinovivisection", "minivivisection");

        public static string FilePath { get; } = Path.Combine(Folder, "minivivisection.log");

        /// <summary>The levels: <paramref name="level"/> for every area, then the
        /// exceptions in <paramref name="areaLevels"/> ("Pause=Trace; Claude=Debug").</summary>
        public static void Configure(LogLevel level, string areaLevels)
        {
            for (var i = 0; i < s_levels.Length; i++)
                s_levels[i] = level;
            foreach (var part in (areaLevels ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=');
                if (pair.Length == 2 && Enum.TryParse(pair[0].Trim(), true, out LogArea area) && Enum.TryParse(pair[1].Trim(), true, out LogLevel areaLevel))
                    s_levels[(int)area] = areaLevel;
            }
        }

        public static bool IsOn(LogArea area, LogLevel level) => level != LogLevel.Off && level <= s_levels[(int)area];

        public static void Error(LogArea area, string format, params object[] args) => Write(area, LogLevel.Error, format, args);

        public static void Warning(LogArea area, string format, params object[] args) => Write(area, LogLevel.Warning, format, args);

        public static void Info(LogArea area, string format, params object[] args) => Write(area, LogLevel.Info, format, args);

        public static void Debug(LogArea area, string format, params object[] args) => Write(area, LogLevel.Debug, format, args);

        public static void Trace(LogArea area, string format, params object[] args) => Write(area, LogLevel.Trace, format, args);

        /// <summary>An exception with its type and where it was thrown.</summary>
        public static string Describe(Exception ex)
        {
            while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            return string.Format("{0} ({1} in {2}.{3})", ScriptEditorHook.FirstLine(ex), ex.GetType().Name,
                ex.TargetSite?.DeclaringType?.Name, ex.TargetSite?.Name);
        }

        public static void Write(LogArea area, LogLevel level, string format, params object[] args)
        {
            if (!IsOn(area, level))
                return;
            string message;
            try
            {
                message = args == null || args.Length == 0 ? format : string.Format(format, args);
            }
            catch (FormatException)
            {
                message = format;
            }
            var line = string.Format("{0:HH:mm:ss.fff} [{1}{2}] {3,-7} {4}: {5}", DateTime.Now, Environment.CurrentManagedThreadId,
                OnUiThread() ? " UI" : "", level, area, message);
            lock (s_memory)
            {
                s_memory.Enqueue(line);
                while (s_memory.Count > MemoryLines)
                    s_memory.Dequeue();
            }
            EnsureWriter();
            Interlocked.Increment(ref s_queued);
            s_queue.Add(line);
        }

        /// <summary>A line written to the file before this returns, with every line before it
        /// (at most half a second's wait): what a crash right after must not lose.</summary>
        public static void Now(LogArea area, string format, params object[] args)
        {
            Write(area, LogLevel.Info, format, args);
            Flush(TimeSpan.FromMilliseconds(500));
        }

        /// <summary>Waits until the writer has written every line given to it so far.</summary>
        public static void Flush(TimeSpan most)
        {
            var target = Interlocked.Read(ref s_queued);
            var until = DateTime.UtcNow + most;
            while (Interlocked.Read(ref s_written) < target && DateTime.UtcNow < until)
                Thread.Sleep(2);
        }

        /// <summary>Rhino shuts down: the last line, so the next session knows this one ended well.</summary>
        public static void Shutdown()
        {
            Write(LogArea.General, LogLevel.Info, ShutdownLine);
            Flush(TimeSpan.FromSeconds(1));
        }

        /// <summary>Whether this is Rhino's UI thread; false outside Rhino (the checks).</summary>
        private static bool OnUiThread()
        {
            try
            {
                return !RhinoApp.InvokeRequired;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The last lines in memory, at least <paramref name="level"/>, of
        /// <paramref name="area"/> (null: all), newest last.</summary>
        public static List<string> Recent(int count, LogLevel level = LogLevel.Trace, LogArea? area = null)
        {
            lock (s_memory)
            {
                return s_memory.Where(line => Matches(line, level, area)).Reverse().Take(count).Reverse().ToList();
            }
        }

        private static bool Matches(string line, LogLevel level, LogArea? area)
        {
            var close = line.IndexOf("] ", StringComparison.Ordinal);
            if (close < 0)
                return true;
            var rest = line.Substring(close + 2);
            var parts = rest.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !Enum.TryParse(parts[0], out LogLevel lineLevel) || lineLevel > level)
                return false;
            return area == null || parts[1].StartsWith(area.Value + ":", StringComparison.Ordinal);
        }

        private static void EnsureWriter()
        {
            if (s_writer != null)
                return;
            lock (s_queue)
            {
                if (s_writer != null)
                    return;
                StartFile(FilePath, "detailed log");
                s_writer = new Thread(WriteLoop) { IsBackground = true, Name = "miniVivisection log" };
                s_writer.Start();
            }
        }

        /// <summary>A new file for this session; the last session's becomes *.previous.</summary>
        internal static void StartFile(string path, string what)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path))
                {
                    if (path == FilePath && !EndedWell(path))
                        KeepCrashed(path);
                    File.Copy(path, Path.ChangeExtension(path, ".previous" + Path.GetExtension(path)), true);
                }
                File.WriteAllText(path, string.Format("miniVivisection {0}, {1}, Rhino started {2:yyyy-MM-dd HH:mm:ss}\n", what,
                    MiniVivisectionPlugin.FullVersion, System.Diagnostics.Process.GetCurrentProcess().StartTime));
            }
            catch (Exception)
            {
                // no file: the memory and the user log still work
            }
        }

        /// <summary>The detailed log's last line says Rhino shut down (or it was rolled over: still running).</summary>
        private static bool EndedWell(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path);
                var last = lines.LastOrDefault(l => l.Trim().Length > 0) ?? "";
                return last.EndsWith(ShutdownLine, StringComparison.Ordinal) || lines.Length <= 1;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>A session's log that didn't end with the shutdown line: kept, the last five.</summary>
        private static void KeepCrashed(string path)
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                File.Copy(path, Path.Combine(folder, "minivivisection.crashed-" + File.GetLastWriteTime(path).ToString("yyyy-MM-dd-HHmmss") + ".log"), true);
                foreach (var old in Directory.GetFiles(folder, "minivivisection.crashed-*.log").OrderByDescending(f => f).Skip(5))
                    File.Delete(old);
            }
            catch (Exception)
            {
                // kept or not, the new session's log starts
            }
        }

        private static void WriteLoop()
        {
            foreach (var line in s_queue.GetConsumingEnumerable())
            {
                try
                {
                    if (new FileInfo(FilePath) is FileInfo info && info.Exists && info.Length > MaxFileBytes)
                        StartFile(FilePath, "detailed log (continued)");
                    File.AppendAllText(FilePath, line + "\n");
                }
                catch (Exception)
                {
                    // logging must never get in the way
                }
                Interlocked.Increment(ref s_written);
            }
        }
    }

    internal enum UserLogKind
    {
        Info,
        Success,
        Warning,
        Error,
    }

    /// <summary>One line of the user log.</summary>
    internal sealed class UserLogEntry
    {
        public UserLogEntry(UserLogKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public DateTime Time { get; } = DateTime.Now;

        public UserLogKind Kind { get; }

        public string Text { get; }

        public string TimeText => Time.ToString("HH:mm:ss");

        public override string ToString() => string.Format("{0} {1,-7} {2}", TimeText, Kind, Text);
    }

    /// <summary>
    /// The user log: what matters to the user, in plain words -- runs started and ended,
    /// where the debugger paused, exceptions, values written back, objects baked,
    /// Claude's answers with their time and cost, problems. Shown on the "Log" page of
    /// the editor's left panel and kept in user.log next to the detailed log; warnings
    /// and errors also on the command line (Options page). Everything here also goes to
    /// the detailed log.
    /// </summary>
    internal static class UserLog
    {
        private static readonly List<UserLogEntry> s_entries = new List<UserLogEntry>();
        private static bool s_fileStarted;

        public static string FilePath { get; } = Path.Combine(Log.Folder, "user.log");

        /// <summary>A line was added (any thread).</summary>
        public static event Action<UserLogEntry> Added;

        /// <summary>The log was cleared (UI thread).</summary>
        public static event Action Cleared;

        public static void Info(string format, params object[] args) => Add(UserLogKind.Info, format, args);

        public static void Success(string format, params object[] args) => Add(UserLogKind.Success, format, args);

        public static void Warning(string format, params object[] args) => Add(UserLogKind.Warning, format, args);

        public static void Error(string format, params object[] args) => Add(UserLogKind.Error, format, args);

        public static List<UserLogEntry> Entries
        {
            get
            {
                lock (s_entries)
                    return s_entries.ToList();
            }
        }

        public static void Clear()
        {
            lock (s_entries)
                s_entries.Clear();
            Cleared?.Invoke();
        }

        public static void Add(UserLogKind kind, string format, params object[] args)
        {
            string text;
            try
            {
                text = args == null || args.Length == 0 ? format : string.Format(format, args);
            }
            catch (FormatException)
            {
                text = format;
            }
            text = VNames.Text(text); // V°Watch: the mark the settings choose
            var entry = new UserLogEntry(kind, text);
            lock (s_entries)
            {
                s_entries.Add(entry);
                var keep = Math.Max(100, MiniVivisectionSettings.UserLogKeep);
                if (s_entries.Count > keep)
                    s_entries.RemoveRange(0, s_entries.Count - keep);
                try
                {
                    if (!s_fileStarted)
                    {
                        s_fileStarted = true;
                        Log.StartFile(FilePath, "user log");
                    }
                    File.AppendAllText(FilePath, entry + "\n");
                }
                catch (Exception)
                {
                    // the page still shows it
                }
            }
            Log.Write(LogArea.General, kind == UserLogKind.Error ? LogLevel.Error : kind == UserLogKind.Warning ? LogLevel.Warning : LogLevel.Info,
                "[user] " + text);
            var commandLine = MiniVivisectionSettings.UserLogOnCommandLine;
            if (commandLine == "All" || commandLine != "Off" && kind >= UserLogKind.Warning)
            {
                try
                {
                    RhinoApp.InvokeOnUiThread(new Action(() => RhinoApp.WriteLine("miniVivisection: " + text)));
                }
                catch (Exception)
                {
                    // outside Rhino (the checks)
                }
            }
            try
            {
                Added?.Invoke(entry);
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.General, "user log listener: {0}", Log.Describe(ex));
            }
        }
    }
}
