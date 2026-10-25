using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Runtime.Code.Execution.Debugging;

namespace MiniVivisection
{
    /// <summary>
    /// A setting changed -- on the Settings page, the Options page, a switch or the
    /// command: what the Script Editors show follows at once (UI thread). Settings read
    /// at every use (tooltips, links, selection, Claude, logs, timeouts) need nothing
    /// here; FreePause applies from the next pause (a pause can't change how it waits).
    /// </summary>
    internal static class SettingEffects
    {
        public static void Apply(MiniVivisectionPlugin plugin, string key)
        {
            var hook = plugin.Hook;
            try
            {
                switch (key)
                {
                    case "PluginOn":
                    case "AiOn":
                        PluginSwitch.Apply(plugin, key);
                        EditorExamples.ApplyAll();
                        DebugRunButton.ApplyAll();
                        GrasshopperToolbar.Apply();
                        GrasshopperPrompts.ApplyTabs();
                        break;
                    case "GrasshopperScripts":
                    case "GrasshopperButton":
                        GrasshopperToolbar.Apply();
                        break;
                    case "GhPromptsButton":
                    case "GhPromptClaude":
                    case "GhPromptPython":
                    case "GhPromptPowerShell":
                        GrasshopperToolbar.Apply();
                        GrasshopperPrompts.ApplyTabs();
                        break;
                    case "GrasshopperScriptPanels":
                    case "GrasshopperMarkdown":
                    case "GrasshopperMarkdownParser":
                        GrasshopperHiddenEditors.RedrawCanvas(); // the Panels drawn as code, or as text again
                        break;
                    case "TerminalFontSize":
                        TerminalView.ApplyFontSizeAll();
                        break;
                    case "ColorsPython":
                    case "ColorsPowerShell":
                    case "ColorsClaude":
                        TerminalView.ApplyColorsAll();
                        break;
                    case "BreakHotkey":
                        if (hook.ControlsIn(Rhino.Runtime.Code.Execution.Debugging.DebugControlsState.Debugging).Count > 0 ||
                            hook.PausedControls().Count > 0)
                            BreakHotkey.Refresh(); // during a run: the new key now
                        break;
                    case "DebugRunButton":
                        DebugRunButton.ApplyAll();
                        break;
                    case "ExamplesTab":
                        EditorExamples.ApplyAll();
                        break;
                    case "NameDots":
                    case "NameDotOffset":
                    case "NameDotOpacity":
                    case "NameDotPointer":
                        RhinoDoc.ActiveDoc?.Views.Redraw();
                        break;
                    case "Draw":
                        plugin.Active = MiniVivisectionSettings.Draw;
                        if (MiniVivisectionSettings.Draw)
                            ForPaused(hook, (controls, editor) => hook.RunOnPausedThread(controls, () => hook.Refresh(controls))); // drawn again now
                        RhinoDoc.ActiveDoc?.Views.Redraw();
                        break;
                    case "TabRhinoWatch":
                    case "TabPython":
                    case "TabPowerShell":
                    case "TabThreads":
                    case "TabClaude":
                        ConsoleTabs.ApplyVisibility();
                        break;
                    case "PageClaude":
                    case "PageRhinoWatch":
                    case "PagePowerShell":
                    case "PageThreads":
                    case "PageLog":
                    case "PageSettings":
                        BrowserPages.ApplyVisibility();
                        break;
                    case "InlineValues":
                    case "ShowChanges":
                        ForPaused(hook, (controls, editor) =>
                        {
                            if (!MiniVivisectionSettings.InlineValues)
                                InlineValues.Clear(editor);
                            else if (!hook.RunOnPausedThread(controls, () => InlineValues.AtPause(controls, editor, newPause: false)))
                                Log.Warning(LogArea.Hover, "values at the line ends not drawn again after {0} changed: {1}", key, WatchActions.NotReachable);
                        });
                        break;
                    case "HoverPreview":
                        if (!MiniVivisectionSettings.HoverPreview && hook != null && hook.Conduit.SetHover(null))
                            RhinoDoc.ActiveDoc?.Views.Redraw();
                        break;
                    case "WatchExpressions":
                        // The editor's Watch tab evaluates its expressions again.
                        ForPaused(hook, (controls, editor) => hook.RunOnPausedThread(controls, () => hook.Refresh(controls)));
                        break;
                    case "RhinoWatchesAutoEdit":
                        ForPaused(hook, (controls, editor) => hook.ApplyAutoEdit(editor));
                        break;
                    case "VisualizerScripts":
                        // Loaded now while paused (VisualizerScripts loads them at the next pause otherwise), then drawn again.
                        ForPaused(hook, (controls, editor) => hook.RunOnPausedThread(controls, () =>
                        {
                            UserLog.Info("Visualizer scripts: {0}", VisualizerScripts.LoadHere());
                            hook.Refresh(controls);
                        }));
                        break;
                    case "SelectFromTabs":
                        if (!MiniVivisectionSettings.SelectFromTabs && hook != null && hook.Conduit.SetSelected(new HashSet<(string, int)>()))
                            RhinoDoc.ActiveDoc?.Views.Redraw();
                        break;
                    case "PowerShellPath":
                    case "PowerShellLoadProfile":
                        if (PowerShellTerminal.ShutdownAll() > 0)
                            UserLog.Info("PowerShell ended: the next command in V PowerShell starts it with the new setting.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Settings, "{0} changed, but applying it failed: {1}", key, Log.Describe(ex));
            }
        }

        /// <summary>Each paused debugger and its Script Editor.</summary>
        private static void ForPaused(ScriptEditorHook hook, Action<IDebugControls, object> action)
        {
            if (hook == null)
                return;
            foreach (var controls in hook.PausedControls())
            {
                var editor = hook.EditorOf(controls);
                if (editor != null)
                    action(controls, editor);
            }
        }
    }
}
