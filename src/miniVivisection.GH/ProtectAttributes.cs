using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace MiniVivisection.GH
{
    /// <summary>
    /// V°Protect on the canvas. Setup: the component (no inputs, no outputs) and a Protect button under
    /// it. Protect: drawn as a Grasshopper Button -- in front the state, "original" or "modified", on the
    /// component itself colored green or red (another state, such as no archive, gray); then a gray
    /// button "Revert". The whole of it is the component for clicks and the menu (right-click anywhere on
    /// it); a left click on Revert reverts when it is modified (asked first). The state is in its message
    /// balloon and its tooltip.
    /// </summary>
    internal sealed class ProtectAttributes : GH_ComponentAttributes
    {
        private static readonly Color Original = Color.FromArgb(46, 160, 67);
        private static readonly Color Modified = Color.FromArgb(205, 40, 40);
        private static readonly Color Unknown = Color.FromArgb(150, 150, 150);
        private const string ButtonText = "Revert";
        private const string SetupText = "Protect";
        private RectangleF _button;
        private RectangleF _name;

        public ProtectAttributes(ProtectComponent owner)
            : base(owner)
        {
        }

        private ProtectComponent Component => (ProtectComponent)Owner;

        protected override void Layout()
        {
            base.Layout();
            if (Component.IsProtected)
            {
                // A Button's layout: the state in front (as wide as its words, at least as "modified"), then the button
                Pivot = GH_Convert.ToPoint(Pivot);
                var words = new[] { State(Component).Text, "modified", "original" };
                var width = words.Max(w => GH_FontServer.StringWidth(w, GH_FontServer.StandardAdjusted)) + 16;
                _name = new RectangleF(Pivot, new SizeF(width, 22));
                _button = new RectangleF(_name.Right, _name.Top, GH_FontServer.StringWidth(ButtonText, GH_FontServer.StandardAdjusted) + 20, _name.Height);
                Bounds = RectangleF.Union(_name, _button);
                _name.Inflate(-2, -2);
                _button.Inflate(-2, -2);
                return;
            }
            // Setup: the button under the component, its words all in sight -- the component (no
            // inputs, no outputs: narrow) as wide as they need, its icon in the middle still
            var bounds = Bounds;
            var needed = Math.Max(GH_FontServer.StringWidth(SetupText, GH_FontServer.Standard), GH_FontServer.StringWidth(SetupText, GH_FontServer.StandardAdjusted)) + 20;
            if (bounds.Width < needed)
                bounds.Inflate((needed - bounds.Width) / 2f, 0);
            bounds.Height += 22;
            Bounds = bounds;
            _name = RectangleF.Empty;
            _button = new RectangleF(bounds.X + 2, bounds.Bottom - 20, bounds.Width - 4, 18);
        }

        // Protect mode: the whole Button is the component -- picked and right-clicked anywhere on it
        public override bool IsPickRegion(PointF point) => Component.IsProtected ? Bounds.Contains(point) : base.IsPickRegion(point);

        public override bool IsMenuRegion(PointF point) => Component.IsProtected ? Bounds.Contains(point) : base.IsMenuRegion(point);

        public override bool IsTooltipRegion(PointF point) => Component.IsProtected ? Bounds.Contains(point) : base.IsTooltipRegion(point);

        /// <summary>"original", "modified", or another state's words; its color.</summary>
        internal static (string Text, Color Color) State(ProtectComponent owner)
        {
            if (owner.DiskState == "changed" || owner.DiskState == "missing" || (owner.OpenModified && owner.DiskState == "original"))
                return ("modified", Modified);
            if (owner.DiskState == "original")
                return ("original", Original);
            return (owner.DiskState, Unknown);
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            var owner = Component;
            if (!owner.IsProtected)
            {
                base.Render(canvas, graphics, channel);
                if (channel == GH_CanvasChannel.Objects)
                {
                    var box = GH_Convert.ToRectangle(_button);
                    using (var capsule = GH_Capsule.CreateTextCapsule(box, box, owner.CanKeep ? GH_Palette.Normal : GH_Palette.Grey, SetupText, 2, 0))
                        capsule.Render(graphics, Selected, owner.Locked, false);
                }
                return;
            }
            if (channel != GH_CanvasChannel.Objects)
                return;
            var (text, color) = State(owner);
            // The component in the state's color, the state's words in front
            var zoom = canvas.Viewport.Zoom;
            var body = Selected ? Color.FromArgb(Math.Min(255, color.R + 40), Math.Min(255, color.G + 40), Math.Min(255, color.B + 40)) : color;
            var fill = new GH_PaletteStyle(body, Color.FromArgb(color.R / 2, color.G / 2, color.B / 2), Color.White);
            using (var capsule = GH_Capsule.CreateCapsule(Bounds, GH_Palette.Normal))
            {
                capsule.RenderEngine.RenderBackground(graphics, zoom, fill);
                capsule.RenderEngine.RenderHighlight(graphics);
                capsule.RenderEngine.RenderOutlines(graphics, zoom, fill);
            }
            canvas.SetSmartTextRenderingHint();
            using (var brush = new SolidBrush(Color.White))
                graphics.DrawString(text, GH_FontServer.StandardAdjusted, brush, _name, GH_TextRenderingConstants.CenterCenter);
            // The button: gray, "Revert"
            var button = GH_Convert.ToRectangle(_button);
            using (var capsule = GH_Capsule.CreateTextCapsule(button, button, GH_Palette.Grey, ButtonText, GH_FontServer.StandardAdjusted, 1, 0))
                capsule.Render(graphics, false, owner.Locked, false);
        }

        public override void SetupTooltip(PointF point, GH_TooltipDisplayEventArgs e)
        {
            var owner = Component;
            base.SetupTooltip(point, e);
            e.Icon = Icons.Protect;
            if (owner.IsProtected)
            {
                var (text, _) = State(owner);
                e.Title = GhNames.Of("Protect") + ": " + text;
                e.Text = "The definition on disk: " + owner.DiskState + "; the open definition: " + (owner.OpenModified ? "changed, not saved" : "unchanged") + ".";
                e.Description = (text == "modified" ? "Click Revert: the original back on disk where it changed, the definition opened again (asked first).\n" : "") +
                                "Right-click: Back to setup.\nOriginals: " + owner.Where;
                return;
            }
            e.Title = GhNames.Of("Protect") + " (setup)";
            e.Text = "Keeps: " + (owner.Files.Count == 0 ? "nothing yet (save the definition)" : string.Join(", ", owner.Files.Select(System.IO.Path.GetFileName))) +
                     "\nOriginals: " + owner.Where;
            e.Description = owner.Log.Count == 0 ? "Right-click to set the files and where their originals go." : string.Join("\n", owner.Log);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var owner = Component;
            if (e.Button == MouseButtons.Left && _button.Contains(e.CanvasLocation))
            {
                if (!owner.IsProtected && owner.CanKeep)
                {
                    owner.Protect();
                    ExpireLayout();
                    sender.Invalidate();
                    return GH_ObjectResponse.Handled;
                }
                if (owner.IsProtected)
                {
                    if (owner.NeedsRevert && sender.Viewport.Zoom >= 0.5f)
                        owner.Revert();
                    return GH_ObjectResponse.Handled;
                }
            }
            return base.RespondToMouseDown(sender, e);
        }
    }
}
