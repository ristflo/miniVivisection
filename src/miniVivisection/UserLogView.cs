using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace MiniVivisection
{
    /// <summary>
    /// The "Log" page of the editor's left panel: the user log (UserLog) as a compact
    /// list -- time and text, colored by kind, newest last, following new lines.
    /// Right-click: Copy, Copy all, Clear, Open log folder.
    /// </summary>
    internal sealed class UserLogView : Panel
    {
        private readonly ObservableCollection<UserLogEntry> _entries = new ObservableCollection<UserLogEntry>(UserLog.Entries);
        private readonly GridView _grid;

        public UserLogView(object editor)
        {
            var dark = false;
            _grid = new GridView
            {
                DataStore = _entries,
                ShowHeader = false,
                AllowMultipleSelection = true,
                GridLines = GridLines.None,
                Columns =
                {
                    new GridColumn { DataCell = new TextBoxCell(nameof(UserLogEntry.TimeText)), Width = 58 },
                    new GridColumn { DataCell = new TextBoxCell(nameof(UserLogEntry.Text)), Expand = true },
                },
            };
            _grid.CellFormatting += (sender, e) =>
            {
                if (!(e.Item is UserLogEntry entry))
                    return;
                if (e.Column == _grid.Columns[0])
                    e.ForegroundColor = Colors.Gray;
                else if (entry.Kind == UserLogKind.Error)
                    e.ForegroundColor = Color.FromArgb(220, 50, 47);
                else if (entry.Kind == UserLogKind.Warning)
                    e.ForegroundColor = dark ? Color.FromArgb(240, 180, 40) : Color.FromArgb(180, 110, 0);
                else if (entry.Kind == UserLogKind.Success)
                    e.ForegroundColor = dark ? Color.FromArgb(120, 200, 120) : Color.FromArgb(30, 130, 60);
            };
            var copy = new ButtonMenuItem((s, e) => Clipboard.Instance.Text = string.Join(Environment.NewLine, _grid.SelectedItems.OfType<UserLogEntry>())) { Text = "Copy" };
            var copyAll = new ButtonMenuItem((s, e) => Clipboard.Instance.Text = string.Join(Environment.NewLine, _entries)) { Text = "Copy all" };
            var clear = new ButtonMenuItem((s, e) => UserLog.Clear()) { Text = "Clear" };
            var folder = new ButtonMenuItem((s, e) => OpenFolder()) { Text = "Open log folder" };
            _grid.ContextMenu = new ContextMenu(copy, copyAll, clear, new SeparatorMenuItem(), folder);

            UserLog.Added += OnAdded;
            UserLog.Cleared += OnCleared;
            // Static events: the closed editor's page would stay reachable, and be written to.
            EditorWindows.WhenClosed(editor, () =>
            {
                UserLog.Added -= OnAdded;
                UserLog.Cleared -= OnCleared;
            });
            EditorTheme.Apply(editor, this, _grid);
            dark = _grid.BackgroundColor.A > 0 && _grid.BackgroundColor.ToHSL().L < 0.5f;
            Content = _grid;
            ScrollToEnd();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UserLog.Added -= OnAdded;
                UserLog.Cleared -= OnCleared;
            }
            base.Dispose(disposing);
        }

        private void OnAdded(UserLogEntry entry)
        {
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                _entries.Add(entry);
                var keep = Math.Max(100, MiniVivisectionSettings.UserLogKeep);
                while (_entries.Count > keep)
                    _entries.RemoveAt(0);
                ScrollToEnd();
            }));
        }

        private void OnCleared() => RhinoApp.InvokeOnUiThread(new Action(() => _entries.Clear()));

        private void ScrollToEnd()
        {
            if (_entries.Count > 0)
                _grid.ScrollToRow(_entries.Count - 1);
        }

        private static void OpenFolder()
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
        }
    }
}
