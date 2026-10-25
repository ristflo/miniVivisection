using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;

namespace MiniVivisection
{
    /// <summary>
    /// A console-style text view for the plug-in's prompts (Python, PowerShell, Claude):
    /// output and input in one text area. The owner prints a prompt after the output
    /// and the user types right after it, as in a terminal; the text before the prompt
    /// can be selected and copied, not changed.
    ///
    /// Keys: Enter submits the input (Shift+Enter starts a new line in it, where the
    /// owner allows it); Up/Down recall earlier inputs; Escape clears the input;
    /// Ctrl+C copies a selection, else interrupts; Home goes to the input's start.
    /// While nothing is prompted -- a command runs -- typing is ignored.
    ///
    /// Font size: Options "Prompt font size" (V Python, V PowerShell, V Claude), also
    /// Ctrl+mouse wheel. Syntax colors (SyntaxColors, the Script Editor's): the input in
    /// <see cref="InputLanguage"/> while it's typed, and fenced code blocks in the output
    /// (<see cref="ColorCodeBlocks"/>); each owner's option turns them on and off.
    /// </summary>
    internal sealed class TerminalView : Panel
    {
        private static readonly List<WeakReference<TerminalView>> s_all = new List<WeakReference<TerminalView>>();
        private readonly RichTextArea _text;
        private bool _coloring; // formatting changes raise TextChanged too
        private bool _colored;  // anything colored: new output is set back to plain
        private int _fenceScan; // code blocks are colored from here on
        private readonly List<string> _history = new List<string>();
        private int _historyIndex;
        private string _prompt; // the shown prompt; null while a command runs
        private int _inputStart; // right after the prompt

        public TerminalView(object editor, bool wrap)
        {
            _text = new RichTextArea { Font = Fonts.Monospace(MiniVivisectionSettings.TerminalFontSize), Wrap = wrap, AcceptsTab = false, AcceptsReturn = false, SpellCheck = false };
            _text.KeyDown += OnKeyDown;
            _text.TextInput += OnTextInput;
            _text.TextChanged += (s, e) => ColorInput();
            _text.MouseWheel += OnMouseWheel;
            lock (s_all)
            {
                s_all.RemoveAll(r => !r.TryGetTarget(out _));
                s_all.Add(new WeakReference<TerminalView>(this));
            }
            var copy = new ButtonMenuItem((s, e) => Clipboard.Instance.Text = _text.SelectedText ?? "") { Text = "Copy" };
            var paste = new ButtonMenuItem((s, e) => Paste()) { Text = "Paste" };
            var selectAll = new ButtonMenuItem((s, e) => _text.SelectAll()) { Text = "Select all" };
            var clear = new ButtonMenuItem((s, e) => Clear()) { Text = "Clear" };
            _text.ContextMenu = new ContextMenu(copy, paste, new SeparatorMenuItem(), selectAll, clear);
            EditorTheme.Apply(editor, this, _text);
            Content = _text;
        }

        /// <summary>Enter: the input, without the prompt. The view prompts again only
        /// when the owner calls <see cref="Prompt"/>.</summary>
        public event Action<string> Submitted;

        /// <summary>Ctrl+C with nothing selected.</summary>
        public event Action Interrupted;

        /// <summary>What Tab inserts in the input (Python: four spaces); null: nothing.</summary>
        public string TabText { get; set; }

        /// <summary>Tab completes the input (V°Claude's / commands): the input completed, and lines
        /// to show above the prompt (the choices, when it can't go further); a null input: not
        /// completed, Tab does what <see cref="TabText"/> says.</summary>
        public Func<string, (string Input, IReadOnlyList<string> Choices)> Complete { get; set; }

        /// <summary>Shift+Enter starts a new line in the input.</summary>
        public bool MultilineInput { get; set; }

        public bool IsPrompting => _prompt != null;

        /// <summary>The input's language, colored while typed ("python", "powershell"); null: plain.</summary>
        public string InputLanguage { get; set; }

        /// <summary>Whether the input is colored now (the owner's option).</summary>
        public Func<bool> InputColorsOn { get; set; } = () => false;

        /// <summary>Whether fenced code blocks in the output are colored now (V Claude's option); null: never.</summary>
        public Func<bool> ColorCodeBlocks { get; set; }

        // --- font size ---------------------------------------------------------------------

