using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// The visualizer scripts of the Options page (Python files with
    /// <c>@rhinoviv.visualize</c> / <c>@rhinoviv.inverse</c> functions): run in the
    /// paused script's context at the first pause of every run -- a new run defines its
    /// classes anew, and the registry knows them by class -- and again when one is saved
    /// (right away while paused, else at the next pause). <c>reload_visualizers()</c> in
    /// the Python tab runs them on demand. Each result goes to the user log.
    /// </summary>
    internal static class VisualizerScripts
    {
        private static readonly List<FileSystemWatcher> s_watchers = new List<FileSystemWatcher>();
        private static ScriptEditorHook s_hook;
        private static volatile bool s_due = true;
        private static DateTime s_lastChange;

        public static void Start(ScriptEditorHook hook)
        {
            s_hook = hook;
            WatchFiles();
            MiniVivisectionSettings.Changed += key =>
            {
                if (key == "VisualizerScripts" || key == "ReloadVisualizersOnSave")
                {
                    WatchFiles();
                    s_due = true;
                }
            };
        }

        /// <summary>A run starts: load at its first pause.</summary>
        public static void NewRun() => s_due = true;

        /// <summary>At a pause, on the paused thread: the scripts, if a run started or one
        /// changed since they were last run.</summary>
        public static void AtPause()
        {
            if (!s_due || MiniVivisectionSettings.VisualizerScripts.Length == 0)
                return;
            s_due = false;
            Load();
        }

        /// <summary>reload_visualizers(), on the paused thread: a summary for the Python tab.</summary>
        public static string LoadHere()
        {
            if (MiniVivisectionSettings.VisualizerScripts.Length == 0)
                return "No visualizer scripts set (Tools > Options > miniVivisection > Visualizers).";
            var lines = Load();
            return lines.Count == 0 ? "Nothing loaded." : string.Join("\n", lines);
        }

        private static List<string> Load()
        {
            var summary = new List<string>();
            var paths = MiniVivisectionSettings.VisualizerScripts;
            foreach (var missing in paths.Where(p => !File.Exists(p)))
            {
                UserLog.Warning("Visualizer script not found: {0}", missing);
                summary.Add("not found: " + missing);
            }
            var existing = paths.Where(File.Exists).ToList();
            if (existing.Count == 0)
                return summary;
            try
            {
                foreach (var line in PythonBridge.LoadVisualizerScripts(existing))
                {
                    var parts = line.Split(new[] { '|' }, 3);
                    if (parts.Length < 3)
                        continue;
                    var file = Path.GetFileName(parts[1]);
                    if (parts[0] == "ok")
                    {
                        UserLog.Success("Visualizers from {0}: {1}", file, parts[2].Length > 0 ? parts[2] : "(nothing defined)");
                        summary.Add(file + ": " + parts[2]);
                    }
                    else
                    {
                        UserLog.Error("Visualizer script {0}: {1}", file, parts[2]);
                        summary.Add(file + ": " + parts[2]);
                    }
                }
            }
            catch (Exception ex)
            {
                UserLog.Error("Couldn't run the visualizer scripts: {0}", ScriptEditorHook.FirstLine(ex));
                summary.Add(ScriptEditorHook.FirstLine(ex));
            }
            return summary;
        }

        private static void WatchFiles()
        {
            lock (s_watchers)
            {
                foreach (var watcher in s_watchers)
                    watcher.Dispose();
                s_watchers.Clear();
                foreach (var path in MiniVivisectionSettings.VisualizerScripts.Where(File.Exists))
                {
                    try
                    {
                        var watcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileName(path))
                        {
                            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        };
                        watcher.Changed += (s, e) => OnSaved();
                        watcher.EnableRaisingEvents = true;
                        s_watchers.Add(watcher);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(LogArea.Python, "can't watch {0}: {1}", path, Log.Describe(ex));
                    }
                }
            }
        }

        /// <summary>A script was saved (editors write in bursts: one reload).</summary>
        private static void OnSaved()
        {
            if (!MiniVivisectionSettings.ReloadVisualizersOnSave || (DateTime.Now - s_lastChange).TotalMilliseconds < 500)
                return;
            s_lastChange = DateTime.Now;
            s_due = true;
            var hook = s_hook;
            var paused = hook?.PausedControls().FirstOrDefault();
            if (paused == null)
                return; // at the next pause
            System.Threading.Tasks.Task.Delay(300).ContinueWith(_ =>
            {
                if (!hook.RunOnPausedThread(paused, () =>
                {
                    AtPause();
                    hook.Refresh(paused);
                }))
                    Log.Info(LogArea.Python, "visualizer scripts: reloaded at the next pause");
            });
        }
    }
}
