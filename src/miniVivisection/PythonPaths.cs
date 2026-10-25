using System;
using System.IO;

namespace MiniVivisection
{
    /// <summary>
    /// Makes the plug-in's python folder (next to the .rhp: minivivisection_visualize.py, the
    /// drawing pipeline; rhinoviv.py, the visualizer decorators) importable in Rhino's
    /// Python -- by the plug-in and by scripts (<c>import rhinoviv</c>):
    /// - a scripteditorwatchviz.pth in Rhino 8's Python site-packages
    ///   (%USERPROFILE%\.rhinocode\py39-rh8\Lib\site-packages), read when Python starts;
    /// - and, for a Python already running, the folder put on sys.path before the
    ///   pipeline is imported (PythonBridge.PythonDirectory).
    /// </summary>
    internal static class PythonPaths
    {
        public static string Folder { get; } =
            Path.Combine(Path.GetDirectoryName(typeof(PythonPaths).Assembly.Location) ?? "", "python");

        public static void Install()
        {
            PythonBridge.PythonDirectory = Folder;
            if (!Directory.Exists(Folder))
            {
                UserLog.Warning("The plug-in's python folder is missing ({0}): visualizers and Interpret as won't work.", Folder);
                return;
            }
            try
            {
                var site = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rhinocode", "py39-rh8", "Lib", "site-packages");
                if (!Directory.Exists(site))
                {
                    Log.Info(LogArea.Python, "no Rhino 8 Python site-packages yet ({0}); the folder goes on sys.path when used", site);
                    return;
                }
                var pth = Path.Combine(site, "minivivisection.pth");
                var old = Path.Combine(site, "scripteditorwatchviz.pth"); // before the rename
                if (File.Exists(old))
                    File.Delete(old);
                var line = Folder + Environment.NewLine;
                if (!File.Exists(pth) || File.ReadAllText(pth) != line)
                {
                    File.WriteAllText(pth, line);
                    Log.Info(LogArea.Python, "wrote {0} -> {1}", pth, Folder);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Python, "can't write the .pth file: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Python already running (the plug-in was loaded with the Script Editor
        /// open): the .pth is read only when Python starts, so the folder goes on sys.path
        /// now -- where Python can be reached without waiting (ScriptEditorHook.TryPython).</summary>
        public static void AddToRunningPython(ScriptEditorHook hook)
        {
            try
            {
                if (PythonBridge.IsRunning && Directory.Exists(Folder) && hook.TryPython(() => PythonBridge.AddToSysPath(Folder), out var added) && added)
                    Log.Info(LogArea.Python, "{0} is on the running Python's sys.path", Folder);
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Python, "can't put the python folder on sys.path: {0}", Log.Describe(ex));
            }
        }
    }
}
