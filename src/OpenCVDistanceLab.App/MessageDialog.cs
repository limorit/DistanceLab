using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace OpenCVDistanceLab.App;

internal static class MessageDialog
{
    internal static async Task Show(Window owner, string title, string message)
    {
        Window dialog = Create(title, message, false);
        await dialog.ShowDialog(owner);
    }

    internal static async Task<bool> Confirm(Window owner, string title, string message)
    {
        Window dialog = Create(title, message, true);
        return await dialog.ShowDialog<bool>(owner);
    }

    private static Window Create(string title, string message, bool confirmation)
    {
        Window dialog = new()
        {
            Title = title,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        App.ApplyUnifiedWindowStyle(dialog);
        TextBlock body = new() { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 7
        };
        if (confirmation)
        {
            Button cancel = new() { Content = "取消", MinWidth = 80, IsCancel = true };
            cancel.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(cancel);
        }
        Button accept = new()
        {
            Content = confirmation ? "确定" : "关闭",
            MinWidth = 80,
            IsDefault = true
        };
        accept.Classes.Add("primary");
        accept.Click += (_, _) => dialog.Close(confirmation);
        buttons.Children.Add(accept);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 20,
            Children = { body, buttons }
        };
        return dialog;
    }
}
