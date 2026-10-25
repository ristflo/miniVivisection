using System;
using System.Linq;
using System.Reflection;
using Rhino;
using Rhino.PlugIns;

namespace MiniVivisection
{
    /// <summary>
    /// Experimental: while the Rhino Script Editor's debugger is paused, draws what
    /// its V Watch tab holds in the viewports and keeps Rhino usable. See README.md.
    /// </summary>
    public class MiniVivisectionPlugin : PlugIn
    {
        public MiniVivisectionPlugin()
        {
            Instance = this;
        }

        public static MiniVivisectionPlugin Instance { get; private set; }

        /// <summary>Loaded with Rhino, so editors opened before any command of this
        /// plug-in ran are followed too.</summary>
        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        /// <summary>When this .rhp was built: local time with the zone (VersionInfo).</summary>
        internal static string BuildTime => VersionInfo.BuiltLocal;

        /// <summary>"0.2.0 (build 57), 2026-10-09 14:05 AST".</summary>
        internal static string FullVersion => VersionInfo.Full;

        internal WatchConduit Conduit { get; private set; }

        private DrawnPicker _picker;

        internal ScriptEditorHook Hook { get; private set; }

        /// <summary>Whether watched curves are drawn (MiniVivisection command).</summary>
        internal bool Active
        {
            get => Conduit.Enabled;
            set
            {
                if (value == Conduit.Enabled)
                    return;
                if (!value)
                    Conduit.ClearAll();
                Conduit.Enabled = value;
                RhinoDoc.ActiveDoc?.Views.Redraw();
            }
        }

        /// <summary>The plug-in's name, as Rhino shows it (the assembly's title).</summary>
        internal const string ProductName = "miniVivisection";

        /// <summary>major.minor.patch, e.g. 0.2.0 (Version.props).</summary>
        internal static string ProductVersion => VersionInfo.Version;

        internal static int BuildNumber => VersionInfo.Build;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // Built on Rhino 8's Script Editor internals (RhinoCode 8.36, by reflection and
            // Harmony): not loaded in another Rhino, e.g. a zip copy dragged onto Rhino 9.
            // The Package Manager offers it to Rhino 8 only (its rh8_36-win tag).
            if (RhinoApp.Version.Major != 8)
            {
                errorMessage = string.Format("miniVivisection {0} is for Rhino 8 and was not loaded in Rhino {1}.", ProductVersion, RhinoApp.Version.Major);
                return LoadReturnCode.ErrorShowDialog;
            }
            MiniVivisectionSettings.Migrate();
            MiniVivisectionSettings.MigrateRenamed();
            MiniVivisectionSettings.ApplyLogLevels();
            Safely("TestDesktop.Start", () => TestDesktop.Start()); // live tests only: their windows on another virtual desktop
            MiniVivisectionSettings.Changed += OnSettingChanged;
            PythonPaths.Install();
            Conduit = new WatchConduit { Enabled = MiniVivisectionSettings.Draw };
            Hook = new ScriptEditorHook(Conduit);
            Hook.Start();
            PythonPaths.AddToRunningPython(Hook);
            BreakHotkey.Start(Hook);
            Safely("GrasshopperDebug.Start", () => GrasshopperDebug.Start());
            Safely("GrasshopperPauseGuard.Start", () => GrasshopperPauseGuard.Start());
            Safely("GrasshopperRuns.Start", () => GrasshopperRuns.Start());
            Safely("GrasshopperToolbar.Start", () => GrasshopperToolbar.Start());
            Safely("GrasshopperPausedData.Start", () => GrasshopperPausedData.Start());
            Safely("GrasshopperHiddenEditors.Start", () => GrasshopperHiddenEditors.Start());
            Safely("GrasshopperPauseCanvas.Start", () => GrasshopperPauseCanvas.Start());
            Safely("GrasshopperLink.Ensure", () => GrasshopperLink.Ensure());
            Safely("ScriptRunCommand.Start", () => ScriptRunCommand.Start());
            Safely("MarkdownTextMouse.Install", () => MarkdownTextMouse.Install());
            Safely("MarkdownRhinoText.InstallStyleDefault", () => MarkdownRhinoText.InstallStyleDefault());
            Safely("RhinoProtect.Install", () => RhinoProtect.Install());
            Safely("RhinoToolbar.Install", () => RhinoToolbar.Install());
            Safely("RhinoAiInstaller.Start", () => RhinoAiInstaller.Start());
            // The lessons' working copies, also the Grasshopper ones (opened with Grasshopper's
            // File > Open, before any Script Editor): ready at load.
            Safely("EditorExamples.WorkingCopies", () => EditorExamples.WorkingCopies(EditorExamples.ShippedFolder, EditorExamples.WorkingFolder));
            VisualizerScripts.Start(Hook);
            _picker = new DrawnPicker(Hook) { Enabled = true };
            Safely("FreePause.TryInstall", () => FreePause.TryInstall());
            BreakpointConditions.TerminalWriter = (controls, text) =>
            {
                var editor = Hook?.EditorOf(controls);
                if (editor != null)
                    RhinoApp.InvokeOnUiThread(new Action(() => new ScriptEditorAccess(editor).AppendTerminal(text + Environment.NewLine)));
            };
            Safely("BreakpointConditions.TryInstall", () => BreakpointConditions.TryInstall());
            Safely("TraceLockGuard.TryInstall", () => TraceLockGuard.TryInstall()); // OnLoad runs on the UI thread: it records that thread
            Safely("StepTracking.TryInstall", () => StepTracking.TryInstall());
            Safely("HoverValues.TryInstall", () => HoverValues.TryInstall(Hook));
            // Always on the command line: shows which build runs.
            RhinoApp.WriteLine("miniVivisection {0} loaded (experimental). Settings: Tools > Options > miniVivisection.", FullVersion);
            Log.Info(LogArea.Startup, "loaded: {0}; detailed log {1}", FullVersion, Log.FilePath);
            UserLog.Info("miniVivisection {0} loaded.", FullVersion);
            return LoadReturnCode.Success;
        }

        /// <summary>One part's setup at load: if it fails -- even before its own error handling,
        /// as when a type it names is gone after a Rhino update -- the rest still loads.</summary>
        private static void Safely(string what, Action install)
        {
            try
            {
                install();
            }
            catch (Exception ex)
            {
                UserLog.Warning("{0} couldn't be set up: {1}", what, ex.Message);
            }
        }

        /// <summary>The plug-in's page in Rhino's Options dialog.</summary>
        protected override void OptionsDialogPages(System.Collections.Generic.List<Rhino.UI.OptionsDialogPage> pages)
        {
            pages.Add(new MiniVivisectionOptionsPage());
        }

        private void OnSettingChanged(string key)
        {
            // What the Script Editors show follows at once.
            RhinoApp.InvokeOnUiThread(new System.Action(() => SettingEffects.Apply(this, key)));
        }

        protected override void OnShutdown()
        {
            MiniVivisectionSettings.Changed -= OnSettingChanged;
            GrasshopperPrompts.Shutdown();
            PowerShellTerminal.ShutdownAll();
            GrasshopperInputScripts.Shutdown();
            ClaudeChat.DeleteConfigs();
            if (_picker != null)
                _picker.Enabled = false;
            Hook?.Dispose();
            if (Conduit != null)
                Conduit.Enabled = false;
            Log.Shutdown(); // last: a log without this line was a session that crashed
        }
    }
}
