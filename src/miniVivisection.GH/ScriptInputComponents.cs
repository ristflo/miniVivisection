using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using HarmonyLib;

namespace MiniVivisection.GH
{
    /// <summary>
    /// A double-click on a script component that runs its "script" input: Grasshopper does
    /// nothing then (RhinoCode's ScriptContext.OpenScript returns at once). Instead, its script --
    /// the file, or a Panel's text as a working file -- and its library input's files open in
    /// the Script Editor, the script's tab in front (the .rhp's
    /// GrasshopperInputScripts.OpenWithLibraries, by reflection).
    ///
    /// A prefix on GH_Canvas.DoubleClick_InactiveObject (not generic; the component's own
    /// double-click is on RhinoCode's generic component attributes). Only on the component
    /// itself, not its inputs and outputs, and without Shift, Ctrl or Alt.
    /// </summary>
    internal static class ScriptInputComponents
    {
        private static Harmony s_harmony;

        public static void Install()
        {
            if (s_harmony != null)
                return;
            try
            {
                var doubleClick = typeof(GH_Canvas).GetMethod("DoubleClick_InactiveObject", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    ?? throw new MissingMethodException(typeof(GH_Canvas).FullName, "DoubleClick_InactiveObject");
                var harmony = new Harmony("minivivisection.gh.scriptinputcomponents");
                harmony.Patch(doubleClick, prefix: new HarmonyMethod(typeof(ScriptInputComponents), nameof(DoubleClickPrefix)));
                s_harmony = harmony;
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("miniVivisection: a double-click on a component with a script input stays as Grasshopper has it: {0}", ex.Message);
            }
        }

        private static bool DoubleClickPrefix(GH_Canvas __instance, GH_CanvasMouseEvent e, ref bool __result)
        {
            try
            {
                if (e.Button != MouseButtons.Left || Control.ModifierKeys != Keys.None || !__instance.IsDocument)
                    return true;
                var attributes = __instance.Document.FindAttribute(e.CanvasLocation, false);
                if (attributes == null || !ReferenceEquals(attributes, attributes.GetTopLevel))
                    return true; // an input or output: as Grasshopper has it
                var component = attributes.DocObject;
                if (component?.GetType().GetProperty("UsingScriptInputParam")?.GetValue(component) as bool? != true)
                    return true;
                var open = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection")
                    ?.GetType("MiniVivisection.GrasshopperInputScripts")
                    ?.GetMethod("OpenWithLibraries", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (open == null)
                    return true;
                open.Invoke(null, new object[] { component });
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("miniVivisection: a double-click on a component with a script input: {0}", ex.Message);
                return true;
            }
        }
    }
}
