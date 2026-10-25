using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Rhino;
using Rhino.UI;

namespace MiniVivisection
{
    /// <summary>
    /// The miniVivisection toolbar in Rhino: the manual, the examples' overview in Rhino and in
    /// Grasshopper, the lessons folder, the settings, MarkdownText, ProtectModel (toolbar/miniVivisection.rui next to the plug-in, made by
    /// tools/make_toolbar.py; Rhino loads it by the plug-in's name, else it is opened here). Shown
    /// or hidden in its container as V°Settings > Rhino > "Help, examples and settings toolbar" says -- when
    /// the setting changes, and once for each toolbar the plug-in brings (shown by default); in
    /// between, Rhino keeps it where the user put it, or closed.
    /// </summary>
    internal static class RhinoToolbar
    {
        public const string Name = "miniVivisection";

        private static string Shipped => Path.Combine(HelpCommands.Folder, "toolbar", Name + ".rui");

        /// <summary>Once Rhino is idle: the toolbar file found (or opened), shown when it is new and the setting says so.</summary>
        public static void Install()
        {
            EventHandler then = null;
            then = (s, e) =>
            {
                RhinoApp.Idle -= then;
                try
                {
                    if (File == null && System.IO.File.Exists(Shipped))
                        RhinoApp.ToolbarFiles.Open(Shipped);
                    // A toolbar the plug-in hasn't shown yet (the first, or another version's): as the setting says
                    var stamp = File == null ? null : System.IO.File.GetLastWriteTimeUtc(Shipped).Ticks.ToString();
                    var store = MiniVivisectionPlugin.Instance?.Settings;
                    if (stamp != null && store != null && store.GetString("HelpToolbarShownFor", "") != stamp)
                    {
                        Apply();
                        store.SetString("HelpToolbarShownFor", stamp);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(LogArea.General, "Toolbar: {0}", Log.Describe(ex));
                }
            };
            RhinoApp.Idle += then;
            MiniVivisectionSettings.Changed += key =>
            {
                if (key == "HelpToolbar")
                    RhinoApp.InvokeOnUiThread((Action)Apply);
            };
        }

        /// <summary>The toolbar file Rhino has open (null: none).</summary>
        public static ToolbarFile File => RhinoApp.ToolbarFiles.FindByName(Name, true);

        /// <summary>The toolbar shown or hidden, as the setting says. Rhino 8 shows a toolbar in a
        /// container, and RhinoCommon has nothing for it. Rhino may hold it in more than one (one it
        /// restored from its window layout, one -ShowToolbar made): shown -- nothing when one of them
        /// is, else -ShowToolbar; hidden -- every one of them. Rhino's own (internal) dock bars say
        /// which and hide them; without them, -Containers Show "miniVivisection" Hide.</summary>
        public static void Apply()
        {
            var file = File;
            if (file == null || file.ToolbarCount == 0 || Rhino.Commands.Command.InCommand())
                return;
            var show = MiniVivisectionSettings.HelpToolbar;
            var containers = Containers(file.GetToolbar(0).Id);
            if (show)
            {
                if (containers?.Any(c => c.Visible) == true)
                    return;
                RhinoApp.RunScript("_-ShowToolbar \"" + Name + "\" _Enter", false);
            }
            else if (containers != null)
            {
                foreach (var container in containers.Where(c => c.Visible))
                    container.Hide();
            }
            else
                RhinoApp.RunScript("_-Containers _Show \"" + Name + "\" _Hide _Enter", false);
            Log.Info(LogArea.General, "Toolbar: {0}", show ? "shown" : "hidden");
        }

        /// <summary>A container (dock bar) holding the toolbar.</summary>
        private sealed class Container
        {
            public Guid Id;
            public bool Visible;
            public Action Hide;
        }

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        /// <summary>The containers holding the toolbar (by Rhino.UI's TabPanelDockBars); null when
        /// they can't be told (another Rhino).</summary>
        private static List<Container> Containers(Guid toolbar)
        {
            try
            {
                var bars = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Rhino.UI")?.GetType("Rhino.UI.Internal.TabPanels.TabPanelDockBars");
                var list = bars?.GetMethod("GetDockBarList", Any, null, Type.EmptyTypes, null)?.Invoke(null, null) as System.Collections.IEnumerable;
                var isVisible = bars?.GetMethod("IsVisible", Any, null, new[] { typeof(RhinoDoc), typeof(Guid) }, null);
                var setShown = bars?.GetMethod("Show", Any, null, new[] { typeof(RhinoDoc), typeof(Guid), typeof(bool) }, null);
                if (list == null || isVisible == null || setShown == null)
                    return null;
                var doc = RhinoDoc.ActiveDoc;
                var found = new List<Container>();
                foreach (var pair in list)
                {
                    // (name, TabPanelDockBar) -> its Collection -> Items: the toolbars and panels in it
                    var bar = pair?.GetType().GetProperty("Item2")?.GetValue(pair);
                    var collection = bar?.GetType().GetProperty("Collection", Any)?.GetValue(bar);
                    if (!(collection?.GetType().GetProperty("Items", Any)?.GetValue(collection) is System.Collections.IEnumerable items))
                        continue;
                    var holds = items.Cast<object>().Any(item => item != null &&
                        item.GetType().GetProperty("IsToolBar", Any)?.GetValue(item) is bool isToolBar && isToolBar &&
                        item.GetType().GetProperty("Id", Any)?.GetValue(item) is Guid id && id == toolbar);
                    if (!holds || !(bar.GetType().GetProperty("Id", Any)?.GetValue(bar) is Guid barId))
                        continue;
                    found.Add(new Container
                    {
                        Id = barId,
                        Visible = (bool)isVisible.Invoke(null, new object[] { doc, barId }),
                        Hide = () => setShown.Invoke(null, new object[] { doc, barId, false }),
                    });
                }
                return found;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.General, "Toolbar: its containers: {0}", Log.Describe(ex));
                return null;
            }
        }
    }
}
