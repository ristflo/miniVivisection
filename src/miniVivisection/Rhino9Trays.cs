#if RHINO9
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino.Runtime.Code.Editor;
using Rhino.Runtime.Code.Editor.Commands;

namespace MiniVivisection
{
    /// <summary>
    /// Rhino 9's console tray (bottom) and left panel, as the plug-in adds tabs to them. Both are
    /// RhinoCodeEditor <c>TrayContainer</c>s -- <c>EditorConsoles&lt;TModel&gt;</c> and
    /// <c>EditorBrowsers&lt;TModel&gt;</c>: a strip of tabs (<c>Tabs</c>) and a content panel
    /// (<c>m_container</c>). The content follows the model's <c>CurrentConsole</c> /
    /// <c>CurrentBrowser</c> (enums ConsoleTray / BrowserTray) through <c>SetCurrentTray</c>, whose
    /// switch ignores values it doesn't know. The plug-in's tabs use such values (negative: the
    /// editor resets the console by ranges above Variables): selected, the editor's own tabs show
    /// unselected and leave the content to the plug-in; an editor tab clicked puts its own back.
    /// No patch. Internal editor UI, by reflection (RhinoCodeEditor 9.2).
    /// </summary>
    internal static class Rhino9Trays
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        public const int Terminal = 0;  // ConsoleTray.Terminal
        public const int Explorer = 1;  // BrowserTray.Explorer
        public const int Examples = 4;  // BrowserTray.Examples

        /// <summary>The window's console tray ("EditorConsoles") or left panel ("EditorBrowsers"),
        /// or null.</summary>
        public static Panel Find(Window window, string generic)
        {
            var name = "RhinoCodeEditor.Components." + generic + "`1";
            foreach (var control in window.Children) // Eto: all descendants
            {
                for (var t = control.GetType(); t != null; t = t.BaseType)
                {
                    if (t.IsGenericType && t.GetGenericTypeDefinition().FullName == name)
                        return (Panel)control;
                }
            }
            return null;
        }

        public static Panel ContainerOf(Panel tray) => (Panel)FindField(tray.GetType(), "m_container").GetValue(tray);

        public static Control TabsOf(Panel tray) =>
            (Control)(tray.GetType().GetProperty("Tabs", Any) ?? throw new MissingMemberException(tray.GetType().FullName, "Tabs")).GetValue(tray);

