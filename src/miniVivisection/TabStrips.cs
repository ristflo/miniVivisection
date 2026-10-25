using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// The plug-in's tabs in the editor's tab strips (RhinoCodeEditor's TabTray, a
    /// BaseTabControl): shown or hidden by the AI and Debug switches and the tab settings
    /// (PluginSwitch, ConsoleTabs, BrowserPages). Reflection only, so it is checked
    /// outside Rhino against a stand-in strip.
    /// </summary>
    internal static class TabStrips
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>The plug-in's <paramref name="tabs"/> in a tab strip (an editor
        /// BaseTabControl), in their order at its end, those <paramref name="visible"/>
        /// says; the selection stays on the same tab, or moves to
        /// <paramref name="fallbackTray"/> when its tab goes.</summary>
        internal static void SetVisible(object trayBar, IList<object> tabs, Func<object, bool> visible, string fallbackTray)
        {
            var selectedTray = trayBar.GetType().GetProperty("SelectedTray", Any);
            var trayType = Nullable.GetUnderlyingType(selectedTray.PropertyType) ?? selectedTray.PropertyType;
            object TrayOf(object tab) => tab.GetType().GetProperty("Tray", Any)?.GetValue(tab);
            var selected = selectedTray.GetValue(trayBar);
            if (selected != null && tabs.Any(t => Equals(TrayOf(t), selected) && !visible(t)))
            {
                selected = Enum.Parse(trayType, fallbackTray);
                selectedTray.SetValue(trayBar, selected); // switches the content, as a click does
            }
            var items = (IList)trayBar.GetType().GetProperty("Items", Any).GetValue(trayBar);
            // TabTray keeps every tab it was given (_tabs) and rebuilds the strip from it
            // whenever a tab group is toggled -- debugging starts or ends, a help card shows
            // -- so a hidden tab must leave that list too, or it comes straight back.
            var all = TryField(trayBar.GetType(), "_tabs")?.GetValue(trayBar) as IList;
            var gate = FindField(trayBar.GetType(), "_itemsLock").GetValue(trayBar);
            lock (gate)
            {
                foreach (var tab in tabs)
                {
                    items.Remove(tab);
                    all?.Remove(tab);
                }
                foreach (var tab in tabs.Where(visible))
                {
                    items.Add(tab);
                    all?.Add(tab);
                }
            }
            if (selected != null)
                selectedTray.SetValue(trayBar, selected); // its index may have moved
            trayBar.GetType().GetMethod("MakeDirty", Any, null, Type.EmptyTypes, null)?.Invoke(trayBar, null);
            (trayBar as Control)?.Invalidate();
            KeepLaidOut(trayBar);
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> s_followed =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

        /// <summary>The strip lays out its tabs at a paint after MakeDirty (RecalcLayout), and
        /// tabs that don't fit go into its overflow menu. It has no resize handler of its own: a
        /// layout made while it was narrow -- the editor still sizing its window when the plug-in
        /// adds its tabs -- stayed, and the plug-in's tabs were hidden until a window resize. So:
        /// stale on every resize and when shown, and now, after the current UI work, and a
        /// second later.</summary>
        internal static void KeepLaidOut(object trayBar)
        {
            if (!(trayBar is Control strip))
                return;
            var makeDirty = trayBar.GetType().GetMethod("MakeDirty", Any, null, Type.EmptyTypes, null);
            void Dirty()
            {
                try
                {
                    makeDirty?.Invoke(trayBar, null);
                    strip.Invalidate();
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Tabs, "tab strip layout: {0}", Log.Describe(ex));
                }
            }
            if (!s_followed.TryGetValue(trayBar, out _))
            {
                s_followed.Add(trayBar, null);
                strip.SizeChanged += (s, e) => Dirty();
                strip.Shown += (s, e) => Dirty();
            }
            Dirty();
            Application.Instance.AsyncInvoke(Dirty);
            var later = new UITimer { Interval = 1 };
            later.Elapsed += (s, e) =>
            {
                later.Stop();
                Dirty();
            };
            later.Start();
        }

        /// <summary>The tab the strip's AddTab just made (its last item).</summary>
        internal static object LastTab(object trayBar) =>
            ((IEnumerable)trayBar.GetType().GetProperty("Items", Any).GetValue(trayBar)).Cast<object>().Last();

        internal static FieldInfo TryField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            return null;
        }

        internal static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }
    }
}
