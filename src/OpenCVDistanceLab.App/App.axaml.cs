using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace OpenCVDistanceLab.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    internal static void ApplyUnifiedWindowStyle(Window window)
    {
        window.Classes.Add("unified");
        if (OperatingSystem.IsMacOS())
        {
            window.Classes.Add("macos");
            window.FontFamily = FontFamily.Default;
        }
        else if (OperatingSystem.IsWindows())
        {
            window.Classes.Add("windows");
        }
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