        /// <summary>A new tab at the end of the strip, themed as the editor's (protected CreateTab).</summary>
        public static Control CreateTab(Panel tray) =>
            (Control)(tray.GetType().GetMethod("CreateTab", Any, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException(tray.GetType().FullName, "CreateTab")).Invoke(tray, null);

        /// <summary>The strip's StackLayout (TrayTabs.m_tabs), for a tab of the plug-in's own drawing.</summary>
        public static StackLayout StackOf(Control tabs) => (StackLayout)FindField(tabs.GetType(), "m_tabs").GetValue(tabs);

        /// <summary>Tabs added to a shown strip, given the strip's height: TrayTabs sets every
        /// tab's Height to its own in RecomputeLayout, which it runs only when its size
        /// changes. A tab added later had its place but no height of its own and painted
        /// nothing -- the V tabs stayed blank until the window was resized. RecomputeLayout
        /// (protected) now, and once more after the next layout pass.</summary>
        public static void FitTabs(Panel tray)
        {
            void Fit()
            {
                var tabs = TabsOf(tray);
                tabs?.GetType().GetMethod("RecomputeLayout", Any, null, Type.EmptyTypes, null)?.Invoke(tabs, null);
            }
            try
            {
                Fit();
            }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tabs, "tray tabs, fitted: {0}", Log.Describe(ex));
            }
            Later(0.5, Fit);
        }

        /// <summary><paramref name="action"/> on the UI thread after <paramref name="seconds"/>.</summary>
        private static void Later(double seconds, Action action)
        {
            var timer = new UITimer { Interval = seconds };
            timer.Elapsed += (s, e) =>
            {
                timer.Stop();
                timer.Dispose();
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Log.Debug(LogArea.Tabs, "tray tabs, fitted: {0}", Log.Describe(ex));
                }
            };
            timer.Start();
        }

        public static int GetTray(object model, string property) => Convert.ToInt32(Property(model, property).GetValue(model));

        public static void SetTray(object model, string property, int value)
        {
            var p = Property(model, property);
            p.SetValue(model, Enum.ToObject(p.PropertyType, value));
        }

        public static bool GetBool(object model, string property) => (bool)Property(model, property).GetValue(model);

        public static void SetBool(object model, string property, bool value) => Property(model, property).SetValue(model, value);

        public static void SetMember(object target, string property, object value) => Property(target, property).SetValue(target, value);

        public static void SetEnumMember(object target, string property, int value)
        {
            var p = Property(target, property);
            p.SetValue(target, Enum.ToObject(p.PropertyType, value));
        }

        /// <summary>A color of the tray's theme (RhinoCodeEditor.UI.Theme: TrayBrowserTabColor, TabHoverColor, ...), else <paramref name="fallback"/>.</summary>
        public static Color ThemeColor(object themed, string name, Color fallback)
        {
            try
            {
                var theme = themed.GetType().GetProperty("Theme", Any)?.GetValue(themed);
                return theme?.GetType().GetProperty(name, Any)?.GetValue(theme) is Color c ? c : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static PropertyInfo Property(object target, string name) =>
            target.GetType().GetProperty(name, Any) ?? throw new MissingMemberException(target.GetType().FullName, name);

        public static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }
    }

    /// <summary>
    /// One tab of the plug-in in a Rhino 9 tray: the command a <c>TrayTab</c> runs and paints by
    /// (<c>Mode</c>: its tab shows selected), as the editor's ConsoleTrayToggleCommand /
    /// BrowserTrayToggleCommand. Selected when the model's <paramref name="current"/> property
    /// holds <paramref name="value"/>; then <paramref name="show"/> puts the plug-in's content into
    /// the tray (also when the editor restores the value from its saved layout).
    /// </summary>
    internal sealed class TrayTabCommand : ToggleCommand
    {
        private readonly object _model;
        private readonly string _current; // "CurrentConsole" / "CurrentBrowser"
        private readonly string _shown;   // "ShowConsole" / "ShowBrowser"
        private readonly int _value;
        private readonly Action _show;
        private readonly Action _focus;

        public TrayTabCommand(IEditor model, string current, string shown, int value, string id, string title, Action show, Action focus)
            : base(false)
        {
            _model = model;
            _current = current;
            _shown = shown;
            _value = value;
            _show = show;
            _focus = focus;
            Id = id;              // no theme icon has it: the tab shows its title only
            Title = title;
            Description = title;  // the tab's tooltip
            CanExecute = true;    // else TrayTab disables itself
            Bind(Follow, model, current); // runs once now: a value restored from the layout shows
        }

        public int Value => _value;

        public bool IsCurrent => Rhino9Trays.GetTray(_model, _current) == _value;

        private void Follow()
        {
            try
            {
                Mode = IsCurrent;
                if (Mode)
                    _show();
            }
            catch (Exception ex)
            {
                Log.Warning(LogArea.Tabs, "Rhino 9 tab {0}: {1}", Title, Log.Describe(ex));
            }
        }

        /// <summary>This tab in front, its tray unfolded (a click, the V° menu, the tests).</summary>
        public void Select()
        {
            Rhino9Trays.SetTray(_model, _current, _value); // Follow shows it, if it changed
            _show();                                       // and if it didn't
            Rhino9Trays.SetBool(_model, _shown, true);
            _focus();
        }

        // TrayTab.OnMouseDown → Toggle → Switch(!Mode), as the editor's tabs.
        public override Task Switch(bool mode, CancellationToken token)
        {
            if (mode)
                Select();
            else if (IsCurrent)
                Rhino9Trays.SetBool(_model, _shown, !Rhino9Trays.GetBool(_model, _shown)); // fold / unfold
            return Task.CompletedTask;
        }
    }
}
#endif
