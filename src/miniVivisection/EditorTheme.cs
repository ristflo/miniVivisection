using System;
using System.Reflection;
using Eto.Drawing;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>The Script Editor's terminal colors (its theme, read by reflection), for
    /// the plug-in's tabs.</summary>
    internal static class EditorTheme
    {
        private static Color? Background(object editor)
        {
            try
            {
                var registry = editor.GetType().Assembly.GetType("RhinoCodeEditor.Themes.ThemeRegistry");
                var theme = registry?.GetProperty("ActiveTheme", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                return theme?.GetType().GetProperty("TerminalBackgroundColor")?.GetValue(theme) is Color background && background.A > 0 ? background : (Color?)null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Gives <paramref name="controls"/> the editor's terminal background and a
        /// readable text color; leaves the defaults when the theme can't be read.</summary>
        public static void Apply(object editor, params Control[] controls)
        {
            if (!(Background(editor) is Color background))
                return; // default colors
            var text = background.ToHSL().L < 0.5f ? Colors.WhiteSmoke : Colors.Black;
            foreach (var control in controls)
            {
                control.BackgroundColor = background;
                if (control is TextControl textControl) // labels, text boxes, text areas
                    textControl.TextColor = text;
            }
        }
    }
}
