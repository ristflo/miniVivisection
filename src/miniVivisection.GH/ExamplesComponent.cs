using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace MiniVivisection.GH
{
    /// <summary>
    /// The help components on the miniVivisection tab (Help): a component with a button under it
    /// -- a click on it, or a double-click on the component, does what it says. No inputs, no
    /// outputs. Shown while V°Settings > Grasshopper > "Help, examples and settings components" is
    /// on (from Grasshopper's next start). V°Examples, V°Manual, V°Settings.
    /// </summary>
    public abstract class HelpButtonComponent : GH_Component
    {
        protected HelpButtonComponent(string name, string description)
            : base(GhNames.Of(name), GhNames.Of(name), description, "miniVivisection", "Help")
        {
        }

        /// <summary>The button's words.</summary>
        internal abstract string ButtonText { get; }

        /// <summary>What the button does (out of the canvas's click).</summary>
        internal abstract void Click();

        /// <summary>The icon: the toolbar's (tools/make_toolbar.py), inside the .gha.</summary>
        protected abstract string IconName { get; }

        private Bitmap _icon;

        protected override Bitmap Icon => _icon ?? (_icon = LoadIcon(IconName));

        private static Bitmap LoadIcon(string name)
        {
            using var stream = typeof(HelpButtonComponent).Assembly.GetManifestResourceStream(name);
            return stream == null ? null : new Bitmap(stream);
        }

        public override GH_Exposure Exposure => ScriptPanels.Live("GhHelpComponents", true) ? GH_Exposure.primary : GH_Exposure.hidden;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
        }

        public override void CreateAttributes() => m_attributes = new HelpButtonAttributes(this);

        /// <summary>The button's action, out of the canvas's mouse click (the canvas may get another definition).</summary>
        internal void ClickSoon() => Instances.DocumentEditor?.BeginInvoke(new Action(() =>
        {
            try
            {
                Click();
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine(Name + ": " + ex.Message);
            }
        }));

        /// <summary>A Rhino command run (the .rhp's); said when it isn't there.</summary>
        internal static void RunRhinoCommand(string script)
        {
            if (!Rhino.RhinoApp.RunScript(script, false))
                Rhino.RhinoApp.WriteLine("miniVivisection: {0} couldn't run -- is the miniVivisection plug-in loaded?", script.Trim('_', '-', '!', ' '));
        }
    }

    /// <summary>V°Examples: opens the Grasshopper examples' overview -- your copy of gh0_overview.gh
    /// (the .rhp makes the copies: MiniVivisectionSettings.LessonsFolder; without it, the one next to the
    /// plug-in) -- or shows it when it is open; its menu also opens the lessons folder.</summary>
    public sealed class ExamplesComponent : HelpButtonComponent
    {
        public ExamplesComponent()
            : base("Examples", "All of miniVivisection's examples: a click on its button opens the overview of the Grasshopper lessons (your copies), every lesson linked. Its menu: the lessons folder.")
        {
        }

        public override Guid ComponentGuid => new Guid("b8e2d4a6-3c51-4f7e-9a0d-5e6f7a8b9c2d");

        protected override string IconName => "ghexamples_24.png";

        internal override string ButtonText => "Open the overview";

        internal override void Click()
        {
            var error = OpenOverview();
            if (error != null)
                Rhino.RhinoApp.WriteLine(GhNames.Of("Examples") + ": " + error);
        }

        /// <summary>The lessons folder: your copies (the .rhp's), else the examples next to the plug-in.</summary>
        internal static string Folder
        {
            get
            {
                var copies = ScriptPanels.Live<string>("LessonsFolder", null);
                if (!string.IsNullOrEmpty(copies) && Directory.Exists(copies))
                    return copies;
                return Path.Combine(Path.GetDirectoryName(typeof(ExamplesComponent).Assembly.Location) ?? "", "examples");
            }
        }

        internal static string OverviewPath => Path.Combine(Folder, "grasshopper", "gh0_overview.gh");

        /// <summary>The overview opened, or shown when it is open. What went wrong, or null.</summary>
        internal static string OpenOverview()
        {
            var path = OverviewPath;
            if (!File.Exists(path))
                return "No examples' overview (" + path + ").";
            var editor = Instances.DocumentEditor;
            var canvas = Instances.ActiveCanvas;
            if (editor == null || canvas == null)
                return "Grasshopper isn't open.";
            if (!editor.Visible)
                editor.Show();
            GH_Document Opened() => Instances.DocumentServer.FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) &&
                string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            var doc = Opened();
            if (doc == null)
            {
                editor.ScriptAccess_OpenDocument(path);
                doc = Opened();
                if (doc == null)
                    return "Grasshopper didn't open " + path + ".";
            }
            if (!ReferenceEquals(canvas.Document, doc))
                canvas.Document = doc;
            return null;
        }

        internal static void OpenFolder()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine(GhNames.Of("Examples") + ": can't open " + Folder + ": " + ex.Message);
            }
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "Open the overview", (s, e) => ClickSoon()).ToolTipText = OverviewPath;
            Menu_AppendItem(menu, "Open the lessons folder", (s, e) => OpenFolder()).ToolTipText = Folder;
        }
    }

    /// <summary>V°Manual: the manual (the PDF: miniVivisectionManual); its menu: as Markdown, in the
    /// Script Editor (-miniVivisectionManual Markdown, as V°Help).</summary>
    public sealed class ManualComponent : HelpButtonComponent
    {
        public ManualComponent()
            : base("Manual", "The miniVivisection manual: a click on its button opens it (the PDF). Its menu: the manual as Markdown, in the Script Editor.")
        {
        }

        public override Guid ComponentGuid => new Guid("3e7a9c1b-5d24-4f86-8b0e-6a2c4d9f1e57");

        protected override string IconName => "manual_24.png";

        internal override string ButtonText => "Open the manual";

        internal override void Click() => RunRhinoCommand("_miniVivisectionManual");

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(menu, "The manual (PDF)", (s, e) => ClickSoon());
            Menu_AppendItem(menu, "The manual as Markdown, in the Script Editor", (s, e) => Instances.DocumentEditor?.BeginInvoke(new Action(() => RunRhinoCommand("_-miniVivisectionManual _Markdown"))));
        }
    }

    /// <summary>V°Settings: miniVivisection's settings -- Rhino's Options dialog, at the
    /// miniVivisection page (miniVivisectionSettings).</summary>
    public sealed class SettingsComponent : HelpButtonComponent
    {
        public SettingsComponent()
            : base("Settings", "miniVivisection's settings: a click on its button opens Rhino's Options at the miniVivisection page (General, Rhino and Grasshopper tabs).")
        {
        }

        public override Guid ComponentGuid => new Guid("9c4b2e7d-1a63-4f58-a0d9-7e3b5c8f2a16");

        protected override string IconName => "settings_24.png";

        internal override string ButtonText => "Open the settings";

        internal override void Click() => RunRhinoCommand("_miniVivisectionSettings");
    }

    /// <summary>A help component on the canvas: the component, and under it its button.</summary>
    internal sealed class HelpButtonAttributes : GH_ComponentAttributes
    {
        private RectangleF _button;

        public HelpButtonAttributes(HelpButtonComponent owner)
            : base(owner)
        {
        }

        private HelpButtonComponent Component => (HelpButtonComponent)Owner;

        protected override void Layout()
        {
            base.Layout();
            var bounds = Bounds;
            var needed = GH_FontServer.StringWidth(Component.ButtonText, GH_FontServer.StandardAdjusted) + 20;
            if (bounds.Width < needed)
                bounds.Inflate((needed - bounds.Width) / 2f, 0);
            bounds.Height += 22;
            Bounds = bounds;
            _button = new RectangleF(bounds.X + 2, bounds.Bottom - 20, bounds.Width - 4, 18);
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects)
                return;
            var box = GH_Convert.ToRectangle(_button);
            using (var capsule = GH_Capsule.CreateTextCapsule(box, box, GH_Palette.Normal, Component.ButtonText, GH_FontServer.StandardAdjusted, 2, 0))
                capsule.Render(graphics, Selected, Owner.Locked, false);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == MouseButtons.Left && _button.Contains(e.CanvasLocation) && sender.Viewport.Zoom >= 0.5f)
            {
                Component.ClickSoon();
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseDoubleClick(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            if (e.Button == MouseButtons.Left && ContentBox.Contains(e.CanvasLocation))
            {
                Component.ClickSoon();
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDoubleClick(sender, e);
        }
    }
}
