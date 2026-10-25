using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using MiniVivisection;

namespace MiniVivisection.GH
{
    /// <summary>
    /// A script Panel edited in place: a control laid over the Panel on Grasshopper's canvas,
    /// drawn as the Panel draws its code (ScriptPanels) -- line numbers, colors, no wrapping --
    /// with a caret, selection, undo, the clipboard, auto-indent, Tab / Shift+Tab, Ctrl+/ for a
    /// comment, and scrolling (the wheel; Shift+wheel sideways). Like Grasshopper's own text box
    /// (GH_PanelAttributes.ShowCanvasTextBox): clicking elsewhere keeps the text, Esc drops the
    /// changes; Ctrl+Enter or Ctrl+S keeps it too. The edit is one step of Grasshopper's undo.
    /// Opened at a line, that line is marked: where Ctrl+Alt+B stopped the Panel's script.
    /// </summary>
    internal sealed class CodeBox : Control
    {
        private readonly GH_Panel _panel;
        private readonly GH_Canvas _canvas;
        private readonly ScriptTokens.Language _language;
        private readonly string _original;
        private readonly string _newLine;
        private readonly Color _fore, _back;
        private readonly ScriptPanels.Palette _palette;
        private readonly Timer _blink;
        private readonly Stack<Snapshot> _undo = new Stack<Snapshot>();
        private readonly Stack<Snapshot> _redo = new Stack<Snapshot>();
        private List<string> _lines;
        private readonly Dictionary<string, string> _raw = new Dictionary<string, string>(StringComparer.Ordinal); // expanded line -> as it was, with tabs
        private List<ScriptTokens.Token>[] _tokens;
        private int _line, _column, _anchorLine, _anchorColumn;
        private int _preferredColumn = -1;
        private int _markedLine = -1; // 0-based; -1: none
        private float _scrollX, _scrollY;
        private float _charWidth, _lineHeight;
        private bool _caretOn = true;
        private bool _closed;
        private bool _dragging;
        private bool _edited;
        private string _lastEdit; // "type": consecutive typing is one undo step
        private string _stopTitle;    // where Ctrl+Alt+B stopped the script: the overlay
        private string[] _stopLines;
        private RectangleF _overlay;  // where it was drawn (empty: not shown)
        private static CodeBox s_open;

        /// <summary>Whether a code box has the keyboard now: Grasshopper's window then leaves the
        /// keys alone (ScriptPanels' prefix on GH_DocumentEditor.EditorKeyDown).</summary>
        internal static bool IsFocused => s_open != null && !s_open._closed && s_open.ContainsFocus;

        private readonly struct Snapshot
        {
            public Snapshot(string text, int line, int column)
            {
                Text = text;
                Line = line;
                Column = column;
            }

            public string Text { get; }

            public int Line { get; }

            public int Column { get; }
        }

        private const float TopMargin = 3f;
        private const int TabSize = ScriptTokens.TabSize;

