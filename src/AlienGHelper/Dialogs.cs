using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TccG15;

/// <summary>Caixas de mensagem simples (o Avalonia não traz MessageBox).</summary>
public static class Dialogs
{
    public static Task ShowAsync(Window? owner, string title, string message) =>
        ShowCore(owner, title, message, yesNo: false);

    /// <summary>true = Yes.</summary>
    public static Task<bool> ConfirmAsync(Window? owner, string title, string message) =>
        ShowCore(owner, title, message, yesNo: true);

    private static async Task<bool> ShowCore(Window? owner, string title, string message, bool yesNo)
    {
        bool result = false;
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        dlg.Bind(Window.BackgroundProperty, dlg.GetResourceObservable("LayerBrush"));

        var ok = new Button { Content = yesNo ? "Yes" : "OK", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" } };
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(ok);
        if (yesNo)
        {
            var no = new Button { Content = "No", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            no.Click += (_, _) => dlg.Close();
            buttons.Children.Add(no);
        }

        var panel = new StackPanel { Margin = new Thickness(22, 20), Spacing = 18 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        dlg.Content = panel;

        if (owner is { IsVisible: true }) await dlg.ShowDialog(owner);
        else
        {
            var done = new TaskCompletionSource();
            dlg.Closed += (_, _) => done.TrySetResult();
            dlg.Show();
            await done.Task;
        }
        return result;
    }
}