        /// <summary>The font size setting changed: every terminal follows (UI thread).</summary>
        public static void ApplyFontSizeAll()
        {
            lock (s_all)
            {
                s_all.RemoveAll(r => !r.TryGetTarget(out _));
                foreach (var reference in s_all)
                {
                    if (reference.TryGetTarget(out var view))
                        view._text.Font = Fonts.Monospace(MiniVivisectionSettings.TerminalFontSize);
                }
            }
        }

        /// <summary>The coloring options changed: every terminal colors (or un-colors) its input again.</summary>
        public static void ApplyColorsAll()
        {
            lock (s_all)
            {
                foreach (var reference in s_all)
                {
                    if (reference.TryGetTarget(out var view))
                    {
                        view.ColorInput();
                        if (view.ColorCodeBlocks?.Invoke() == true)
                            view.ColorFences();
                    }
                }
            }
        }

        private void OnMouseWheel(object sender, MouseEventArgs e)
        {
            if ((e.Modifiers & Keys.Control) == 0)
                return;
            e.Handled = true;
            var size = MiniVivisectionSettings.TerminalFontSize + (e.Delta.Height > 0 ? 1 : -1);
            MiniVivisectionSettings.Set("TerminalFontSize", Math.Max(6, Math.Min(36, size))); // SettingEffects: every terminal
        }

        // --- colors ------------------------------------------------------------------------

        /// <summary>The text: a rich text box always ends with a paragraph break of its own,
        /// which isn't part of it (positions count line breaks as one character).</summary>
        internal string TextNow
        {
            get
            {
                var text = _text.Text ?? "";
                return text.EndsWith("\n") ? text.Substring(0, text.Length - 1) : text;
            }
        }

        private bool Dark => SyntaxColors.IsDark(_text.BackgroundColor);

        private void SetColor(int start, int length, Color color)
        {
            if (length <= 0)
                return;
            _coloring = true;
            try
            {
                _text.Buffer.SetForeground(Eto.Forms.Range.FromLength(start, length), color);
            }
            finally
            {
                _coloring = false;
            }
        }

        /// <summary><paramref name="code"/> at <paramref name="start"/>, in <paramref name="language"/>'s colors (plain first).</summary>
        private void ColorCode(int start, string code, string language)
        {
            SetColor(start, code.Length, _text.TextColor);
            foreach (var (tokenStart, length, kind) in SyntaxColors.Tokens(code, language))
                SetColor(start + tokenStart, length, SyntaxColors.ColorOf(kind, Dark));
            _colored = true;
        }

