using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenCVDistanceLab.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    internal static void ApplyPlatformWindowStyle(Window window)
    {
        if (!OperatingSystem.IsMacOS()) return;
        window.Classes.Add("macos");
        window.FontFamily = FontFamily.Default;
    }

    private async void AppAboutMenu_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: MainWindow mainWindow
            })
            await mainWindow.ShowAboutAsync();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
