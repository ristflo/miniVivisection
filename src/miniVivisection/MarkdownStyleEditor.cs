using System;
using System.IO;
using System.Linq;
using System.Text;
using Eto.Drawing;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// A small editor for Markdown styles (MarkdownStyle's CSS): a text box, a line saying what
    /// would be left out (checked as you type), and buttons. Two ways:
    /// - a style file (V Settings' Edit beside "Markdown style for ..."): Save, Save as, Default,
    ///   Close. A default file is read-only -- Save as makes one's own, which the setting then
    ///   points to; never saved over a default (anything in styles/ next to the plug-in);
    /// - a Rhino file's style (MarkdownText's dialog): Load (a file), Save as (a file), Default,
    ///   OK, Cancel -- OK gives the CSS back, and MarkdownText draws all the file's texts with it.
    /// </summary>
    internal static class MarkdownStyleEditor
    {
        private static readonly Color Gray = Color.FromArgb(120, 120, 120);
        private static readonly Color Warning = Color.FromArgb(170, 90, 0);

        /// <summary>The style file <paramref name="path"/> (null or empty: the default, read-only)
        /// edited; the path it was saved as last (for the setting), or null.</summary>
        public static string EditFile(Window owner, MarkdownStyle.Target target, string path)
        {
            path = string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            string css;
            try
            {
                css = path != null && File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : MarkdownStyle.DefaultCss(target);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Can't read " + path + ": " + ex.Message, "Markdown style", MessageBoxType.Error);
                return null;
            }
            if (path != null && !File.Exists(path))
                path = null; // missing: the default shown, Save as makes the file
            string savedAs = null;
            var (dialog, area, status) = Make(target, css);
            var saved = css;
            var save = new Button { Text = "Save" };
            var saveAs = new Button { Text = "Save as..." };
            var reset = new Button { Text = "Default", ToolTip = "The text of miniVivisection's default style (then Save as)" };
            var close = new Button { Text = "Close" };

            void Show()
            {
                var readOnly = path == null || MarkdownStyleFiles.IsDefaultFile(path);
                dialog.Title = "Markdown style for " + target + " -- " + (path == null ? "miniVivisection's default (read-only)" : readOnly ? Path.GetFileName(path) + " (a default: read-only)" : path);
                save.Enabled = !readOnly;
                save.ToolTip = readOnly ? "A default style is never changed: Save as makes your own" : "Save " + path;
            }

            bool Write(string to, string text)
            {
                try
                {
                    File.WriteAllText(to, text.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, "Can't save " + to + ": " + ex.Message, "Markdown style", MessageBoxType.Error);
                    return false;
                }
            }

            bool DoSave()
            {
                if (path == null || MarkdownStyleFiles.IsDefaultFile(path))
                    return DoSaveAs();
                var text = Text(area);
                if (!Write(path, text))
                    return false;
                saved = text;
                return true;
            }

            bool DoSaveAs()
            {
                var to = AskPath(dialog, target, path);
                if (to == null)
                    return false;
                var text = MarkdownStyle.AsOwn(Text(area));
                if (!Write(to, text))
                    return false;
                area.Text = text;
                path = savedAs = to;
                saved = text;
                Show();
                return true;
            }

            save.Click += (s, e) => DoSave();
            saveAs.Click += (s, e) => DoSaveAs();
            reset.Click += (s, e) => area.Text = MarkdownStyle.DefaultCss(target);
            close.Click += (s, e) => dialog.Close();
            dialog.Closing += (s, e) =>
            {
                if (Text(area) == saved)
                    return;
                var answer = MessageBox.Show(dialog, "Save the changes to the style?", "Markdown style", MessageBoxButtons.YesNoCancel, MessageBoxType.Question, MessageBoxDefaultButton.Yes);
                if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !DoSave()))
                    e.Cancel = true;
            };
            Lay(dialog, area, status, save, saveAs, reset, null, close);
            Show();
            dialog.ShowModal(owner);
            return savedAs;
        }

        /// <summary>A Rhino file's style <paramref name="css"/> (<paramref name="name"/>: where it
        /// came from) edited: the CSS and its source's name (a file loaded or saved as), or null: Cancel.</summary>
        public static (string Css, string Source)? EditText(Window owner, MarkdownStyle.Target target, string css, string name)
        {
            var (dialog, area, status) = Make(target, css);
            dialog.Title = "Markdown style of this Rhino file -- " + name;
            string source = null;
            var ok = false;
            var load = new Button { Text = "Load...", ToolTip = "A style file's text" };
            var saveAs = new Button { Text = "Save as...", ToolTip = "This text as a style file of your own" };
            var reset = new Button { Text = "Default", ToolTip = "The text of miniVivisection's default style" };
            var okButton = new Button { Text = "OK", ToolTip = "This file's Markdown texts drawn with it" };
            var cancel = new Button { Text = "Cancel" };
            load.Click += (s, e) =>
            {
                var path = AskOpen(dialog, target);
                if (path == null)
                    return;
                try
                {
                    area.Text = File.ReadAllText(path).Replace("\r\n", "\n");
                    source = Path.GetFileName(path);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, "Can't read " + path + ": " + ex.Message, "Markdown style", MessageBoxType.Error);
                }
            };
            saveAs.Click += (s, e) =>
            {
                var to = AskPath(dialog, target, null);
                if (to == null)
                    return;
                try
                {
                    var text = MarkdownStyle.AsOwn(Text(area));
                    File.WriteAllText(to, text.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
                    area.Text = text;
                    source = Path.GetFileName(to);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, "Can't save " + to + ": " + ex.Message, "Markdown style", MessageBoxType.Error);
                }
            };
            reset.Click += (s, e) =>
            {
                area.Text = MarkdownStyle.DefaultCss(target);
                source = "default";
            };
            okButton.Click += (s, e) =>
            {
                ok = true;
                dialog.Close();
            };
            cancel.Click += (s, e) => dialog.Close();
            dialog.AbortButton = cancel;
            Lay(dialog, area, status, load, saveAs, reset, null, okButton, cancel);
            dialog.ShowModal(owner);
            return ok ? (Text(area), source) : ((string, string)?)null;
        }

        /// <summary>A style file to open (the folder of the defaults first); null: none.</summary>
        public static string AskOpen(Window owner, MarkdownStyle.Target target)
        {
            var dialog = new OpenFileDialog { Title = "Markdown style (CSS)" };
            dialog.Filters.Add(new FileFilter("CSS", ".css"));
            dialog.Filters.Add(new FileFilter("All files", ".*"));
            return dialog.ShowDialog(owner) == DialogResult.Ok ? dialog.FileName : null;
        }

        /// <summary><paramref name="css"/> saved as a style file of one's own (asked where; a
        /// default's stamp taken off): its path, or null.</summary>
        public static string SaveAs(Window owner, MarkdownStyle.Target target, string css)
        {
            var to = AskPath(owner, target, null);
            if (to == null)
                return null;
            try
            {
                File.WriteAllText(to, MarkdownStyle.AsOwn(css).Replace("\n", Environment.NewLine), new UTF8Encoding(false));
                return to;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Can't save " + to + ": " + ex.Message, "Markdown style", MessageBoxType.Error);
                return null;
            }
        }

        // Where to save a style of one's own: never a default's file (asked again)
        private static string AskPath(Window owner, MarkdownStyle.Target target, string current)
        {
            while (true)
            {
                var dialog = new SaveFileDialog { Title = "Save the Markdown style as", FileName = current != null && !MarkdownStyleFiles.IsDefaultFile(current) ? current : "my_" + MarkdownStyle.DefaultFileName(target) };
                dialog.Filters.Add(new FileFilter("CSS", ".css"));
                if (dialog.ShowDialog(owner) != DialogResult.Ok)
                    return null;
                var path = dialog.FileName;
                if (string.IsNullOrEmpty(Path.GetExtension(path)))
                    path += ".css";
                if (!MarkdownStyleFiles.IsDefaultFile(path))
                    return path;
                MessageBox.Show(owner, "The default styles (in " + MarkdownStyleFiles.Folder + ") are never changed: save your own somewhere else.", "Markdown style", MessageBoxType.Warning);
            }
        }

        private static string Text(TextArea area) => (area.Text ?? "").Replace("\r\n", "\n");

        // The dialog, its text box and its status line (what would be left out, checked as you type)
        private static (Dialog Dialog, TextArea Area, Label Status) Make(MarkdownStyle.Target target, string css)
        {
            var dialog = new Dialog { Resizable = true, Padding = 8, MinimumSize = new Size(480, 300) };
            var area = new TextArea
            {
                Font = new Font("Consolas", 10),
                Wrap = false,
                AcceptsTab = true,
                AcceptsReturn = true,
                Size = new Size(760, 560),
                Text = css ?? "",
            };
            var status = new Label { Wrap = WrapMode.Word };
            var timer = new UITimer { Interval = 0.4 };
            void Check()
            {
                var style = MarkdownStyle.Parse(Text(area));
                status.TextColor = style.Problems.Count == 0 ? Gray : Warning;
                status.Text = style.Problems.Count == 0
                    ? "Fine: every element and property known. " + (MarkdownStyle.IsPristineDefault(Text(area)) ? "(The default, unchanged.)" : "")
                    : "Left out: " + string.Join("; ", style.Problems.Take(4)) + (style.Problems.Count > 4 ? "; ... (" + style.Problems.Count + ")" : "");
            }
            timer.Elapsed += (s, e) =>
            {
                timer.Stop();
                Check();
            };
            area.TextChanged += (s, e) =>
            {
                timer.Stop();
                timer.Start();
            };
            dialog.Closed += (s, e) => timer.Stop();
            Check();
            return (dialog, area, status);
        }

        private static void Lay(Dialog dialog, TextArea area, Label status, params Button[] buttons)
        {
            var row = new TableRow();
            foreach (var button in buttons)
                row.Cells.Add(button == null ? new TableCell(null, true) : new TableCell(button));
            dialog.Content = new TableLayout
            {
                Spacing = new Size(6, 6),
                Rows =
                {
                    new TableRow(area) { ScaleHeight = true },
                    new TableRow(status),
                    new TableRow(new TableLayout(row) { Spacing = new Size(6, 0) }),
                },
            };
        }
    }
}
