using System;
using System.Windows;
using System.Windows.Input;
using WpfBox = System.Windows.UIElement; // TextBox, RichTextBox

internal static class WpfInput
{
    static readonly PresentationSource Source = new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "keys", IntPtr.Zero);
    public static void Type(object control, string s)
    {
        var box = (WpfBox)control;
        var composition = new TextComposition(InputManager.Current, box, s);
        var preview = new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent };
        box.RaiseEvent(preview);
        if (preview.Handled) return;
        var rich = box as System.Windows.Controls.RichTextBox;
        var before = rich == null ? null : new System.Windows.Documents.TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text;
        var input = new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition) { RoutedEvent = TextCompositionManager.TextInputEvent };
        box.RaiseEvent(input);
        if (rich != null && new System.Windows.Documents.TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text == before)
        {
            // A rich text box takes a synthesized character without inserting it: as its editor would.
            rich.Selection.Text = s;
            rich.CaretPosition = rich.Selection.End;
        }
    }

    public static bool Key(object control, Key key)
    {
        var box = (WpfBox)control;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, Source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        box.RaiseEvent(args);
        if (args.Handled) return true;
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, Source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
        box.RaiseEvent(down);
        return false;
    }
}
