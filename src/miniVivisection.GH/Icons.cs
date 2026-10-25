using System.Drawing;
using System.Drawing.Drawing2D;

namespace MiniVivisection.GH
{
    /// <summary>The components' icons, drawn. V Edit: a curve with an orange handle being
    /// dragged. V Text: a heading and lines of text, its anchor an orange dot.</summary>
    internal static class Icons
    {
        private static Bitmap s_editInRhino;
        private static Bitmap s_text;
        private static Bitmap s_protect;

        public static Bitmap EditInRhino => s_editInRhino ?? (s_editInRhino = DrawEditInRhino());

        public static Bitmap Text => s_text ?? (s_text = DrawText());

        /// <summary>V Protect: a shield with a check mark.</summary>
        public static Bitmap Protect => s_protect ?? (s_protect = DrawProtect());

        private static Bitmap DrawProtect()
        {
            var bitmap = new Bitmap(24, 24);
            using (var g = Graphics.FromImage(bitmap))
            using (var shield = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                shield.AddLine(12, 2, 21, 5);
                shield.AddBezier(21, 5, 21, 14, 18, 19, 12, 22);
                shield.AddBezier(12, 22, 6, 19, 3, 14, 3, 5);
                shield.CloseFigure();
                using (var fill = new SolidBrush(Color.FromArgb(40, 120, 200)))
                    g.FillPath(fill, shield);
                using (var outline = new Pen(Color.FromArgb(20, 60, 110), 1f))
                    g.DrawPath(outline, shield);
                using (var check = new Pen(Color.White, 2.2f))
                    g.DrawLines(check, new[] { new PointF(7.5f, 12), new PointF(11, 15.5f), new PointF(16.5f, 8.5f) });
            }
            return bitmap;
        }

        private static Bitmap DrawText()
        {
            var bitmap = new Bitmap(24, 24);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var heading = new Pen(Color.FromArgb(40, 40, 40), 3f))
                    g.DrawLine(heading, 5, 5, 17, 5);
                using (var rule = new Pen(Color.FromArgb(150, 150, 150), 1f))
                    g.DrawLine(rule, 4, 8.5f, 21, 8.5f);
                using (var lines = new Pen(Color.FromArgb(40, 120, 200), 2f))
                {
                    g.DrawLine(lines, 4, 12, 21, 12);
                    g.DrawLine(lines, 4, 16, 19, 16);
                    g.DrawLine(lines, 4, 20, 14, 20);
                }
                using (var anchor = new SolidBrush(Color.FromArgb(245, 140, 0)))
                    g.FillEllipse(anchor, 0, 0, 6, 6);
                using (var outline = new Pen(Color.FromArgb(120, 70, 0), 1f))
                    g.DrawEllipse(outline, 0, 0, 6, 6);
            }
            return bitmap;
        }

        private static Bitmap DrawEditInRhino()
        {
            var bitmap = new Bitmap(24, 24);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var curve = new Pen(Color.FromArgb(40, 120, 200), 2.2f))
                    g.DrawBezier(curve, 2, 19, 7, 3, 13, 21, 22, 6);
                using (var dashed = new Pen(Color.FromArgb(150, 150, 150), 1.2f) { DashStyle = DashStyle.Dash })
                    g.DrawLine(dashed, 12, 12, 16, 5);
                using (var handle = new SolidBrush(Color.FromArgb(245, 140, 0)))
                    g.FillEllipse(handle, 13, 2, 7, 7);
                using (var outline = new Pen(Color.FromArgb(120, 70, 0), 1f))
                    g.DrawEllipse(outline, 13, 2, 7, 7);
            }
            return bitmap;
        }
    }
}
