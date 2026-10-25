using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace MiniVivisection
{
    /// <summary>
    /// The plug-in's page in Rhino's Options dialog (Tools > Options > MiniVivisection):
    /// a SettingsView that writes what was changed when the dialog's OK is pressed. Rhino
    /// makes a new page each time the dialog opens.
    /// </summary>
    public class MiniVivisectionOptionsPage : OptionsDialogPage
    {
        private SettingsView _view;

        public MiniVivisectionOptionsPage() : base("miniVivisection")
        {
            // Opened for its page (miniVivisectionSettings): the page in front once the dialog has its pages
            if (ShowNext)
            {
                ShowNext = false;
                Application.Instance.AsyncInvoke(() =>
                {
                    try
                    {
                        MakeActivePage();
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(LogArea.Settings, "the Options page in front: {0}", Log.Describe(ex));
                    }
                });
            }
        }

        /// <summary>The next Options dialog opens at this page (miniVivisectionSettings).</summary>
        internal static bool ShowNext;

        /// <summary>For a test: the page was the active one last (OnActivate).</summary>
        internal static bool WasActive { get; set; }

        public override bool OnActivate(bool active)
        {
            if (active)
                WasActive = true;
            return base.OnActivate(active);
        }

        /// <summary>Rhino's Options dialog at the miniVivisection page (from a script-runner command: it runs _Options).</summary>
        internal static void Open()
        {
            ShowNext = true;
            RhinoApp.RunScript("_Options", false);
            ShowNext = false;
        }

        public override object PageControl
        {
            get
            {
                if (_view == null)
                {
                    _view = new SettingsView(immediate: false, narrow: false);
                    _view.Modified += () => Modified = true;
                }
                return _view.Control;
            }
        }

        public override bool OnApply()
        {
            _view?.ApplyAll();
            return true;
        }
    }

    /// <summary>
    /// Every setting of the plug-in, built from MiniVivisectionSettings' table: one collapsible
    /// group per area, one line per setting, its description as tooltip. Used twice:
    /// - the Options dialog's page (<c>immediate: false</c>): written on OK;
    /// - the Script Editor's left panel page "Settings" (<c>immediate: true</c>,
    ///   <c>narrow: true</c>): each change is written at once (texts when Enter is
    ///   pressed or the box is left), and the page follows changes made elsewhere -- the
    ///   AI and Debug switches, the Options page, the command.
    /// </summary>
    internal sealed class SettingsView
    {
        private readonly bool _immediate;
        private readonly bool _narrow;
        private readonly Dictionary<string, Func<object>> _values = new Dictionary<string, Func<object>>();
        private readonly Dictionary<string, Action<object>> _show = new Dictionary<string, Action<object>>();
        private bool _showing;

        public SettingsView(bool immediate, bool narrow)
        {
            _immediate = immediate;
            _narrow = narrow;
            Control = Build();
            if (_immediate)
            {
                Control.Load += (s, e) => MiniVivisectionSettings.Changed += OnSettingChanged;
                Control.UnLoad += (s, e) => MiniVivisectionSettings.Changed -= OnSettingChanged;
            }
        }

        public Control Control { get; }

        /// <summary>A value was changed here (the Options dialog marks its page modified).</summary>
        public event Action Modified;

        /// <summary>How many settings have a control.</summary>
        public int Count => _values.Count;

        /// <summary>Writes every value (the Options dialog's OK).</summary>
        public void ApplyAll()
        {
            foreach (var key in _values.Keys.ToList())
                Apply(key);
        }

        private void Apply(string key)
        {
            try
            {
                MiniVivisectionSettings.Set(key, _values[key]());
            }
            catch (Exception ex)
            {
                Log.Error(LogArea.Settings, "can't set {0}: {1}", key, Log.Describe(ex));
            }
        }

        /// <summary>A control's value changed (by the user).</summary>
        private void Changed(string key)
        {
            if (_showing)
                return;
            if (_immediate)
                Apply(key);
            else
                Modified?.Invoke();
        }

        /// <summary>Changed elsewhere: shown here (any thread).</summary>
        private void OnSettingChanged(string key)
        {
            if (!_show.ContainsKey(key))
                return;
            Application.Instance.AsyncInvoke(() =>
            {
                _showing = true;
                try
                {
                    _show[key](MiniVivisectionSettings.Get(key));
                }
                finally
                {
                    _showing = false;
                }
            });
        }

        /// <summary>One tab per settings tab (General, Rhino, Grasshopper), each a list of
        /// collapsible groups; a single tab without the tab strip.</summary>
        private Control Build()
        {
            var tabs = MiniVivisectionSettings.All.Where(s => !s.Hidden).GroupBy(s => s.Tab).ToList();
            if (tabs.Count == 1)
                return Page(tabs[0]);
            var control = new TabControl();
            foreach (var tab in tabs)
                control.Pages.Add(new TabPage { Text = tab.Key, Content = Page(tab) });
            return control;
        }

        private Control Page(IEnumerable<MiniVivisectionSettings.Setting> settings)
        {
            var layout = new DynamicLayout { Padding = new Padding(_narrow ? 4 : 6), Spacing = new Size(4, 2) };
            var first = true;
            foreach (var group in settings.GroupBy(s => s.Group))
            {
                var rows = new TableLayout { Spacing = new Size(6, 3), Padding = new Padding(_narrow ? 6 : 12, 2, 0, 6) };
                foreach (var setting in group)
                {
                    foreach (var row in Rows(setting))
                        rows.Rows.Add(row);
                }
                if (group.Key == MiniVivisectionSettings.LogGroup)
                    rows.Rows.Add(new TableRow(new TableCell(LogButtons()), null));
                layout.Add(new Expander { Header = new Label { Text = VNames.Text(group.Key), Font = SystemFonts.Bold() }, Content = rows, Expanded = first });
                first = false;
            }
            var notes = new List<string>();
            if (settings.Any(s => s.NewWindows))
                notes.Add("* for Script Editor windows opened afterwards");
            notes.Add(_immediate ? "Changes apply at once; also in Tools > Options." : VNames.Text("Changes apply when you press OK; also in the Script Editor's left panel (V Settings)."));
            layout.Add(new Label { Text = string.Join("\n", notes), TextColor = Colors.Gray, Wrap = WrapMode.Word });
            layout.Add(null);
            return new Scrollable { Content = layout, Border = BorderType.None, ExpandContentWidth = true };
        }

        /// <summary>A setting's line (wide), or its label and then its field (narrow).</summary>
        private IEnumerable<TableRow> Rows(MiniVivisectionSettings.Setting setting)
        {
            var label = setting.Label + (setting.NewWindows ? " *" : "");
            var value = MiniVivisectionSettings.Get(setting.Key);
            var key = setting.Key;
            Control field;
            switch (setting.Kind)
            {
                case MiniVivisectionSettings.Kind.Bool:
                {
                    var box = new CheckBox { Text = label, Checked = (bool)value, ToolTip = setting.Description };
                    _values[key] = () => box.Checked == true;
                    _show[key] = v => box.Checked = (bool)v;
                    box.CheckedChanged += (s, e) => Changed(key);
                    yield return new TableRow(new TableCell(box), null);
                    yield break;
                }
                case MiniVivisectionSettings.Kind.Int:
                {
                    var stepper = new NumericStepper { Value = (int)value, MinValue = 0, MaxValue = 1000000, Width = 90, ToolTip = setting.Description };
                    _values[key] = () => (int)stepper.Value;
                    _show[key] = v => stepper.Value = (int)v;
                    stepper.ValueChanged += (s, e) => Changed(key);
                    field = stepper;
                    break;
                }
                case MiniVivisectionSettings.Kind.Choice:
                {
                    var choice = new DropDown { ToolTip = setting.Description };
                    foreach (var option in setting.Choices)
                        choice.Items.Add(option);
                    choice.SelectedIndex = Math.Max(0, Array.IndexOf(setting.Choices, (string)value));
                    _values[key] = () => setting.Choices[Math.Max(0, choice.SelectedIndex)];
                    _show[key] = v => choice.SelectedIndex = Math.Max(0, Array.IndexOf(setting.Choices, (string)v));
                    choice.SelectedIndexChanged += (s, e) => Changed(key);
                    field = choice;
                    break;
                }
                case MiniVivisectionSettings.Kind.Path:
                {
                    var style = setting.StyleFile;
                    var text = TextField(key, (string)value, setting.Description, style != null ? "the default" : "automatic");
                    var browse = new Button { Text = "...", Width = 28, ToolTip = "Choose the file" };
                    browse.Click += (s, e) =>
                    {
                        var dialog = new Eto.Forms.OpenFileDialog { Title = setting.Label };
                        if (style != null)
                            dialog.Filters.Add(new FileFilter("CSS", ".css"));
                        if (dialog.ShowDialog(Control) == DialogResult.Ok)
                        {
                            text.Text = dialog.FileName;
                            Changed(key);
                        }
                    };
                    var row = new TableRow(new TableCell(text, true), browse);
                    if (style != null)
                    {
                        // The style file in a small editor (the default read-only: Save as makes one's own, then chosen here)
                        var edit = new Button { Text = "Edit", ToolTip = "The style in a small editor (the default is read-only: Save as makes your own, and this setting then points to it)" };
                        edit.Click += (s, e) =>
                        {
                            var saved = MarkdownStyleEditor.EditFile(Control.ParentWindow, style.Value, text.Text);
                            if (saved != null)
                            {
                                text.Text = saved;
                                Changed(key);
                            }
                        };
                        row.Cells.Add(edit);
                    }
                    field = new TableLayout(row) { Spacing = new Size(2, 0) };
                    break;
                }
                default:
                    field = TextField(key, (string)value, setting.Description, null);
                    break;
            }
            var caption = new Label { Text = label, ToolTip = setting.Description, VerticalAlignment = VerticalAlignment.Center };
            if (_narrow)
            {
                yield return new TableRow(caption);
                yield return new TableRow(field);
            }
            else
                yield return new TableRow(caption, field);
        }

        /// <summary>A text field: written as typed (Options dialog), or when Enter is
        /// pressed or the box is left (immediate) -- not at every key.</summary>
        private TextBox TextField(string key, string value, string description, string placeholder)
        {
            var text = new TextBox { Text = value, ToolTip = description, PlaceholderText = placeholder };
            if (!_narrow)
                text.Width = 240;
            _values[key] = () => (text.Text ?? "").Trim();
            _show[key] = v => text.Text = (string)v;
            if (_immediate)
            {
                text.LostFocus += (s, e) => Changed(key);
                text.KeyDown += (s, e) =>
                {
                    if (e.KeyData == Keys.Enter)
                    {
                        e.Handled = true;
                        Changed(key);
                    }
                };
            }
            else
                text.TextChanged += (s, e) => Changed(key);
            return text;
        }

        private static Control LogButtons()
        {
            var folder = new Button { Text = "Open log folder", ToolTip = Log.Folder };
            folder.Click += (s, e) =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Log.Folder);
                    Process.Start(new ProcessStartInfo(Log.Folder) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    UserLog.Warning("Can't open the log folder: {0}", ex.Message);
                }
            };
            var copy = new Button { Text = "Copy detailed log", ToolTip = "The last 2000 lines of the detailed log, to the clipboard" };
            copy.Click += (s, e) => Clipboard.Instance.Text = string.Join(Environment.NewLine, Log.Recent(2000));
            return new TableLayout(new TableRow(folder, copy, null)) { Spacing = new Size(4, 0) };
        }
    }
}
