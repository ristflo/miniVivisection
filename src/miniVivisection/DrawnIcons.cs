using System;
using Eto.Drawing;

namespace MiniVivisection
{
    /// <summary>
    /// The plug-in's icons, drawn: filled shapes in color on a 20 x 20 grid, scaled to
    /// the size asked for, for 100 % and 200 % displays -- the left panel's page icons
    /// (BrowserPages) and the V Watch tab's toolbar.
    /// </summary>
    internal static class DrawnIcons
    {
        /// <param name="dimmed">Gray and faint: a two-state button's off state (StateToggle).</param>
        public static Icon Draw(Action<Graphics> draw, Size size, bool dimmed = false)
        {
            IconFrame Frame(float scale)
            {
                var bitmap = new Bitmap((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale), PixelFormat.Format32bppRgba);
                using (var graphics = new Graphics(bitmap))
                {
                    graphics.AntiAlias = true;
                    graphics.ScaleTransform(bitmap.Width / 20f, bitmap.Height / 20f);
                    draw(graphics);
                }
                if (dimmed)
                {
                    for (var y = 0; y < bitmap.Height; y++)
                    {
                        for (var x = 0; x < bitmap.Width; x++)
                        {
                            var c = bitmap.GetPixel(x, y);
                            if (c.A <= 0)
                                continue;
                            var l = 0.3f * c.R + 0.59f * c.G + 0.11f * c.B;
                            bitmap.SetPixel(x, y, new Color(l, l, l, c.A * 0.55f));
                        }
                    }
                }
                return new IconFrame(scale, bitmap);
            }
            return new Icon(Frame(1f), Frame(2f));
        }

        public static Icon Toolbar(Action<Graphics> draw) => Draw(draw, new Size(16, 16));

        private static readonly Color Green = Color.FromArgb(46, 160, 67);
        private static readonly Color Red = Color.FromArgb(207, 34, 46);
        private static readonly Color Brown = Color.FromArgb(176, 112, 48);
        private static readonly Color Blue = Color.FromArgb(47, 128, 237);
        private static readonly Color Amber = Color.FromArgb(230, 160, 0);
        private static readonly Color Slate = Color.FromArgb(120, 130, 145);

        public static void Add(Graphics g)
        {
            g.FillEllipse(Green, 1, 1, 18, 18);
            var pen = new Pen(Colors.White, 2.6f);
            g.DrawLine(pen, 10, 5, 10, 15);
            g.DrawLine(pen, 5, 10, 15, 10);
        }

        public static void Remove(Graphics g)
        {
            g.FillEllipse(Red, 1, 1, 18, 18);
            g.DrawLine(new Pen(Colors.White, 2.6f), 5, 10, 15, 10);
        }

        public static void Bake(Graphics g)
        {
            g.FillPath(Brown, GraphicsPath.GetRoundRect(new RectangleF(2, 11, 16, 7), 1.5f));
            var pen = new Pen(Brown, 2.4f);
            g.DrawLine(pen, 10, 1.5f, 10, 9);
            g.FillPolygon(Brown, new PointF(5.5f, 6.5f), new PointF(14.5f, 6.5f), new PointF(10, 11));
        }

        public static void Edit(Graphics g)
        {
            g.FillPolygon(Blue, new PointF(13, 2), new PointF(18, 7), new PointF(7, 18), new PointF(2, 13));
            g.FillPolygon(Amber, new PointF(2, 13), new PointF(7, 18), new PointF(1, 19));
            g.DrawLine(new Pen(Colors.White, 1.2f), 11.5f, 3.5f, 16.5f, 8.5f);
        }

        public static void AutoEdit(Graphics g)
        {
            Edit(g);
            g.FillEllipse(Amber, 10, 10, 9.5f, 9.5f);
            var pen = new Pen(Colors.White, 1.6f);
            g.DrawLine(pen, 12.2f, 18, 14.75f, 11.5f); // an "A"
            g.DrawLine(pen, 14.75f, 11.5f, 17.3f, 18);
            g.DrawLine(pen, 13.1f, 15.6f, 16.4f, 15.6f);
        }

        /// <summary>Show, on: an open eye.</summary>
        public static void EyeOpen(Graphics g)
        {
            g.FillEllipse(Blue, 1, 4.5f, 18, 11);
            g.FillEllipse(Colors.White, 5.5f, 6, 9, 8);
            g.FillEllipse(Blue, 7.5f, 7.5f, 5, 5);
        }

        /// <summary>Show, off: a closed eye, gray.</summary>
        public static void EyeClosed(Graphics g)
        {
            var pen = new Pen(Slate, 1.8f);
            g.DrawArc(pen, 2, 2, 16, 10, 15, 150);
            g.DrawLine(pen, 5, 10.5f, 3.5f, 14);
            g.DrawLine(pen, 10, 12, 10, 16);
            g.DrawLine(pen, 15, 10.5f, 16.5f, 14);
        }

        public static void Clear(Graphics g)
        {
            g.FillPath(Slate, GraphicsPath.GetRoundRect(new RectangleF(4, 6, 12, 13), 1.5f));
            g.FillRectangle(Slate, 2, 3, 16, 2);
            g.FillRectangle(Slate, 8, 1, 4, 2);
            var pen = new Pen(Colors.White, 1.2f);
            foreach (var x in new[] { 7.5f, 10f, 12.5f })
                g.DrawLine(pen, x, 9, x, 16);
        }
    }
}