        /// <summary>The input, colored as typed (when its option is on), else plain.</summary>
        private void ColorInput()
        {
            if (_coloring || _prompt == null || InputLanguage == null)
                return;
            try
            {
                var input = Input;
                if (input.Length == 0)
                    return;
                if (InputColorsOn())
                    ColorCode(_inputStart, input, InputLanguage);
                else if (_colored)
                    SetColor(_inputStart, input.Length, _text.TextColor);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "terminal colors: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Complete code blocks in the output since the last ones, colored.</summary>
        private void ColorFences()
        {
            try
            {
                var text = TextNow;
                var (blocks, next) = SyntaxColors.Fences(text, _fenceScan);
                foreach (var (start, length, language) in blocks)
                {
                    if (SyntaxColors.Normalize(language) != null)
                        ColorCode(start, text.Substring(start, length), language);
                }
                _fenceScan = next;
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "terminal code blocks: {0}", Log.Describe(ex));
            }
        }

        /// <summary>Text just written: plain (it would take the color of what's before it), then its code blocks.</summary>
        private void AfterWrite(int start, int length)
        {
            if (_colored)
                SetColor(start, length, _text.TextColor);
            if (ColorCodeBlocks?.Invoke() == true)
                ColorFences();
        }

        /// <summary>The text typed after the prompt.</summary>
        public string Input
        {
            get
            {
                var text = TextNow;
                return _prompt == null || _inputStart > text.Length ? "" : text.Substring(_inputStart);
            }
            set
            {
                if (_prompt == null)
                    return;
                Replace(_inputStart, TextNow.Length, value ?? "");
                SetCaret(TextNow.Length);
            }
        }

        /// <summary>Output. While prompting, it goes above the prompt and the input stays.</summary>
        public void Write(string output)
        {
            if (string.IsNullOrEmpty(output))
                return;
            output = output.Replace("\r\n", "\n");
            if (_prompt == null)
            {
                var end = TextNow.Length;
                _text.Append(output, true);
                AfterWrite(end, output.Length);
                return;
            }
            var promptStart = _inputStart - _prompt.Length;
            var caret = _text.CaretIndex - _inputStart;
            if (!output.EndsWith("\n"))
                output += "\n";
            Replace(promptStart, promptStart, output);
            _inputStart += output.Length;
            AfterWrite(promptStart, output.Length);
            SetCaret(caret >= 0 ? _inputStart + caret : TextNow.Length);
        }

        /// <summary>Prints <paramref name="prompt"/> on a line of its own and takes input after it.</summary>
        public void Prompt(string prompt)
        {
            var text = TextNow;
            var shown = (text.Length > 0 && !text.EndsWith("\n") ? "\n" : "") + prompt;
            _text.Append(shown, true);
            AfterWrite(text.Length, shown.Length);
            _prompt = prompt;
            _inputStart = TextNow.Length;
            SetCaret(_inputStart);
        }

        /// <summary>Empties the view; a shown prompt and its input stay.</summary>
        public void Clear()
        {
            var input = Input;
            var prompt = _prompt;
            _text.Text = "";
            _fenceScan = 0;
            _prompt = null;
            if (prompt != null)
            {
                Prompt(prompt);
                Input = input;
            }
        }

        public void FocusInput()
        {
            _text.Focus();
            if (_prompt != null && _text.CaretIndex < _inputStart)
                SetCaret(TextNow.Length);
        }

        /// <summary>For the screenshots and tests: <paramref name="input"/> typed at the prompt and Enter.</summary>
        internal void Type(string input)
        {
            Input = input;
            Submit();
        }

        private void Submit()
        {
            var input = Input;
            _text.Append("\n", true);
            _prompt = null; // until the owner prompts again
            if (input.Trim().Length > 0)
            {
                _history.Remove(input);
                _history.Add(input);
            }
            _historyIndex = _history.Count;
            Submitted?.Invoke(input);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyData == (Keys.Control | Keys.C))
            {
                if (string.IsNullOrEmpty(_text.SelectedText))
                {
                    e.Handled = true;
                    Interrupted?.Invoke();
                }
                return; // copies
            }
            if (e.KeyData == (Keys.Control | Keys.A) || e.IsChar)
                return; // characters: OnTextInput
            if (_prompt == null)
            {
                if (IsEditingKey(e.KeyData))
                    e.Handled = true;
                return;
            }
            var selection = _text.Selection;
            var hasSelection = selection.Length() > 0;
            var caret = _text.CaretIndex;
            switch (e.KeyData)
            {
                case Keys.Enter:
                    e.Handled = true;
                    Submit();
                    break;
                case Keys.Shift | Keys.Enter:
                    e.Handled = true;
                    if (MultilineInput)
                        TypeAtCaret("\n");
                    break;
                case Keys.Tab:
                    e.Handled = true;
                    if (TryComplete())
                        break;
                    if (TabText != null)
                        TypeAtCaret(TabText);
                    break;
                case Keys.Up:
                case Keys.Down:
                    if (!MultilineInput || !Input.Contains("\n"))
                    {
                        e.Handled = true;
                        Recall(e.KeyData == Keys.Up ? -1 : +1);
                    }
                    break;
                case Keys.Escape:
                    e.Handled = true;
                    Input = "";
                    break;
                case Keys.Home:
                    if (caret >= _inputStart)
                    {
                        e.Handled = true;
                        SetCaret(_inputStart);
                    }
                    break;
                case Keys.Backspace:
                    if (hasSelection ? selection.Start < _inputStart : caret <= _inputStart)
                    {
                        e.Handled = true;
                        DeleteSelectionInInput(selection, hasSelection);
                    }
                    break;
                case Keys.Delete:
                    if (hasSelection ? selection.Start < _inputStart : caret < _inputStart)
                    {
                        e.Handled = true;
                        DeleteSelectionInInput(selection, hasSelection);
                    }
                    break;
                case Keys.Left:
                    if (!hasSelection && caret <= _inputStart)
                        e.Handled = true;
                    break;
                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert:
                    e.Handled = true;
                    Paste();
                    break;
                case Keys.Control | Keys.X:
                    if (hasSelection && selection.Start < _inputStart)
                    {
                        e.Handled = true; // copy, but don't cut the output
                        Clipboard.Instance.Text = _text.SelectedText ?? "";
                    }
                    break;
            }
        }

        // Tab with the caret at the input's end: completed (Complete), the choices shown above.
        private bool TryComplete()
        {
            if (Complete == null || _text.CaretIndex != TextNow.Length)
                return false;
            var input = Input;
            var (completed, choices) = Complete(input);
            if (completed == null)
                return false;
            if (completed != input)
                Input = completed;
            if (choices != null && choices.Count > 0)
                Write(string.Join("\n", choices));
            return true;
        }

