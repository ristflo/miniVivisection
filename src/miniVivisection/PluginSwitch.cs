using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eto.Forms;
using Rhino;

namespace MiniVivisection
{
    /// <summary>
    /// Two switches in each Script Editor's top bar, next to the editor's own "toggle
    /// left panel" and "toggle bottom panel" buttons (<c>RCE.Dashboard.RightTray</c>,
    /// which ends with those two):
    /// - <b>AI</b> (setting "AiOn"): the Claude tab and page; off, they're taken out of
    ///   their tab strips (their sessions keep running);
    /// - <b>Debug</b> (setting "PluginOn"): everything else -- the V Watch, Python and
    ///   PowerShell tabs, the V Watch, PowerShell and Log pages, drawing, tooltip
    ///   values and links, value trees, inline values, the hover preview, watch
    ///   expressions, conditional breakpoints (markers hidden, every breakpoint
    ///   pauses), FreePause (from the next pause), selection, Auto edit and the menu
    ///   items; their settings read as off (MiniVivisectionSettings) without changing what's
    ///   stored; objects being edited in Rhino are removed.
    /// Tabs leave their strip (BaseTabControl's item list) and come back in their order;
    /// when the shown one goes, the editor's Terminal or Explorer is shown instead.
    /// </summary>
    internal static class PluginSwitch
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const string AiDescription =
            "Off: V Claude is hidden in every Script Editor (its sessions keep running). On: back as it was.";
        private const string DebugDescription =
            "Off: miniVivisection's debugging features are off in every Script Editor (V tabs and pages, drawing, tooltip values, FreePause\u2026). " +
            "On: back as they were.";

        private static readonly ConditionalWeakTable<object, object> s_added = new ConditionalWeakTable<object, object>();
        private static readonly List<(WeakReference<StateToggle> Toggle, string Key)> s_toggles = new List<(WeakReference<StateToggle>, string)>();
        private static bool s_reported;

        /// <summary>The switches in <paramref name="editor"/>'s top bar, once.</summary>
        public static void TryAdd(object editor)
        {
            if (s_added.TryGetValue(editor, out _))
                return;
            try
            {
                var dashboard = editor.GetType().GetProperty("Dashboard", Any)?.GetValue(editor);
                var tray = dashboard?.GetType().GetProperty("RightTray", Any)?.GetValue(dashboard);
                if (tray == null)
                    return; // not built yet: the next scan
                var row = (TableRow)TabStrips.FindField(tray.GetType(), "m_tray").GetValue(tray);
                var table = (TableLayout)TabStrips.FindField(tray.GetType(), "m_trayContainer").GetValue(tray);
                var column = row.Cells.Count - 2; // the editor's last two: "toggle left panel", "toggle bottom panel"
                var browserButton = column >= 0 ? row.Cells[column].Control : null;
                if (browserButton == null)
                    throw new InvalidOperationException("the top bar's buttons aren't where they were");
                var ai = Toggle(VNames.Of("AI"), "AiOn", VNames.Text(AiDescription));
                var debug = Toggle(VNames.Of("Debug"), "PluginOn", VNames.Text(DebugDescription));
                // The bar is a TableLayout built once: a cell added now isn't shown. So the
                // "toggle left panel" button's cell gets a group: the switches, then the button.
                var group = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, VerticalContentAlignment = VerticalAlignment.Center };
                group.Items.Add(new Label
                {
                    Text = MiniVivisectionPlugin.ProductName + " " + VersionInfo.Short,
                    TextColor = Eto.Drawing.Color.FromArgb(150, 150, 150),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = MiniVivisectionPlugin.ProductName + " " + MiniVivisectionPlugin.FullVersion +
                        " (experimental). Settings: the gear in the left panel, or Tools > Options.",
                });
                group.Items.Add(ai);
                group.Items.Add(debug);
                group.Items.Add(new HelpButton(editor)); // the manual, in the editor
                browserButton.Detach();
                table.Add(group, column, 0);
                group.Items.Add(browserButton);
                s_added.Add(editor, group);
                Log.Info(LogArea.Tabs, "the AI and Debug switches are in the Script Editor's top bar");
            }
            catch (Exception ex)
            {
                while (ex is TargetInvocationException && ex.InnerException != null)
                    ex = ex.InnerException;
                if (!s_reported)
                {
                    s_reported = true;
                    UserLog.Warning("Can't add the AI and Debug switches to the Script Editor's top bar: {0} (the Options page has them too)", ex.Message);
                }
            }
        }

        private static StateToggle Toggle(string caption, string key, string description)
        {
            var toggle = new StateToggle(DrawnIcons.EyeOpen, null, caption, description, showState: true)
            {
                Caption = caption,
                Checked = (bool)MiniVivisectionSettings.Get(key),
            };
            toggle.Toggled += (sender, e) => MiniVivisectionSettings.Set(key, toggle.Checked);
            lock (s_toggles)
                s_toggles.Add((new WeakReference<StateToggle>(toggle), key));
            return toggle;
        }

        /// <summary>A switch's setting changed (UI thread): everything follows.</summary>
        public static void Apply(MiniVivisectionPlugin plugin, string key)
        {
            lock (s_toggles)
            {
                s_toggles.RemoveAll(t => !t.Toggle.TryGetTarget(out _));
                foreach (var (reference, toggleKey) in s_toggles)
                {
                    if (reference.TryGetTarget(out var toggle))
                        toggle.Checked = (bool)MiniVivisectionSettings.Get(toggleKey);
                }
            }
            ConsoleTabs.ApplyVisibility();
            BrowserPages.ApplyVisibility();
            if (key == "AiOn")
            {
                UserLog.Info("AI features turned {0}.", MiniVivisectionSettings.AiOn ? "on" : "off");
                return;
            }
            var on = MiniVivisectionSettings.PluginOn;
            var hook = plugin.Hook;
            if (hook != null)
            {
                foreach (var controls in hook.PausedControls())
                {
                    if (on)
                        hook.RunOnPausedThread(controls, () => hook.Refresh(controls));
                    else
                    {
                        hook.Edits.End(controls);
                        var editor = hook.EditorOf(controls);
                        InlineValues.Clear(editor);
                    }
                }
                hook.Conduit.SetHover(null);
            }
            plugin.Active = MiniVivisectionSettings.Draw;
            RhinoDoc.ActiveDoc?.Views.Redraw();
            UserLog.Info("Debugging features turned {0}.", on ? "on" : "off");
        }

    }
}
