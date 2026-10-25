using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

namespace MiniVivisection.GH
{
    /// <summary>
    /// V Text's Font input: a text parameter whose "Set one Text" (its right-click menu) picks
    /// from the installed fonts -- a list drawn in each font, filtered as you type -- instead of
    /// a text box.
    /// </summary>
    public sealed class FontNameParameter : Param_String
    {
        public override Guid ComponentGuid => new Guid("94ce646c-064c-449f-b41c-89e87e6038b6");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override GH_GetterResult Prompt_Singular(ref GH_String value)
        {
            var name = FontPicker.Pick(value?.Value);
            if (name == null)
                return GH_GetterResult.cancel;
            value = new GH_String(name);
            return GH_GetterResult.success;
        }
    }

    /// <summary>A font, picked from the installed ones: a filter box over a list drawn in each font.</summary>
    internal static class FontPicker
    {
        private static string[] s_names;

        public static string[] Names => s_names ?? (s_names = new InstalledFontCollection().Families.Select(f => f.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());

        /// <summary>The font picked; null: cancelled.</summary>
        public static string Pick(string current)
        {
            using (var form = new Form
            {
                Text = "Font",
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(340, 460),
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
            })
            {
                var filter = new TextBox { Dock = DockStyle.Top };
                var list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 26, IntegralHeight = false };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 36 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                form.Controls.Add(list);
                form.Controls.Add(filter);
                form.Controls.Add(buttons);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                var fonts = new Dictionary<string, Font>();
                list.DrawItem += (s, e) =>
                {
                    if (e.Index < 0)
                        return;
                    e.DrawBackground();
                    var name = (string)list.Items[e.Index];
                    if (!fonts.TryGetValue(name, out var font))
                    {
                        try
                        {
                            var family = new FontFamily(name);
                            var style = new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic }.FirstOrDefault(family.IsStyleAvailable);
                            font = new Font(family, 12f, style);
                        }
                        catch (Exception)
                        {
                            font = null;
                        }
                        fonts[name] = font;
                    }
                    TextRenderer.DrawText(e.Graphics, name, font ?? list.Font, e.Bounds, e.ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    e.DrawFocusRectangle();
                };
                void Fill()
                {
                    var typed = filter.Text.Trim();
                    var selected = list.SelectedItem as string ?? current;
                    list.BeginUpdate();
                    list.Items.Clear();
                    foreach (var name in Names.Where(n => typed.Length == 0 || n.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0))
                        list.Items.Add(name);
                    list.EndUpdate();
                    var at = selected == null ? -1 : list.Items.IndexOf(selected);
                    if (at < 0 && list.Items.Count > 0 && typed.Length > 0)
                        at = 0;
                    list.SelectedIndex = at;
                }
                filter.TextChanged += (s, e) => Fill();
                filter.KeyDown += (s, e) =>
                {
                    // Up and down move in the list while typing
                    if ((e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) && list.Items.Count > 0)
                    {
                        list.SelectedIndex = Math.Max(0, Math.Min(list.Items.Count - 1, list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1)));
                        e.Handled = true;
                    }
                };
                list.DoubleClick += (s, e) =>
                {
                    if (list.SelectedItem != null)
                        form.DialogResult = DialogResult.OK;
                };
                Fill();
                form.Shown += (s, e) => filter.Focus();
                try
                {
                    var owner = Instances.DocumentEditor;
                    var result = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
                    return result == DialogResult.OK ? list.SelectedItem as string : null;
                }
                finally
                {
                    foreach (var font in fonts.Values)
                        font?.Dispose();
                }
            }
        }
    }
}