        private CodeBox(GH_PanelAttributes attributes, GH_Canvas canvas, ScriptTokens.Language language, float zoom)
        {
            _panel = attributes.Owner;
            _canvas = canvas;
            _language = language;
            _original = _panel.UserText ?? "";
            _newLine = _original.Contains("\r\n") ? "\r\n" : "\n";
            _fore = ScriptPanels.Fore(attributes);
            _back = Color.FromArgb(255, ScriptPanels.Back(attributes));
            _palette = ScriptPanels.IsDark(_back) ? ScriptPanels.Dark : ScriptPanels.Light;
            // Shown with tabs as spaces; a line left as it was goes back with its tabs (Raw)
            var raw = ScriptTokens.Lines(_original);
            _lines = raw.Select(ScriptTokens.ExpandTabs).ToList();
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i].IndexOf('\t') >= 0 && !_raw.ContainsKey(_lines[i]))
                    _raw[_lines[i]] = raw[i];
            }
            Retokenize();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.IBeam;
            var size = Math.Max(6f, (_panel.Properties.Font?.SizeInPoints ?? 8f) * zoom);
            Font = GH_FontServer.NewFont(GH_FontServer.FamilyConsole, size, FontStyle.Regular);
            using (var g = CreateGraphics())
            {
                _charWidth = g.MeasureString(new string('0', 40), Font, PointF.Empty, ScriptPanels.Typographic).Width / 40f;
                _lineHeight = Font.GetHeight(g);
            }
            _blink = new Timer { Interval = 530 };
            _blink.Tick += (s, e) =>
            {
                _caretOn = !_caretOn;
                Invalidate();
            };
        }

        /// <summary>The Panel's script edited in place, its caret on <paramref name="line"/>
        /// (1-based; 0: the start) -- at <paramref name="column"/> (0-based; -1: the line's first
        /// character), and that line marked when <paramref name="mark"/>. False when the Panel
        /// isn't on screen large enough: then nothing opens.</summary>
        public static bool Open(GH_Panel panel, ScriptTokens.Language language, int line = 0, bool mark = false, int column = -1)
        {
            var canvas = Instances.ActiveCanvas;
            if (!(panel?.Attributes is GH_PanelAttributes attributes) || canvas == null || canvas.Document == null ||
                !ReferenceEquals(panel.OnPingDocument(), canvas.Document))
                return false;
            foreach (var open in canvas.Controls.OfType<CodeBox>().ToList())
                open.Close(keep: true);
            var screen = ScreenRect(attributes, canvas);
            if (screen.Width < 120 || screen.Height < 40)
                return false;
            var box = new CodeBox(attributes, canvas, language, canvas.Viewport.Zoom) { Bounds = screen, _zoom = canvas.Viewport.Zoom };
            canvas.Controls.Add(box);
            box.BringToFront();
            if (line > 0)
            {
                box.MoveTo(Math.Min(line, box._lines.Count) - 1, 0, false);
                box._column = column >= 0 ? Math.Min(column, box._lines[box._line].Length) : box.FirstNonSpace(box._line);
                box.Collapse();
                if (mark)
                {
                    box._markedLine = box._line;
                    var stop = ScriptPanels.StopOf(panel);
                    if (stop.Line == line)
                    {
                        box._stopTitle = stop.Title;
                        box._stopLines = stop.Lines;
                    }
                }
                box.ScrollToCaret(center: true);
            }
            s_open = box;
            box.Focus();
            box._blink.Start();
            // Placed over the Panel as the canvas shows it now: panned, zoomed or resized under
            // it (the wheel over the canvas outside the box), it keeps the text and closes
            canvas.ViewportChanged += box.OnCanvasChanged;
            canvas.Resize += box.OnCanvasChanged;
            return true;
        }

        private float _zoom; // the canvas's zoom the box was made for (its font)

        // Where the Panel's text area is on screen now (empty when it isn't)
        private static Rectangle ScreenRect(GH_PanelAttributes attributes, GH_Canvas canvas)
        {
            var area = RectangleF.FromLTRB(attributes.PanelBounds.Left + 1, attributes.ContentBounds.Top, attributes.PanelBounds.Right - 1, attributes.PanelBounds.Bottom - 1);
            return Rectangle.Intersect(GH_Convert.ToRectangle(canvas.Viewport.ProjectRectangle(area)), canvas.ClientRectangle);
        }

        // The canvas panned or resized under the box: the box follows the Panel. Zoomed: the
        // font wouldn't fit any more -- the text is kept and the box closes.
        private void OnCanvasChanged(object sender, EventArgs e)
        {
            if (_closed)
                return;
            try
            {
                if (Math.Abs(_canvas.Viewport.Zoom - _zoom) > 0.001f || !(_panel.Attributes is GH_PanelAttributes attributes))
                {
                    Close(keep: true);
                    return;
                }
                var screen = ScreenRect(attributes, _canvas);
                if (screen.Width < 120 || screen.Height < 40)
                    Close(keep: true);
                else if (screen != Bounds)
                    Bounds = screen;
            }
            catch (Exception)
            {
                // as it is
            }
        }

        // --- the text ---------------------------------------------------------------------------

        private string Text_ => string.Join(_newLine, _lines);

        // The text to keep: a line as it was shown, unless it was left as it was -- then with its tabs
        private string TextToKeep() => string.Join(_newLine, _lines.Select(l => _raw.TryGetValue(l, out var raw) ? raw : l));

        private void Retokenize() => _tokens = ScriptTokens.Tokenize(_lines, _language);

        private bool HasSelection => _line != _anchorLine || _column != _anchorColumn;

        private (int Line, int Column, int EndLine, int EndColumn) Selection()
        {
            if (_line < _anchorLine || (_line == _anchorLine && _column < _anchorColumn))
                return (_line, _column, _anchorLine, _anchorColumn);
            return (_anchorLine, _anchorColumn, _line, _column);
        }

        private string SelectedText()
        {
            if (!HasSelection)
                return "";
            var (l1, c1, l2, c2) = Selection();
            if (l1 == l2)
                return _lines[l1].Substring(c1, c2 - c1);
            var parts = new List<string> { _lines[l1].Substring(c1) };
            for (var i = l1 + 1; i < l2; i++)
                parts.Add(_lines[i]);
            parts.Add(_lines[l2].Substring(0, c2));
            return string.Join(_newLine, parts);
        }

        // Before an edit: an undo step (consecutive typing: one step).
        private void BeginEdit(string kind)
        {
            if (kind == null || kind != _lastEdit)
                _undo.Push(new Snapshot(Text_, _line, _column));
            _lastEdit = kind;
            _redo.Clear();
            _edited = true;
            _markedLine = -1;
            _stopTitle = null; // the explanation goes with the first change
        }

        private void EndEdit()
        {
            Retokenize();
            _preferredColumn = -1;
            ScrollToCaret(center: false);
            ShowCaret();
        }

        private void DeleteSelection()
        {
            if (!HasSelection)
                return;
            var (l1, c1, l2, c2) = Selection();
            _lines[l1] = _lines[l1].Substring(0, c1) + _lines[l2].Substring(c2);
            _lines.RemoveRange(l1 + 1, l2 - l1);
            _line = _anchorLine = l1;
            _column = _anchorColumn = c1;
        }

        private void Insert(string text)
        {
            DeleteSelection();
            var parts = ScriptTokens.Lines(text).Select(ScriptTokens.ExpandTabs).ToList();
            var line = _lines[_line];
            var before = line.Substring(0, _column);
            var after = line.Substring(_column);
            if (parts.Count == 1)
            {
                _lines[_line] = before + parts[0] + after;
                _column += parts[0].Length;
            }
            else
            {
                _lines[_line] = before + parts[0];
                for (var i = 1; i < parts.Count; i++)
                    _lines.Insert(_line + i, parts[i]);
                _line += parts.Count - 1;
                _column = parts[parts.Count - 1].Length;
                _lines[_line] += after;
            }
            Collapse();
        }

        private void Collapse()
        {
            _anchorLine = _line;
            _anchorColumn = _column;
        }

        private int FirstNonSpace(int line)
        {
            var text = _lines[line];
            var i = 0;
            while (i < text.Length && text[i] == ' ')
                i++;
            return i;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        // --- keys -------------------------------------------------------------------------------

        protected override bool IsInputKey(Keys keyData) => true;

        // Every key the box knows is taken here, before Grasshopper's window sees it (its
        // shortcuts: Delete, Ctrl+C/V/Z on the canvas); characters come as KeyPress.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            try
            {
                if (Key(keyData))
                    return true;
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("miniVivisection: the script Panel's editor: {0}", ex.Message);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private bool Key(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            var shift = (keyData & Keys.Shift) != 0;
            var control = (keyData & Keys.Control) != 0;
            var alt = (keyData & Keys.Alt) != 0;
            if (alt && !control)
                return false;
            if (control && alt)
                return false; // AltGr: a character
            switch (key)
            {
                case Keys.Escape:
                    Close(keep: false);
                    return true;
                case Keys.Enter when control:
                case Keys.S when control:
                    Close(keep: true);
                    return true;
                case Keys.Enter:
                    NewLine();
                    return true;
                case Keys.Tab:
                    Tab(shift);
                    return true;
                case Keys.Back:
                    Backspace(control);
                    return true;
                case Keys.Delete:
                    Delete(control);
                    return true;
                case Keys.Left:
                    MoveCaret(control ? WordLeft() : StepLeft(), shift);
                    return true;
                case Keys.Right:
                    MoveCaret(control ? WordRight() : StepRight(), shift);
                    return true;
                case Keys.Up:
                    Vertical(-1, shift);
                    return true;
                case Keys.Down:
                    Vertical(1, shift);
                    return true;
                case Keys.PageUp:
                    Vertical(-Math.Max(1, VisibleLines() - 1), shift);
                    return true;
                case Keys.PageDown:
                    Vertical(Math.Max(1, VisibleLines() - 1), shift);
                    return true;
                case Keys.Home:
                    if (control)
                        MoveCaret((0, 0), shift);
                    else
                    {
                        var first = FirstNonSpace(_line);
                        MoveCaret((_line, _column == first ? 0 : first), shift);
                    }
                    return true;
                case Keys.End:
                    MoveCaret(control ? (_lines.Count - 1, _lines[_lines.Count - 1].Length) : (_line, _lines[_line].Length), shift);
                    return true;
                case Keys.A when control:
                    _anchorLine = 0;
                    _anchorColumn = 0;
                    _line = _lines.Count - 1;
                    _column = _lines[_line].Length;
                    ShowCaret();
                    return true;
                case Keys.C when control:
                case Keys.Insert when control:
                    Copy();
                    return true;
                case Keys.X when control:
                    if (Copy())
                    {
                        BeginEdit(null);
                        DeleteSelection();
                        EndEdit();
                    }
                    return true;
                case Keys.V when control:
                case Keys.Insert when shift:
                    Paste();
                    return true;
                case Keys.Z when control && shift:
                case Keys.Y when control:
                    Redo();
                    return true;
                case Keys.Z when control:
                    Undo();
                    return true;
                case Keys.OemQuestion when control:
                case Keys.Divide when control:
                    ToggleComment();
                    return true;
            }
            return control; // other Ctrl keys: nothing, and not Grasshopper's either
        }

        // Rhino's message loop doesn't always give WinForms' key preprocessing (ProcessCmdKey) a
        // turn: the keys come here then, once Grasshopper's window left them (EditorKeyDown).
        protected override void OnKeyDown(KeyEventArgs e)
        {
            try
            {
                if (Key(e.KeyData))
                {
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine("miniVivisection: the script Panel's editor: {0}", ex.Message);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (e.KeyChar < ' ' || e.KeyChar == (char)127)
                return;
            BeginEdit("type");
            Insert(e.KeyChar.ToString());
            EndEdit();
            e.Handled = true;
        }

        private static readonly System.Text.RegularExpressions.Regex MarkdownItem =
            new System.Text.RegularExpressions.Regex(@"^(\s*)([-*+]|\d{1,9}[.)]|>)(\s+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private void NewLine()
        {
            BeginEdit(null);
            DeleteSelection();
            if (_language == ScriptTokens.Language.Markdown)
            {
                // A list or a quote goes on: the next item's marker (a number counted up); Enter on
                // an empty item ends the list
                var current = _lines[_line];
                var item = MarkdownItem.Match(current);
                if (item.Success && _column >= item.Length)
                {
                    if (current.Substring(item.Length).Trim().Length == 0)
                    {
                        _lines[_line] = "";
                        _column = 0;
                        Collapse();
                        EndEdit();
                        return;
                    }
                    var marker = item.Groups[2].Value;
                    if (char.IsDigit(marker[0]) && int.TryParse(marker.Substring(0, marker.Length - 1), out var number))
                        marker = (number + 1) + marker.Substring(marker.Length - 1);
                    Insert("\n" + item.Groups[1].Value + marker + item.Groups[3].Value);
                    EndEdit();
                    return;
                }
            }
            var before = _lines[_line].Substring(0, _column).TrimEnd();
            var indent = FirstNonSpace(_line);
            if (indent > _column)
                indent = _column;
            if ((_language == ScriptTokens.Language.Python && before.EndsWith(":", StringComparison.Ordinal)) ||
                (_language == ScriptTokens.Language.CSharp && before.EndsWith("{", StringComparison.Ordinal)))
                indent += TabSize;
            Insert("\n" + new string(' ', indent));
            EndEdit();
        }

        private void Tab(bool outdent)
        {
            BeginEdit(null);
            if (outdent || (HasSelection && _line != _anchorLine))
            {
                var (l1, _, l2, c2) = Selection();
                if (l2 > l1 && c2 == 0)
                    l2--; // a selection ending at a line's start doesn't take that line
                for (var i = l1; i <= l2; i++)
                {
                    if (outdent)
                    {
                        var remove = Math.Min(FirstNonSpace(i), TabSize);
                        _lines[i] = _lines[i].Substring(remove);
                        if (i == _line)
                            _column = Math.Max(0, _column - remove);
                        if (i == _anchorLine)
                            _anchorColumn = Math.Max(0, _anchorColumn - remove);
                    }
                    else if (_lines[i].Length > 0)
                    {
                        _lines[i] = new string(' ', TabSize) + _lines[i];
                        if (i == _line)
                            _column += TabSize;
                        if (i == _anchorLine)
                            _anchorColumn += TabSize;
                    }
                }
            }
            else
                Insert(new string(' ', TabSize - _column % TabSize));
            EndEdit();
        }

        private void Backspace(bool word)
        {
            if (HasSelection)
            {
                BeginEdit(null);
                DeleteSelection();
                EndEdit();
                return;
            }
            if (_line == 0 && _column == 0)
                return;
            BeginEdit("back");
            var to = word ? WordLeft() : StepLeft();
            if (!word && _column > 0 && _column <= FirstNonSpace(_line))
                to = (_line, Math.Max(0, (_column - 1) / TabSize * TabSize)); // in the indentation: back to the last tab stop
            _anchorLine = to.Line;
            _anchorColumn = to.Column;
            DeleteSelection();
            EndEdit();
        }

        private void Delete(bool word)
        {
            if (!HasSelection)
            {
                if (_line == _lines.Count - 1 && _column == _lines[_line].Length)
                    return;
                var to = word ? WordRight() : StepRight();
                _anchorLine = to.Line;
                _anchorColumn = to.Column;
            }
            BeginEdit(null);
            DeleteSelection();
            EndEdit();
        }

        private bool Copy()
        {
            if (!HasSelection)
                return false;
            try
            {
                Clipboard.SetText(SelectedText().Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
                return true;
            }
            catch (Exception)
            {
                return false; // the clipboard busy
            }
        }

        private void Paste()
        {
            string text;
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (Exception)
            {
                return;
            }
            if (string.IsNullOrEmpty(text))
                return;
            BeginEdit(null);
            Insert(text);
            EndEdit();
        }

        private void Undo()
        {
            if (_undo.Count == 0)
                return;
            _redo.Push(new Snapshot(Text_, _line, _column));
            Restore(_undo.Pop());
        }

        private void Redo()
        {
            if (_redo.Count == 0)
                return;
            _undo.Push(new Snapshot(Text_, _line, _column));
            Restore(_redo.Pop());
        }

        private void Restore(Snapshot snapshot)
        {
            _lines = ScriptTokens.Lines(snapshot.Text).ToList();
            _line = Math.Min(snapshot.Line, _lines.Count - 1);
            _column = Math.Min(snapshot.Column, _lines[_line].Length);
            Collapse();
            _lastEdit = null;
            _edited = true;
            EndEdit();
        }

        // Ctrl+/: the selected lines (or the caret's) commented, or uncommented when all are.
        private void ToggleComment()
        {
            if (_language == ScriptTokens.Language.Markdown)
                return; // Markdown has no line comments
            var mark = _language == ScriptTokens.Language.CSharp ? "// " : "# ";
            var bare = mark.TrimEnd();
            var (l1, _, l2, c2) = Selection();
            if (l2 > l1 && c2 == 0)
                l2--;
            var lines = Enumerable.Range(l1, l2 - l1 + 1).Where(i => _lines[i].Trim().Length > 0).ToList();
            if (lines.Count == 0)
                return;
            BeginEdit(null);
            var uncomment = lines.All(i => _lines[i].TrimStart().StartsWith(bare, StringComparison.Ordinal));
            var indent = lines.Min(FirstNonSpace);
            foreach (var i in lines)
            {
                var text = _lines[i];
                int change;
                if (uncomment)
                {
                    var at = FirstNonSpace(i);
                    var length = text.Substring(at).StartsWith(mark, StringComparison.Ordinal) ? mark.Length : bare.Length;
                    _lines[i] = text.Remove(at, length);
                    change = -length;
                }
                else
                {
                    _lines[i] = text.Insert(indent, mark);
                    change = mark.Length;
                }
                if (i == _line)
                    _column = Math.Max(0, Math.Min(_lines[i].Length, _column + change));
                if (i == _anchorLine)
                    _anchorColumn = Math.Max(0, Math.Min(_lines[i].Length, _anchorColumn + change));
            }
            EndEdit();
        }

        // --- moving ---------------------------------------------------------------------------

        private (int Line, int Column) StepLeft() =>
            _column > 0 ? (_line, _column - 1) : _line > 0 ? (_line - 1, _lines[_line - 1].Length) : (0, 0);

        private (int Line, int Column) StepRight() =>
            _column < _lines[_line].Length ? (_line, _column + 1) : _line < _lines.Count - 1 ? (_line + 1, 0) : (_line, _column);

        private (int Line, int Column) WordLeft()
        {
            if (_column == 0)
                return StepLeft();
            var text = _lines[_line];
            var i = _column;
            while (i > 0 && text[i - 1] == ' ')
                i--;
            if (i > 0 && IsWord(text[i - 1]))
                while (i > 0 && IsWord(text[i - 1]))
                    i--;
            else if (i > 0)
                i--;
            return (_line, i);
        }

        private (int Line, int Column) WordRight()
        {
            var text = _lines[_line];
            if (_column >= text.Length)
                return StepRight();
            var i = _column;
            if (IsWord(text[i]))
                while (i < text.Length && IsWord(text[i]))
                    i++;
            else
                i++;
            while (i < text.Length && text[i] == ' ')
                i++;
            return (_line, i);
        }

        private void MoveCaret((int Line, int Column) to, bool select)
        {
            MoveTo(to.Line, to.Column, select);
            _preferredColumn = -1;
        }

        private void MoveTo(int line, int column, bool select)
        {
            _line = Math.Max(0, Math.Min(line, _lines.Count - 1));
            _column = Math.Max(0, Math.Min(column, _lines[_line].Length));
            if (!select)
                Collapse();
            _lastEdit = null;
            ScrollToCaret(center: false);
            ShowCaret();
        }

        private void Vertical(int lines, bool select)
        {
            if (_preferredColumn < 0)
                _preferredColumn = _column;
            var keep = _preferredColumn;
            MoveTo(_line + lines, keep, select);
            _preferredColumn = keep;
        }

        private int VisibleLines() => Math.Max(1, (int)((ClientSize.Height - TopMargin) / _lineHeight));

        // --- layout and drawing ---------------------------------------------------------------

        private float Gutter => (Math.Max(2, _lines.Count.ToString().Length) + 1.5f) * _charWidth;

        private float TextLeft => Gutter + _charWidth * 0.6f;

        private float ContentWidth => (_lines.Max(l => l.Length) + 2) * _charWidth;

        private float ContentHeight => _lines.Count * _lineHeight + TopMargin * 2;

        private void ScrollToCaret(bool center)
        {
            var viewWidth = ClientSize.Width - TextLeft - 8;
            var viewHeight = ClientSize.Height - TopMargin - 8;
            var y = _line * _lineHeight;
            if (center)
                _scrollY = y - viewHeight / 2;
            else if (y < _scrollY)
                _scrollY = y;
            else if (y + _lineHeight > _scrollY + viewHeight)
                _scrollY = y + _lineHeight - viewHeight;
            var x = _column * _charWidth;
            if (x < _scrollX)
                _scrollX = Math.Max(0, x - 4 * _charWidth);
            else if (x > _scrollX + viewWidth)
                _scrollX = x - viewWidth + 4 * _charWidth;
            ClampScroll();
        }

        private void ClampScroll()
        {
            _scrollY = Math.Max(0, Math.Min(_scrollY, Math.Max(0, ContentHeight - ClientSize.Height + 8)));
            _scrollX = Math.Max(0, Math.Min(_scrollX, Math.Max(0, ContentWidth - (ClientSize.Width - TextLeft) + 8)));
        }

        private void ShowCaret()
        {
            _caretOn = true;
            _blink.Stop();
            _blink.Start();
            Invalidate();
        }

        private (int Line, int Column) At(Point point)
        {
            var line = (int)Math.Floor((point.Y - TopMargin + _scrollY) / _lineHeight);
            line = Math.Max(0, Math.Min(line, _lines.Count - 1));
            var column = (int)Math.Round((point.X - TextLeft + _scrollX) / _charWidth);
            return (line, Math.Max(0, Math.Min(column, _lines[line].Length)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(_back);
            var gutter = Gutter;
            var textLeft = TextLeft;
            var first = Math.Max(0, (int)(_scrollY / _lineHeight));
            var last = Math.Min(_lines.Count - 1, (int)((_scrollY + ClientSize.Height) / _lineHeight) + 1);
            float Y(int line) => TopMargin + line * _lineHeight - _scrollY;

            using (var band = new SolidBrush(Color.FromArgb(ScriptPanels.IsDark(_back) ? 28 : 20, _fore)))
                g.FillRectangle(band, 0, 0, gutter, ClientSize.Height);
            using (var edge = new Pen(Color.FromArgb(45, _fore)))
                g.DrawLine(edge, gutter, 0, gutter, ClientSize.Height);
            using (var numbers = new SolidBrush(Color.FromArgb(120, _fore)))
            using (var current = new SolidBrush(_fore))
            {
                for (var i = first; i <= last; i++)
                    g.DrawString((i + 1).ToString(), Font, i == _line ? current : numbers, gutter - _charWidth * 0.5f, Y(i), ScriptPanels.TypographicFar);
            }

            g.SetClip(new RectangleF(gutter + 1, 0, ClientSize.Width - gutter - 1, ClientSize.Height));
            // The caret's line, the marked line (where a stop was), the selection
            using (var line = new SolidBrush(Color.FromArgb(14, _fore)))
                g.FillRectangle(line, gutter + 1, Y(_line), ClientSize.Width, _lineHeight);
            if (_markedLine >= 0)
            {
                using (var marked = new SolidBrush(ScriptPanels.StopBand))
                    g.FillRectangle(marked, gutter + 1, Y(_markedLine), ClientSize.Width, _lineHeight);
                g.ResetClip();
                var y = Y(_markedLine);
                using (var arrow = new SolidBrush(ScriptPanels.StopArrow))
                    g.FillPolygon(arrow, new[] { new PointF(2, y + 2), new PointF(2 + _lineHeight * 0.45f, y + _lineHeight / 2), new PointF(2, y + _lineHeight - 2) });
                g.SetClip(new RectangleF(gutter + 1, 0, ClientSize.Width - gutter - 1, ClientSize.Height));
            }
            if (HasSelection)
            {
                var (l1, c1, l2, c2) = Selection();
                using (var selection = new SolidBrush(Color.FromArgb(90, 51, 153, 255)))
                {
                    for (var i = Math.Max(l1, first); i <= Math.Min(l2, last); i++)
                    {
                        var from = i == l1 ? c1 : 0;
                        var to = i == l2 ? c2 : _lines[i].Length + 1;
                        g.FillRectangle(selection, textLeft + from * _charWidth - _scrollX, Y(i), Math.Max(_charWidth * 0.4f, (to - from) * _charWidth), _lineHeight);
                    }
                }
            }
            var brushes = new Dictionary<Color, SolidBrush>();
            SolidBrush BrushOf(Color color)
            {
                if (!brushes.TryGetValue(color, out var brush))
                    brushes[color] = brush = new SolidBrush(color);
                return brush;
            }
            try
            {
                var firstColumn = Math.Max(0, (int)(_scrollX / _charWidth) - 1);
                var lastColumn = (int)((_scrollX + ClientSize.Width) / _charWidth) + 1;
                for (var i = first; i <= last; i++)
                {
                    var text = _lines[i];
                    var y = Y(i);
                    var column = 0;
                    if (_tokens != null && i < _tokens.Length)
                    {
                        foreach (var token in _tokens[i])
                        {
                            if (token.Start > column)
                                Piece(column, token.Start, _fore);
                            Piece(token.Start, token.Start + token.Length, ScriptPanels.ColorOf(token.Kind, _palette, _fore));
                            column = token.Start + token.Length;
                        }
                    }
                    if (column < text.Length)
                        Piece(column, text.Length, _fore);

                    void Piece(int start, int end, Color color)
                    {
                        start = Math.Max(start, firstColumn);
                        end = Math.Min(end, Math.Min(text.Length, lastColumn));
                        while (start < end && text[start] == ' ')
                            start++;
                        if (start < end)
                            g.DrawString(text.Substring(start, end - start), Font, BrushOf(color), textLeft + start * _charWidth - _scrollX, y, ScriptPanels.Typographic);
                    }
                }
            }
            finally
            {
                foreach (var brush in brushes.Values)
                    brush.Dispose();
            }
            if (_caretOn && Focused)
            {
                using (var caret = new Pen(_fore, Math.Max(1.5f, _charWidth / 6f)))
                {
                    var x = textLeft + _column * _charWidth - _scrollX;
                    g.DrawLine(caret, x, Y(_line) + 1, x, Y(_line) + _lineHeight - 1);
                }
            }
            g.ResetClip();

            // Where the view is in the code: thin bars, right and bottom
            using (var bar = new SolidBrush(Color.FromArgb(90, _fore)))
            {
                if (ContentHeight > ClientSize.Height)
                {
                    var length = Math.Max(16f, ClientSize.Height * ClientSize.Height / ContentHeight);
                    var at = (ClientSize.Height - length) * _scrollY / Math.Max(1f, ContentHeight - ClientSize.Height);
                    g.FillRectangle(bar, ClientSize.Width - 5, at, 4, length);
                }
                var viewWidth = ClientSize.Width - textLeft;
                if (ContentWidth > viewWidth)
                {
                    var length = Math.Max(16f, viewWidth * viewWidth / ContentWidth);
                    var at = textLeft + (viewWidth - length) * _scrollX / Math.Max(1f, ContentWidth - viewWidth);
                    g.FillRectangle(bar, at, ClientSize.Height - 5, length, 4);
                }
            }
            using (var border = new Pen(Color.FromArgb(110, _fore)))
                g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            DrawStopOverlay(g, textLeft);
        }

        // What happened, top right, as the Script Editor's stop overlay: a click puts it away, the
        // first change too.
        private void DrawStopOverlay(Graphics g, float textLeft)
        {
            _overlay = RectangleF.Empty;
            if (_stopTitle == null)
                return;
            var pad = _charWidth * 0.8f;
            var width = Math.Min(ClientSize.Width - textLeft - 12, 64 * _charWidth + 2 * pad);
            if (width < 12 * _charWidth)
                return;
            var columns = Math.Max(10, (int)((width - 2 * pad) / _charWidth));
            var lines = new List<(string Text, bool Title)>();
            foreach (var part in Wrap(_stopTitle, columns))
                lines.Add((part, true));
            foreach (var paragraph in _stopLines ?? new string[0])
                foreach (var part in Wrap(paragraph, columns))
                    lines.Add((part, false));
            lines.Add(("(click here to put this away)", false));
            var height = Math.Min(ClientSize.Height - 8, lines.Count * _lineHeight + 2 * pad);
            _overlay = new RectangleF(ClientSize.Width - width - 10, 6, width, height);
            // Not over the stopped line itself: at the bottom then
            var stopped = _markedLine >= 0 ? new RectangleF(0, TopMargin + _markedLine * _lineHeight - _scrollY, ClientSize.Width, _lineHeight) : RectangleF.Empty;
            if (!stopped.IsEmpty && _overlay.IntersectsWith(stopped))
                _overlay.Y = Math.Max(6, ClientSize.Height - height - 8);
            using (var fill = new SolidBrush(Color.FromArgb(245, 255, 244, 214)))
                g.FillRectangle(fill, _overlay);
            using (var edge = new Pen(Color.FromArgb(220, 191, 128, 0), 1.5f))
                g.DrawRectangle(edge, _overlay.X, _overlay.Y, _overlay.Width, _overlay.Height);
            g.SetClip(_overlay);
            using (var bold = new Font(Font, FontStyle.Bold))
            using (var title = new SolidBrush(Color.FromArgb(255, 120, 60, 0)))
            using (var text = new SolidBrush(Color.FromArgb(255, 40, 40, 40)))
            {
                var y = _overlay.Y + pad;
                foreach (var (line, isTitle) in lines)
                {
                    g.DrawString(line, isTitle ? bold : Font, isTitle ? title : text, _overlay.X + pad, y, ScriptPanels.Typographic);
                    y += _lineHeight;
                }
            }
            g.ResetClip();
        }

        // Words to lines of at most columns characters.
        private static IEnumerable<string> Wrap(string text, int columns)
        {
            var line = "";
            foreach (var word in (text ?? "").Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > columns)
                {
                    yield return line;
                    line = "";
                }
                line = line.Length == 0 ? word : line + " " + word;
                while (line.Length > columns)
                {
                    yield return line.Substring(0, columns);
                    line = line.Substring(columns);
                }
            }
            if (line.Length > 0)
                yield return line;
        }

        // --- the mouse --------------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left)
                return;
            if (_stopTitle != null && _overlay.Contains(e.Location))
            {
                _stopTitle = null; // read: out of the way
                Invalidate();
                return;
            }
            var at = At(e.Location);
            MoveTo(at.Line, at.Column, (ModifierKeys & Keys.Shift) != 0);
            _preferredColumn = -1;
            _dragging = true;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging)
                return;
            var at = At(e.Location);
            MoveTo(at.Line, at.Column, true);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var text = _lines[_line];
            var from = _column;
            var to = _column;
            while (from > 0 && IsWord(text[from - 1]))
                from--;
            while (to < text.Length && IsWord(text[to]))
                to++;
            _anchorLine = _line;
            _anchorColumn = from;
            _column = to;
            _dragging = false;
            ShowCaret();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs handled)
                handled.Handled = true; // not Grasshopper's zoom
            var steps = e.Delta / 120f;
            if ((ModifierKeys & Keys.Shift) != 0)
                _scrollX -= steps * 6 * _charWidth;
            else
                _scrollY -= steps * 3 * _lineHeight;
            ClampScroll();
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            ShowCaret();
        }

        // Clicking elsewhere: the text kept, as Grasshopper's own text box does.
        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (!_dragging)
                BeginInvoke(new Action(() =>
                {
                    if (!_closed && !ContainsFocus)
                        Close(keep: true);
                }));
        }

        /// <summary>Closed: its text into the Panel (<paramref name="keep"/>, when changed), or dropped.</summary>
        internal void Close(bool keep)
        {
            if (_closed)
                return;
            _closed = true;
            if (ReferenceEquals(s_open, this))
                s_open = null;
            _blink.Stop();
            _canvas.ViewportChanged -= OnCanvasChanged;
            _canvas.Resize -= OnCanvasChanged;
            var text = TextToKeep();
            var parent = Parent;
            parent?.Controls.Remove(this);
            if (keep && _edited && text != _original.Replace("\r\n", "\n").Replace("\n", _newLine))
            {
                _panel.RecordUndoEvent("Edit Panel");
                _panel.TriggerAutoSave();
                _panel.SetUserText(text);
            }
            _canvas.Focus();
            _canvas.Refresh();
            Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _blink.Dispose();
            base.Dispose(disposing);
        }

        // --- for the live test ------------------------------------------------------------------

        /// <summary>Keys as the user presses them (a key name per item, "Ctrl+Z", or text to type).</summary>
        internal void Press(string what)
        {
            if (Enum.TryParse(what.Replace("Ctrl+", "").Replace("Shift+", ""), out Keys key) && what.Length > 1)
            {
                if (what.Contains("Ctrl+"))
                    key |= Keys.Control;
                if (what.Contains("Shift+"))
                    key |= Keys.Shift;
                Key(key);
                return;
            }
            foreach (var c in what)
                OnKeyPress(new KeyPressEventArgs(c));
        }

        internal string TextNow => Text_;

        internal int CaretLine => _line + 1;

        internal int CaretColumn => _column;
    }
}
