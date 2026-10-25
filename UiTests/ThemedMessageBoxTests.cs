using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QsrPriceBenchmarks.Ui.Dialogs;
using Xunit;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// What the app's message box must do as a dialog, whatever it looks like: offer
/// exactly the answers asked for, make Enter the affirmative one, and make Esc
/// or closing the window the cancelling one — so dismissing a delete
/// confirmation can never mean Yes. Built on the shared WPF thread (with the
/// theme) and driven by raising Click, so no window is shown.
/// </summary>
public sealed class ThemedMessageBoxTests
{
    private sealed record Answers(string[] Labels, MessageBoxResult Default, MessageBoxResult Cancelling, MessageBoxResult Unanswered);

    private static Answers Inspect(MessageBoxButton button, MessageBoxImage icon)
    {
        Answers? answers = null;
        WpfThread.Run(() =>
        {
            ThemedMessageBox dialog = new("Delete run 3 and its 42 snapshots?", "Confirm Delete", button, icon);
            Button[] buttons = Buttons(dialog);
            answers = new Answers(
                [.. buttons.Select(b => b.Content as string ?? "")],
                Single(buttons, b => b.IsDefault),
                Single(buttons, b => b.IsCancel),
                dialog.Result);
            dialog.Close();
        });
        return answers!;
    }

    private static Button[] Buttons(ThemedMessageBox dialog)
    {
        StackPanel row = dialog.FindName("ButtonRow") as StackPanel
            ?? throw new InvalidOperationException("ThemedMessageBox has no ButtonRow.");
        return [.. row.Children.OfType<Button>()];
    }

    private static MessageBoxResult Single(Button[] buttons, Func<Button, bool> which)
    {
        Button button = Assert.Single(buttons, b => which(b));
        return button.Tag is MessageBoxResult result ? result : throw new InvalidOperationException("untagged button");
    }

    [Theory(DisplayName = "Each button set offers exactly its answers, Enter = the first, Esc = the cancelling one")]
    [InlineData(MessageBoxButton.OK, new[] { "OK" }, MessageBoxResult.OK, MessageBoxResult.OK)]
    [InlineData(MessageBoxButton.OKCancel, new[] { "OK", "Cancel" }, MessageBoxResult.OK, MessageBoxResult.Cancel)]
    [InlineData(MessageBoxButton.YesNo, new[] { "Yes", "No" }, MessageBoxResult.Yes, MessageBoxResult.No)]
    [InlineData(MessageBoxButton.YesNoCancel, new[] { "Yes", "No", "Cancel" }, MessageBoxResult.Yes, MessageBoxResult.Cancel)]
    public void Buttons_DefaultAndCancel(MessageBoxButton button, string[] labels, MessageBoxResult enter, MessageBoxResult esc)
    {
        Answers answers = Inspect(button, MessageBoxImage.Warning);

        Assert.Equal(labels, answers.Labels);
        Assert.Equal(enter, answers.Default);
        Assert.Equal(esc, answers.Cancelling);
        // Closing the window without pressing anything (Alt+F4) gives the same as Esc.
        Assert.Equal(esc, answers.Unanswered);
    }

    [Fact(DisplayName = "Pressing a button closes the dialog with that button's answer")]
    public void Click_ReturnsThatAnswer()
    {
        MessageBoxResult yes = MessageBoxResult.None;
        MessageBoxResult no = MessageBoxResult.None;
        WpfThread.Run(() =>
        {
            yes = Press(MessageBoxButton.YesNo, "Yes");
            no = Press(MessageBoxButton.YesNo, "No");
        });

        Assert.Equal(MessageBoxResult.Yes, yes);
        Assert.Equal(MessageBoxResult.No, no);
    }

    private static MessageBoxResult Press(MessageBoxButton button, string label)
    {
        ThemedMessageBox dialog = new("Delete run 3?", "Confirm Delete", button, MessageBoxImage.Warning);
        Button target = Buttons(dialog).Single(b => (b.Content as string) == label);
        target.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        return dialog.Result;
    }
}
