using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MiniVivisection
{
    /// <summary>
    /// MarkdownText: Markdown as Rhino text (MarkdownRhinoText). On a Markdown text -- selected,
    /// picked, or double-clicked (MarkdownTextMouse) -- its Markdown opens in a dialog: the whole
    /// text selected in Rhino; a part picked alone (a click in the command, Ctrl+Shift+click
    /// before it, a double-click) puts the caret at its section and grays the rest (editable
    /// still). What was changed on the parts in Rhino is taken in first (Reconcile), and OK
    /// updates the same objects (Update). Enter instead: new text -- the Markdown, then where its
    /// top left goes. The dialog sets the width (mm), the base size (pt) and the base font, and
    /// the Rhino file's Markdown style (MarkdownStyleFiles: one per file) -- edited, loaded from a
    /// file, saved as one, or back to the default; OK then draws all the file's Markdown texts with it.
    /// -MarkdownText (scripted): File=path for the Markdown, Height and Width in model units.
    /// </summary>
    public class MarkdownTextCommand : Rhino.Commands.Command
    {
        public override string EnglishName => "MarkdownText";

        // New text: the base size (pt), the width (mm), the base font (null: the dimension style's)
        private static double s_points = 10;
        private static double s_millimeters = 160;
        private static string s_font;

        // A part double-clicked: edited by the next run (MarkdownTextMouse)
        private static Guid s_pending;

        private static readonly Color Gray = Color.FromArgb(150, 150, 150);

        /// <summary>Edits the Markdown text <paramref name="part"/> is part of, the caret at its
        /// section: the command, run for it.</summary>
        internal static void EditPart(Guid part)
        {
            s_pending = part;
            if (!RhinoApp.RunScript("_MarkdownText", false))
                s_pending = Guid.Empty; // didn't run (a command going on): not for a later MarkdownText
        }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // A part double-clicked; else what's selected -- one part alone: its section, more: the whole text
            RhinoObject chosen = null;
            var whole = false;
            if (s_pending != Guid.Empty)
            {
                chosen = doc.Objects.FindId(s_pending);
                s_pending = Guid.Empty;
            }
            if (chosen == null)
            {
                var selected = doc.Objects.GetSelectedObjects(false, false).Where(MarkdownRhinoText.IsPart).ToList();
                if (selected.Count > 0)
                {
                    chosen = selected[0];
                    whole = selected.Count > 1;
                }
            }
            if (chosen == null)
            {
                // Picked now: the part clicked (not its group), its section in the dialog
                var pick = new GetObject();
                pick.SetCommandPrompt("Select Markdown text to edit (the part you click: its section), or press Enter for new");
                pick.SetCustomGeometryFilter((obj, geometry, component) => MarkdownRhinoText.IsPart(obj));
                pick.AcceptNothing(true);
                pick.EnablePreSelect(false, true);
                var picked = pick.Get();
                if (picked == GetResult.Cancel)
                    return Result.Cancel;
                if (picked == GetResult.Object)
                    chosen = pick.Object(0).Object();
            }
            return chosen != null ? EditExisting(doc, mode, chosen, whole) : CreateNew(doc, mode);
        }

        private static Result EditExisting(RhinoDoc doc, RunMode mode, RhinoObject chosen, bool whole)
        {
            var text = MarkdownRhinoText.Find(doc, chosen);
            if (text == null)
                return Result.Failure;
            if (whole)
                text.Picked = null;
            // The whole text selected while its Markdown is edited
            foreach (var o in text.Parts)
                o.Select(true);
            doc.Views.Redraw();
            var (source, frame, notes, partStart, partEnd) = MarkdownRhinoText.Reconcile(doc, text);
            foreach (var note in notes)
                RhinoApp.WriteLine("MarkdownText: " + note);
            string edited;
            var newFrame = frame;
            StyleChoice style = null;
            if (mode == RunMode.Interactive)
            {
                var result = Edit(doc, source, frame, partStart, partEnd, notes);
                if (result == null)
                    return Result.Cancel;
                (edited, newFrame, style) = result.Value;
            }
            else
            {
                edited = ReadFile(source);
                if (edited == null)
                    return Result.Cancel;
            }
            // A new style: the file keeps it, and all its other Markdown texts are drawn with it
            var restyled = ApplyStyle(doc, style, chosen.Id);
            if (restyled < 0 && edited.TrimEnd() == text.Source.TrimEnd() && newFrame.SameAs(text.Frame, doc.ModelAbsoluteTolerance))
                return Result.Nothing;
            var before = new HashSet<Guid>(text.Parts.Select(o => o.Id));
            var ids = MarkdownRhinoText.Update(doc, text, edited, newFrame);
            doc.Objects.UnselectAll();
            doc.Objects.Select(ids);
            doc.Views.Redraw();
            var added = ids.Count(i => !before.Contains(i));
            var removed = before.Count(i => !ids.Contains(i));
            RhinoApp.WriteLine(string.Format("MarkdownText: edited in place -- {0} objects{1}{2}. Undo takes it back.{3}", ids.Count,
                added > 0 ? ", " + added + " new (the text needs more parts)" : "", removed > 0 ? ", " + removed + " removed" : "",
                restyled < 0 ? "" : " The file's Markdown style changed: " + restyled + " other Markdown text" + (restyled == 1 ? "" : "s") + " drawn again too."));
            return Result.Success;
        }

        /// <summary>The style chosen in the dialog: the Rhino file's CSS, changed there by Edit,
        /// Load or Default (applied at OK), and where it came from (a file's name, "default").</summary>
        internal sealed class StyleChoice
        {
            public string Css;
            public string Source;
            public bool Changed;
        }

        // The dialog's new style kept by the file, and its Markdown texts drawn with it (except
        // the one being edited: updated next). How many; -1: the style didn't change.
        private static int ApplyStyle(RhinoDoc doc, StyleChoice style, Guid except)
        {
            if (style == null || !style.Changed)
                return -1;
            if (style.Css == MarkdownStyleFiles.DocumentCss(doc))
            {
                MarkdownStyleFiles.Keep(doc, style.Css, style.Source); // the setting's style, now the file's own
                return -1;
            }
            MarkdownStyleFiles.Keep(doc, style.Css, style.Source);
            MarkdownStyleFiles.Report(style.Css, "of this file");
            return MarkdownRhinoText.Restyle(doc, except);
        }

        private static Result CreateNew(RhinoDoc doc, RunMode mode)
        {
            var frame = new MarkdownRhinoText.Frame
            {
                Height = s_points / PointsPerUnit(doc),
                Width = s_millimeters / MillimetersPerUnit(doc),
                Font = s_font,
            };
            string markdown;
            if (mode == RunMode.Interactive)
            {
                var result = Edit(doc, "# Title\n\nSome **bold** and *italic* text, a [link](rhinoview:Top).\n\n- one\n- two\n- [ ] a task\n", frame, -1, -1, null);
                if (result == null)
                    return Result.Cancel;
                StyleChoice style;
                (markdown, frame, style) = result.Value;
                var restyled = ApplyStyle(doc, style, Guid.Empty);
                if (restyled > 0)
                    RhinoApp.WriteLine("MarkdownText: the file's Markdown style changed -- {0} Markdown text{1} drawn again.", restyled, restyled == 1 ? "" : "s");
                s_points = frame.Height * PointsPerUnit(doc);
                s_millimeters = frame.Width * MillimetersPerUnit(doc);
                s_font = frame.Font;
            }
            else
            {
                markdown = ReadFile(null);
            }
            if (string.IsNullOrWhiteSpace(markdown))
                return Result.Cancel;
            var where = new GetPoint();
            where.SetCommandPrompt("Where the Markdown text starts (its top left)");
            var height = new OptionDouble(frame.Height, 1e-6, 1e6);
            var width = new OptionDouble(frame.Width, 0, 1e9);
            if (mode != RunMode.Interactive)
            {
                where.AddOptionDouble("Height", ref height, "a paragraph's text height, in model units");
                where.AddOptionDouble("Width", ref width, "the width text wraps in, in model units (0: no wrapping)");
            }
            while (true)
            {
                var result = where.Get();
                if (result == GetResult.Option)
                    continue;
                if (result != GetResult.Point)
                    return Result.Cancel;
                break;
            }
            if (mode != RunMode.Interactive)
            {
                frame.Height = height.CurrentValue;
                frame.Width = width.CurrentValue;
            }
            var plane = where.View()?.ActiveViewport.ConstructionPlane() ?? Rhino.Geometry.Plane.WorldXY;
            plane.Origin = where.Point();
            frame.Plane = plane;
            var ids = MarkdownRhinoText.Create(doc, markdown, frame, out _);
            doc.Views.Redraw();
            RhinoApp.WriteLine("MarkdownText: {0} objects, grouped -- double-click any of it (or select it and run MarkdownText) to edit the Markdown.", ids.Count);
            return Result.Success;
        }

        private static double PointsPerUnit(RhinoDoc doc) => RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Inches) * 72.0;

        private static double MillimetersPerUnit(RhinoDoc doc) => RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);

        // -MarkdownText: the Markdown from a file (Enter: keep what it was; Esc: nothing)
        private static string ReadFile(string current)
        {
            var path = "";
            var got = RhinoGet.GetString("Markdown file", current != null, ref path);
            if (got == Result.Cancel)
                return null;
            if (got != Result.Success || string.IsNullOrWhiteSpace(path))
                return current;
            path = path.Trim().Trim('"');
            if (!File.Exists(path))
            {
                RhinoApp.WriteLine("MarkdownText: no file {0}.", path);
                return current;
            }
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }

        /// <summary>The Markdown in a dialog, with the frame's width (mm), base size (pt) and font;
        /// [partStart, partEnd) the part edited (the rest gray, the caret at its start; -1: none);
        /// notes on top. Null: Cancel.</summary>
        private static (string Markdown, MarkdownRhinoText.Frame Frame, StyleChoice Style)? Edit(RhinoDoc doc, string markdown, MarkdownRhinoText.Frame frame, int partStart, int partEnd, List<string> notes)
        {
            markdown = (markdown ?? "").Replace("\r\n", "\n");
            var (dialog, area, width, size, fonts, styleFont, style) = EditorDialog(doc, markdown, frame, partStart, partEnd, notes);
            var mm = MillimetersPerUnit(doc);
            var pt = PointsPerUnit(doc);
            if (!dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow))
                return null;
            var text = area.Text.Replace("\r\n", "\n");
            text = text.TrimEnd('\n') + (markdown.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "");
            var font = (fonts.Text ?? "").Trim();
            var result = frame.Copy();
            result.Width = width.Value / mm;
            result.Height = size.Value / pt;
            result.Font = font.Length == 0 || (frame.Font == null && string.Equals(font, styleFont, StringComparison.OrdinalIgnoreCase)) ? frame.Font : font;
            // Unchanged numbers keep the frame's own (no rounding through mm and pt)
            if (Math.Abs(width.Value - Math.Round(frame.Width * mm, 3)) < 1e-9)
                result.Width = frame.Width;
            if (Math.Abs(size.Value - Math.Round(frame.Height * pt, 3)) < 1e-9)
                result.Height = frame.Height;
            return (text, result, style);
        }

        /// <summary>The dialog (not shown yet) and its fields: the Markdown box, width (mm), size
        /// (pt), font, the dimension style's font, and the Markdown style chosen. Also for the
        /// screenshots (tools).</summary>
        internal static (Dialog<bool> Dialog, RichTextArea Area, NumericStepper Width, NumericStepper Size, ComboBox Fonts, string StyleFont, StyleChoice Style) EditorDialog(
            RhinoDoc doc, string markdown, MarkdownRhinoText.Frame frame, int partStart, int partEnd, List<string> notes)
        {
            var area = EditorArea(markdown, partStart, partEnd);
            var mm = MillimetersPerUnit(doc);
            var pt = PointsPerUnit(doc);
            var styleFont = doc.DimStyles.Current.Font?.FamilyName ?? "Arial";
            var width = new NumericStepper { MinValue = 0, MaxValue = 1e6, DecimalPlaces = 1, Increment = 10, Value = Math.Round(frame.Width * mm, 3), Width = 90, ToolTip = "The width text wraps in, in millimeters (0: no wrapping)" };
            var size = new NumericStepper { MinValue = 0.5, MaxValue = 10000, DecimalPlaces = 1, Increment = 1, Value = Math.Round(frame.Height * pt, 3), Width = 70, ToolTip = "A paragraph's text height in points (1/72 inch; Rhino's text height: capital letters); headings are larger" };
            var fonts = new ComboBox
            {
                DataStore = Fonts.AvailableFontFamilies.Select(f => f.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Cast<object>().ToList(),
                Text = frame.Font ?? styleFont,
                AutoComplete = true,
                Width = 220,
                ToolTip = "The base font (code stays in Consolas)",
            };
            var dialog = new Dialog<bool> { Title = "MarkdownText", Resizable = true, Padding = 8 };
            var style = new StyleChoice { Css = MarkdownStyleFiles.DocumentCss(doc) };
            var ok = new Button { Text = "OK" };
            var cancel = new Button { Text = "Cancel" };
            ok.Click += (s, e) => dialog.Close(true);
            cancel.Click += (s, e) => dialog.Close(false);
            dialog.DefaultButton = null; // Enter is a new line
            dialog.AbortButton = cancel;
            var rows = new List<TableRow>();
            if (notes != null && notes.Count > 0)
                rows.Add(new TableRow(new Label { Text = "Taken from the Rhino objects: " + string.Join(" ", notes), TextColor = Color.FromArgb(160, 90, 0), Wrap = WrapMode.Word }));
            rows.Add(new TableRow(new Label
            {
                Text = partStart >= 0 ? "The part you picked is in black, the other parts' Markdown in gray (editable too)." : "Markdown -- headings, **bold**, *italic*, `code`, lists, - [ ] tasks, > quotes, ``` code, | tables |, ---, [links](gh:Name / ghview:Name / rhino:Name / rhinoview:Name / https://...)",
            }));
            rows.Add(new TableRow(area) { ScaleHeight = true });
            rows.Add(new TableRow(new Label
            {
                Text = "Ctrl+Z undoes a word at a time, Ctrl+Y (or Ctrl+Shift+Z) redoes. After OK, Rhino's Undo takes the whole edit back, Redo brings it again.",
                TextColor = Gray,
            }));
            rows.Add(new TableRow(StyleRow(doc, dialog, style)));
            rows.Add(new TableRow(new TableLayout(new TableRow(
                new Label { Text = "Width", VerticalAlignment = VerticalAlignment.Center }, width, new Label { Text = "mm   Size", VerticalAlignment = VerticalAlignment.Center }, size,
                new Label { Text = "pt   Font", VerticalAlignment = VerticalAlignment.Center }, fonts, null, ok, cancel)) { Spacing = new Size(6, 0) }));
            dialog.Content = new TableLayout { Spacing = new Size(6, 6) };
            foreach (var row in rows)
                ((TableLayout)dialog.Content).Rows.Add(row);
            dialog.Shown += (s, e) =>
            {
                area.Focus();
                PlaceCaret(area, Math.Max(0, partStart));
            };
            return (dialog, area, width, size, fonts, styleFont, style);
        }

        // The Rhino file's Markdown style: which it is, and Edit, Load, Save as, Default -- the
        // choice applied at OK, to all the file's Markdown texts
        private static Control StyleRow(RhinoDoc doc, Window dialog, StyleChoice style)
        {
            var label = new Label { VerticalAlignment = VerticalAlignment.Center, ToolTip = "One style for all of this Rhino file's Markdown texts, kept in the file. V Settings, \"Markdown style for Rhino\": the style a file starts with." };
            void Show()
            {
                var name = MarkdownStyle.IsPristineDefault(style.Css) ? "miniVivisection's default"
                    : style.Changed ? (string.IsNullOrEmpty(style.Source) || style.Source == "default" ? "edited" : "from " + style.Source)
                    : MarkdownStyleFiles.NameOf(doc, style.Css);
                label.Text = "Style: " + name + (style.Changed && style.Css != MarkdownStyleFiles.DocumentCss(doc) ? " -- OK draws all of this file's Markdown texts with it" : "");
                label.TextColor = style.Changed ? Color.FromArgb(160, 90, 0) : Gray;
            }
            void Take(string css, string source)
            {
                if (css == null)
                    return;
                style.Css = css.Replace("\r\n", "\n");
                style.Source = source;
                style.Changed = true;
                Show();
            }
            var edit = new Button { Text = "Edit style...", ToolTip = "This file's Markdown style in a small editor" };
            edit.Click += (s, e) =>
            {
                var result = MarkdownStyleEditor.EditText(dialog, MarkdownStyle.Target.Rhino, style.Css, MarkdownStyle.IsPristineDefault(style.Css) ? "miniVivisection's default" : "this file's");
                if (result != null && result.Value.Css != style.Css)
                    Take(result.Value.Css, result.Value.Source ?? style.Source);
            };
            var load = new Button { Text = "Load...", ToolTip = "A style file (CSS) for this file's Markdown texts" };
            load.Click += (s, e) =>
            {
                var path = MarkdownStyleEditor.AskOpen(dialog, MarkdownStyle.Target.Rhino);
                if (path == null)
                    return;
                try
                {
                    Take(File.ReadAllText(path), Path.GetFileName(path));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(dialog, "Can't read " + path + ": " + ex.Message, "MarkdownText", MessageBoxType.Error);
                }
            };
            var save = new Button { Text = "Save as...", ToolTip = "This style as a file of your own (for other Rhino files, or V Settings)" };
            save.Click += (s, e) =>
            {
                var saved = MarkdownStyleEditor.SaveAs(dialog, MarkdownStyle.Target.Rhino, style.Css);
                if (saved != null)
                    Take(MarkdownStyle.AsOwn(style.Css), Path.GetFileName(saved));
            };
            var reset = new Button { Text = "Default", ToolTip = "miniVivisection's default style" };
            reset.Click += (s, e) => Take(MarkdownStyle.DefaultCss(MarkdownStyle.Target.Rhino), "default");
            Show();
            return new TableLayout(new TableRow(new TableCell(label, true), edit, load, save, reset)) { Spacing = new Size(6, 0) };
        }

        /// <summary>The dialog's text box: the Markdown in Consolas, unwrapped; outside
        /// [partStart, partEnd) gray (-1: none).</summary>
        internal static RichTextArea EditorArea(string markdown, int partStart, int partEnd)
        {
            var area = new RichTextArea
            {
                Font = new Eto.Drawing.Font("Consolas", 10),
                AcceptsTab = true,
                Wrap = false,
                Size = new Size(760, 460),
            };
            area.Text = markdown;
            if (partStart >= 0 && partEnd > partStart)
            {
                // The other parts' Markdown gray: still editable
                if (partStart > 0)
                    area.Buffer.SetForeground(new Range<int>(0, partStart - 1), Gray);
                var length = area.Text.Length;
                if (partEnd < length)
                    area.Buffer.SetForeground(new Range<int>(partEnd, length - 1), Gray);
            }
            UndoInSteps(area);
            return area;
        }

        /// <summary>The box's undo in steps of a word: WPF's RichTextBox keeps all the typing in a
        /// row as one step -- a step ends now before a space, a line break, a tab or a punctuation
        /// mark, and after a pause. A long history (1000 steps, starting empty: the Markdown as it
        /// came is where undo ends), and Ctrl+Shift+Z redoes too (besides WPF's Ctrl+Y).</summary>
        internal static void UndoInSteps(RichTextArea area)
        {
            var box = area.ControlObject;
            var type = box?.GetType();
            var enabled = type?.GetProperty("IsUndoEnabled");
            var limit = type?.GetProperty("UndoLimit");
            var close = type?.GetMethod("LockCurrentUndoUnit", Type.EmptyTypes);
            var redo = type?.GetMethod("Redo", Type.EmptyTypes);
            if (enabled == null || limit == null || close == null)
                return; // not WPF's: its own undo
            try
            {
                enabled.SetValue(box, false); // the limit is set with the history off (and empty)
                limit.SetValue(box, 1000);
                enabled.SetValue(box, true);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.General, "MarkdownText: the box's undo history: {0}", Log.Describe(ex));
            }
            var last = DateTime.MinValue;
            area.KeyDown += (sender, e) =>
            {
                try
                {
                    if (e.KeyData == (Keys.Control | Keys.Shift | Keys.Z) && redo != null)
                    {
                        redo.Invoke(box, null);
                        e.Handled = true;
                        return;
                    }
                    var now = DateTime.UtcNow;
                    var boundary = e.Key == Keys.Space || e.Key == Keys.Enter || e.Key == Keys.Tab || (e.KeyChar != '\0' && char.IsPunctuation(e.KeyChar));
                    if (boundary || (now - last).TotalSeconds > 1.5)
                        close.Invoke(box, null);
                    last = now;
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.General, "MarkdownText: an undo step: {0}", Log.Describe(ex));
                }
            };
        }

        // The caret at an offset: at a line's start Eto's CaretIndex lands at the end of the line
        // before, so the WPF box's caret goes to that line's paragraph start
        internal static void PlaceCaret(RichTextArea area, int offset)
        {
            var text = area.Text;
            offset = Math.Min(offset, text.Length);
            var line = text.Take(offset).Count(c => c == '\n');
            try
            {
                var box = area.ControlObject;
                var document = box?.GetType().GetProperty("Document")?.GetValue(box);
                var paragraph = (document?.GetType().GetProperty("Blocks")?.GetValue(document) as IEnumerable)?.Cast<object>().ElementAtOrDefault(line);
                var start = paragraph?.GetType().GetProperty("ContentStart")?.GetValue(paragraph);
                var caret = box?.GetType().GetProperty("CaretPosition");
                if (start != null && caret != null)
                {
                    caret.SetValue(box, start);
                    paragraph.GetType().GetMethod("BringIntoView", Type.EmptyTypes)?.Invoke(paragraph, null);
                    return;
                }
            }
            catch (Exception)
            {
                // Eto's caret below
            }
            area.CaretIndex = offset;
        }
    }
}
