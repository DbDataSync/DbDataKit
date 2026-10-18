using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DbDataBuild.Tui.Views;

/// <summary>Small helpers so every screen behaves the same: a centered modal box, Esc to close, a button that runs an action.</summary>
public static class Ui
{
    public static Button Button(string text, Action onPress, bool isDefault = false)
    {
        var b = new Button { Text = text, IsDefault = isDefault };
        b.Accepting += (_, e) => { onPress(); e.Handled = true; };
        return b;
    }

    public static void Message(IApplication app, string title, string text) => MessageBox.Query(app, title, text, "OK");

    public static bool Confirm(IApplication app, string title, string text, string yes = "Yes", string no = "No") => MessageBox.Query(app, title, text, yes, no) == 0;

    /// <summary>A modal screen: it fills most of the terminal, closes on Esc and remembers whether it was confirmed.</summary>
    public abstract class Modal : Window
    {
        protected Modal(string title, int widthPercent = 90, int heightPercent = 85)
        {
            Title = title;
            X = Pos.Center(); Y = Pos.Center();
            Width = Dim.Percent(widthPercent); Height = Dim.Percent(heightPercent);
        }

        public bool Confirmed { get; protected set; }

        public void Close(IApplication app, bool confirmed = false)
        {
            Confirmed = confirmed;
            app.RequestStop();
        }
    }

    /// <summary>A label whose text is shown as it is: Terminal.Gui reads `_` as the start of a hotkey, and names in this tool are full of underscores.</summary>
    public sealed class PlainLabel : Label
    {
        public PlainLabel() => HotKeySpecifier = new System.Text.Rune('\uffff');
    }

    public static void EscapeCloses(View view, IApplication app) => view.KeyDown += (_, k) => { if (k == Key.Esc) { app.RequestStop(); k.Handled = true; } };
}
