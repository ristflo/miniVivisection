using System;
using Eto.Drawing;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// A two-state button whose state can't be mistaken: Eto's ToggleButton (and a
    /// CheckBox in the dark editor) differ only by a faint frame. Two looks:
    /// - with a label (<see cref="ShowState"/>): the icon, then a pill reading ON
    ///   (filled green, white text) or OFF (gray outline, gray text); the icon is in
    ///   color when on and grayed out when off;
    /// - icon only: one icon for on, another for off (the V Watch tab's Show eye:
    ///   open and blue, or closed and gray).
    /// Click or Space toggles. The tooltip names the state.
    /// </summary>
    internal sealed class StateToggle : Drawable
    {
        private static readonly Color OnColor = Color.FromArgb(46, 160, 67);
        private static readonly Color OffColor = Color.FromArgb(140, 140, 140);
        // A WPF button's own face colors (Aero 2), for ButtonLook: gray, light blue under
        // the mouse; no frame.
        private static readonly Color ButtonFace = Color.FromArgb(221, 221, 221);
        private static readonly Color ButtonHoverFace = Color.FromArgb(190, 230, 253);
        private static readonly Color OffOnFace = Color.FromArgb(105, 105, 105); // OFF, readable on the gray face

        private readonly Icon _onIcon;
        private readonly Icon _offIcon;
        private readonly string _name;
        private readonly string _description;
        private bool _checked;
        private bool _hover;

        /// <param name="name">What it switches, for the tooltip ("Auto edit").</param>
        /// <param name="offIcon">Null: <paramref name="onIcon"/> grayed out.</param>
        public StateToggle(Action<Graphics> onIcon, Action<Graphics> offIcon, string name, string description, bool showState)
        {
            _onIcon = DrawnIcons.Toolbar(onIcon);
            _offIcon = offIcon != null ? DrawnIcons.Toolbar(offIcon) : DrawnIcons.Draw(onIcon, new Size(16, 16), dimmed: true);
            _name = name;
            _description = description;
            ShowState = showState;
            CanFocus = showState;
            Size = showState ? new Size(54, 22) : new Size(20, 18);
            UpdateToolTip();
        }

        public bool ShowState { get; }

        /// <summary>A word left of the pill instead of the icon ("AI", "Debug").</summary>
        public string Caption
        {
            get => _caption;
            set
            {
                _caption = value;
                if (ShowState && !string.IsNullOrEmpty(value))
                    Size = new Size((int)Math.Ceiling(CaptionFont.MeasureString(value).Width) + 6 + 34, Size.Height);
                Invalidate();
            }
        }

        private string _caption;
        private static readonly Font CaptionFont = new Font(SystemFont.Bold, 8);
        private static readonly Color CaptionColor = Color.FromArgb(150, 150, 150);

        /// <summary>Drawn like the buttons next to it (a WPF button's gray face and
        /// frame), as in the V Watch tab's toolbar.</summary>
        public bool ButtonLook
        {
            get => _buttonLook;
            set
            {
                _buttonLook = value;
                Invalidate();
            }
        }

        private bool _buttonLook;

        /// <summary>The user toggled it (not raised when <see cref="Checked"/> is set).</summary>
        public event EventHandler<EventArgs> Toggled;

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value)
                    return;
                _checked = value;
                UpdateToolTip();
                Invalidate();
            }
        }

        private void UpdateToolTip()
        {
            ToolTip = string.Format("{0}: {1} -- click to turn it {2}.{3}", _name, _checked ? "ON" : "OFF", _checked ? "off" : "on",
                string.IsNullOrEmpty(_description) ? "" : "\n" + _description);
        }

        private void Toggle()
        {
            Checked = !Checked;
            Toggled?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Buttons == MouseButtons.Primary)
            {
                e.Handled = true;
                Toggle();
            }
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyData == Keys.Space || e.KeyData == Keys.Enter)
            {
                e.Handled = true;
                Toggle();
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics, Size);

        /// <summary>The look, on any surface (also rendered to images by the checks).</summary>
        internal void Render(Graphics g, Size size)
        {
            g.AntiAlias = true;
            var offColor = OffColor;
            if (_buttonLook)
            {
                g.FillRectangle(_hover ? ButtonHoverFace : ButtonFace, 0, 0, size.Width, size.Height);
                offColor = OffOnFace;
            }
            else if (_hover)
                g.FillPath(Color.FromArgb(128, 128, 128, 48), GraphicsPath.GetRoundRect(new RectangleF(0, 0, size.Width, size.Height), 3));
            var icon = _checked ? _onIcon : _offIcon;
            if (!ShowState)
            {
                g.DrawImage(icon, (size.Width - 16) / 2f, (size.Height - 16) / 2f, 16, 16);
                return;
            }
            var pillLeft = 22f;
            if (!string.IsNullOrEmpty(_caption))
            {
                var measuredCaption = g.MeasureString(CaptionFont, _caption);
                g.DrawText(CaptionFont, CaptionColor, 3, (size.Height - measuredCaption.Height) / 2, _caption);
                pillLeft = measuredCaption.Width + 6;
            }
            else
                g.DrawImage(icon, 3, (size.Height - 16) / 2f, 16, 16);
            if (_buttonLook && !_checked && string.IsNullOrEmpty(_caption))
                g.DrawImage(icon, 3, (size.Height - 16) / 2f, 16, 16); // twice: the faint gray icon is lost on the gray face
            var pill = new RectangleF(pillLeft, (size.Height - 15) / 2f, Math.Min(32, size.Width - pillLeft - 2), 15);
            var path = GraphicsPath.GetRoundRect(pill, 7.5f);
            var font = new Font(SystemFont.Bold, 7);
            var text = _checked ? "ON" : "OFF";
            if (_checked)
                g.FillPath(OnColor, path);
            else
                g.DrawPath(new Pen(offColor, 1.2f), path);
            var measured = g.MeasureString(font, text);
            g.DrawText(font, _checked ? Colors.White : offColor, pill.X + (pill.Width - measured.Width) / 2, pill.Y + (pill.Height - measured.Height) / 2, text);
        }
    }
}