        // Typed characters go into the input, wherever the caret was.
        private void OnTextInput(object sender, TextInputEventArgs e)
        {
            if (_prompt == null)
            {
                e.Cancel = true;
                return;
            }
            var selection = _text.Selection;
            if (_text.CaretIndex < _inputStart || (selection.Length() > 0 && selection.Start < _inputStart))
            {
                e.Cancel = true;
                SetCaret(TextNow.Length);
                TypeAtCaret(e.Text);
            }
        }

        private void Paste()
        {
            if (_prompt == null || !Clipboard.Instance.ContainsText)
                return;
            var text = Clipboard.Instance.Text.Replace("\r\n", "\n");
            if (!MultilineInput)
                text = text.Replace("\n", " ");
            var selection = _text.Selection;
            if (_text.CaretIndex < _inputStart || (selection.Length() > 0 && selection.Start < _inputStart))
                SetCaret(TextNow.Length);
            TypeAtCaret(text);
        }

        /// <summary>Inserts at the caret, replacing a selection (both within the input).</summary>
        private void TypeAtCaret(string text)
        {
            var selection = _text.Selection;
            int start, end;
            if (selection.Length() > 0 && selection.Start >= _inputStart)
            {
                start = selection.Start;
                end = selection.End + 1;
            }
            else
            {
                start = end = Math.Max(_inputStart, _text.CaretIndex);
            }
            Replace(start, end, text);
            SetCaret(start + text.Length);
        }

        private void DeleteSelectionInInput(Range<int> selection, bool hasSelection)
        {
            if (!hasSelection || selection.End + 1 <= _inputStart)
                return;
            Replace(_inputStart, selection.End + 1, "");
            SetCaret(_inputStart);
        }

        private void Recall(int step)
        {
            if (_history.Count == 0)
                return;
            _historyIndex = Math.Max(0, Math.Min(_history.Count, _historyIndex + step));
            Input = _historyIndex < _history.Count ? _history[_historyIndex] : "";
        }

        /// <summary>Replaces the text from <paramref name="start"/> up to <paramref name="end"/>.</summary>
        private void Replace(int start, int end, string text)
        {
            var current = TextNow;
            if (start == end && start > 0 && text.Length > 0 && current[start - 1] == '\n')
            {
                // An empty selection at a line's start lands at the end of the line before
                // (rich text box). At the end: appended; else the line's first character is
                // replaced by the text plus itself -- a line break isn't replaced, the text
                // goes in before it.
                if (start >= current.Length)
                {
                    _text.Append(text, true);
                    return;
                }
                _text.Selection = Eto.Forms.Range.FromLength(start, 1);
                _text.SelectedText = current[start] == '\n' ? text : text + current[start];
                return;
            }
            _text.Selection = Eto.Forms.Range.FromLength(start, Math.Max(0, end - start));
            _text.SelectedText = text;
        }

        /// <summary>Places the caret. At a line's start the rich text box puts it at the end
        /// of the line before: placed there, then moved one step on in WPF's own terms.</summary>
        private void SetCaret(int index)
        {
            var text = TextNow;
            index = Math.Max(0, Math.Min(text.Length, index));
            if (index == 0 || text[index - 1] != '\n')
            {
                _text.CaretIndex = index;
                return;
            }
            _text.CaretIndex = index - 1;
            try
            {
                var box = _text.ControlObject;
                var caretProperty = box.GetType().GetProperty("CaretPosition");
                var caret = caretProperty?.GetValue(box);
                if (caret == null)
                    return;
                var direction = caret.GetType().Assembly.GetType("System.Windows.Documents.LogicalDirection");
                var next = caret.GetType().GetMethod("GetNextInsertionPosition", new[] { direction })
                    ?.Invoke(caret, new[] { Enum.Parse(direction, "Forward") });
                if (next != null)
                    caretProperty.SetValue(box, next);
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "terminal caret: {0}", Log.Describe(ex));
            }
        }

        private static bool IsEditingKey(Keys keys)
        {
            var key = keys & Keys.KeyMask;
            return key == Keys.Enter || key == Keys.Backspace || key == Keys.Delete || key == Keys.Tab ||
                   keys == (Keys.Control | Keys.V) || keys == (Keys.Control | Keys.X) || keys == (Keys.Shift | Keys.Insert);
        }
    }
}
