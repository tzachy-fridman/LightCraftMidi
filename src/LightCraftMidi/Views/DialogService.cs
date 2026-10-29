using System.Windows;
using System.Windows.Controls;

namespace LightCraftMidi.Views;

/// <summary>Minimal text prompt and message dialogs.</summary>
public sealed class DialogService(Func<Window?> owner) : ViewModels.IDialogService
{
    public string? Prompt(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 280, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Owner = owner(),
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text : null;
    }

    public void Inform(string title, string message)
    {
        var o = owner();
        if (o != null) MessageBox.Show(o, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public bool Confirm(string title, string message)
    {
        var o = owner();
        var result = o != null
            ? MessageBox.Show(o, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            : MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.OK;
    }
}
