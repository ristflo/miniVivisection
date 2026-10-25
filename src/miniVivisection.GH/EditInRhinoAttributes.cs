using System.Drawing;
using System.Windows.Forms;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace MiniVivisection.GH
{
    /// <summary>V Edit's look on the canvas: the component, and under it two buttons, one above the other -- Reset
    /// sel., orange while an edited edit object is selected in Rhino (a click drops those
    /// edits), and Reset all, orange while there are edits (a click drops them all); gray
    /// else.</summary>
    internal sealed class EditInRhinoAttributes : GH_ComponentAttributes
    {
        private RectangleF _button; // Reset sel.
        private RectangleF _all;    // Reset all

        public EditInRhinoAttributes(EditInRhinoComponent owner)
            : base(owner)
        {
        }

        protected override void Layout()
        {
            base.Layout();
            var bounds = Bounds;
            bounds.Height += 42; // two buttons, one above the other
            Bounds = bounds;
            _button = new RectangleF(bounds.X + 2, bounds.Bottom - 40, bounds.Width - 4, 18);
            _all = new RectangleF(bounds.X + 2, bounds.Bottom - 20, bounds.Width - 4, 18);
        }

        protected override void Render(GH_Canvas canvas, Graphics graphics, GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects)
                return;
            var owner = (EditInRhinoComponent)Owner;
            Button(graphics, _button, "Reset sel.", owner.HasSelectedEdit);
            Button(graphics, _all, "Reset all", owner.HasEdits);
        }

        private void Button(Graphics graphics, RectangleF area, string text, bool active)
        {
            var box = GH_Convert.ToRectangle(area);
            using (var capsule = GH_Capsule.CreateTextCapsule(box, box, active ? GH_Palette.Warning : GH_Palette.Grey, text, 2, 0))
                capsule.Render(graphics, Selected, Owner.Locked, false);
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas sender, GH_CanvasMouseEvent e)
        {
            var owner = (EditInRhinoComponent)Owner;
            if (e.Button == MouseButtons.Left && _button.Contains(e.CanvasLocation) && owner.HasSelectedEdit)
            {
                owner.ResetSelected();
                return GH_ObjectResponse.Handled;
            }
            if (e.Button == MouseButtons.Left && _all.Contains(e.CanvasLocation) && owner.HasEdits)
            {
                owner.ResetAll();
                return GH_ObjectResponse.Handled;
            }
            return base.RespondToMouseDown(sender, e);
        }
    }
}
