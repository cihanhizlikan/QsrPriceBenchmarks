using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QsrPriceBenchmarks.Ui.Dialogs;

/// <summary>
/// The app's message box, in the app's theme. <see cref="MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage)"/>
/// is drawn by Windows itself (the Win32 <c>MessageBox</c> API), so no WPF style
/// or brush can reach it; this window replaces it with the same call shape and
/// the same result type, so a call site changes only its class name.
///
/// <para>
/// Keyboard as in the native box: Enter presses the first button (OK / Yes).
/// Esc, and closing the window any other way, choose the cancelling answer —
/// Cancel, or No when there is no Cancel, or OK when OK is the only button — so
/// dismissing a delete confirmation never deletes. Button labels are English,
/// matching the rest of the UI (the native box follows the Windows language).
/// </para>
/// </summary>
public partial class ThemedMessageBox : Window
{
    private MessageBoxResult _result;

    public ThemedMessageBox(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        InitializeComponent();
        Title = caption;
        CaptionText.Text = caption;
        MessageText.Text = messageBoxText;
        _result = CancellingResult(button);
        ShowIcon(icon);
        AddButtons(button, icon);
    }

    /// <summary>The answer the dialog closed with; until a button is pressed, the cancelling one.</summary>
    public MessageBoxResult Result
    {
        get
        {
            return _result;
        }
    }

    /// <summary>
    /// Shows the dialog modally over the main window and returns the answer.
    /// Safe to call from any thread: it runs on the UI thread.
    /// </summary>
    public static MessageBoxResult Show(
        string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            // No WPF application means no theme to render with; the native box
            // is the only one that can still say something.
            return MessageBox.Show(messageBoxText, caption, button, icon);
        }
        if (!app.Dispatcher.CheckAccess())
        {
            return app.Dispatcher.Invoke(() => Show(messageBoxText, caption, button, icon));
        }

        ThemedMessageBox dialog = new(messageBoxText, caption, button, icon);
        Window? owner = app.MainWindow;
        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        dialog.ShowDialog();
        return dialog.Result;
    }

    /// <summary>The answer Esc or closing the window gives.</summary>
    private static MessageBoxResult CancellingResult(MessageBoxButton button)
    {
        return button switch
        {
            MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.OK,
        };
    }

    private void AddButtons(MessageBoxButton button, MessageBoxImage icon)
    {
        (string Label, MessageBoxResult Result)[] choices = button switch
        {
            MessageBoxButton.OKCancel => [("OK", MessageBoxResult.OK), ("Cancel", MessageBoxResult.Cancel)],
            MessageBoxButton.YesNo => [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No)],
            MessageBoxButton.YesNoCancel =>
                [("Yes", MessageBoxResult.Yes), ("No", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel)],
            _ => [("OK", MessageBoxResult.OK)],
        };

        // The first button is the affirmative one. Confirming something a
        // warning or error is about (a delete) gets the danger style the main
        // window's Delete buttons use; any other affirmative gets the Run style.
        bool confirmsDanger = (button is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel)
            && (icon is MessageBoxImage.Warning or MessageBoxImage.Error);
        string primaryStyle = confirmsDanger ? "ButtonDanger" : "ButtonRun";
        MessageBoxResult cancelling = CancellingResult(button);

        for (int i = 0; i < choices.Length; i++)
        {
            (string label, MessageBoxResult result) = choices[i];
            Button choice = new()
            {
                Content = label,
                Tag = result,
                MinWidth = 84,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == 0,
                IsCancel = result == cancelling,
                Style = FindResource(i == 0 ? primaryStyle : "ButtonBase") as Style,
            };
            choice.Click += Choice_Click;
            ButtonRow.Children.Add(choice);
        }
    }

    private void ShowIcon(MessageBoxImage icon)
    {
        (string Glyph, string BrushKey)? look = icon switch
        {
            MessageBoxImage.Error => ("✕", "BrushDanger"),
            MessageBoxImage.Warning => ("⚠", "BrushDanger"),
            MessageBoxImage.Information => ("ℹ", "BrushLightCyan"),
            MessageBoxImage.Question => ("?", "BrushLightCyan"),
            _ => null,
        };
        if (look is null)
        {
            IconGlyph.Visibility = Visibility.Collapsed;
            return;
        }
        IconGlyph.Text = look.Value.Glyph;
        IconGlyph.Foreground = FindResource(look.Value.BrushKey) as Brush;
    }

    private void Choice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MessageBoxResult result })
        {
            _result = result;
        }
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }
}
