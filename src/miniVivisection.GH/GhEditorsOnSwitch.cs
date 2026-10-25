using System;
using System.Linq;
using System.Reflection;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;

namespace MiniVivisection.GH
{
    /// <summary>
    /// Another definition on the canvas -- one opened, or a switch to another open one: the script
    /// editors of the definition left are closed first (setting Grasshopper > "Close script editors
    /// when the definition changes"), as their close buttons would (Grasshopper may still ask about
    /// changes not applied). Only editors on screen: a hidden one (Always debug's) is the .rhp's to
    /// keep; and not the editor of a script paused in the debugger -- closing it would end the pause.
    /// </summary>
    internal static class GhEditorsOnSwitch
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static GH_Canvas s_canvas;

        /// <summary>When the .gha loads: the canvas followed once it's there.</summary>
        public static void Install()
        {
            Instances.CanvasCreated += Follow;
            if (Instances.ActiveCanvas != null)
                Follow(Instances.ActiveCanvas);
        }

        private static void Follow(GH_Canvas canvas)
        {
            if (canvas == null || ReferenceEquals(canvas, s_canvas))
                return;
            s_canvas = canvas;
            canvas.DocumentChanged += (sender, e) =>
            {
                if (e.OldDocument != null && !ReferenceEquals(e.OldDocument, e.NewDocument) && ScriptPanels.Live("GrasshopperCloseEditors", true))
                    CloseEditors(e.OldDocument);
            };
        }

        /// <summary>Closes the script editors on screen of <paramref name="document"/>'s components;
        /// how many.</summary>
        internal static int CloseEditors(GH_Document document)
        {
            var closed = 0;
            var paused = PausedComponent();
            foreach (var component in document.Objects.ToList())
            {
                try
                {
                    if (ReferenceEquals(component, paused))
                    {
                        Rhino.RhinoApp.WriteLine("miniVivisection: '{0}' is paused in the debugger -- its script editor stays open.", component.NickName);
                        continue;
                    }
                    var editor = ScriptEditorOf(component);
                    if (editor?.GetType().GetProperty("HasVisibleEditor", Any)?.GetValue(editor) as bool? != true)
                        continue;
                    var codeEditor = editor.GetType().GetProperty("CodeEditor", Any)?.GetValue(editor);
                    if (codeEditor?.GetType().GetField("_rce", Any)?.GetValue(codeEditor) is Eto.Forms.Window window && window.Visible)
                    {
                        window.Close();
                        closed++;
                    }
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine("miniVivisection: the script editor of '{0}' couldn't be closed: {1}", component.NickName, ex.Message);
                }
            }
            return closed;
        }

        // A script component's editor (RhinoCodePlatform.GH.Context's ScriptEditor), or null: its Context's
        private static object ScriptEditorOf(IGH_DocumentObject component)
        {
            object context = null;
            for (var type = component.GetType(); type != null && context == null; type = type.BaseType)
            {
                var field = type.GetField("Context", Any | BindingFlags.DeclaredOnly);
                var property = field == null ? type.GetProperty("Context", Any | BindingFlags.DeclaredOnly) : null;
                context = field?.GetValue(component) ?? property?.GetValue(component);
            }
            return context?.GetType().GetProperty("ScriptEditor", Any)?.GetValue(context);
        }

        // The .rhp's script component paused now (GrasshopperRuns.RunningComponent), or null
        private static object PausedComponent()
        {
            try
            {
                var rhp = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "miniVivisection");
                return rhp?.GetType("MiniVivisection.GrasshopperRuns")?.GetProperty("RunningComponent", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
