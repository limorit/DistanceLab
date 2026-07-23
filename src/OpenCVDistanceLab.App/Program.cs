using Avalonia;
using OpenCVDistanceLab.Interop;

namespace OpenCVDistanceLab.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--native-self-test", StringComparer.Ordinal))
        {
            try
            {
                NativeBackend.SelfTest();
                Console.WriteLine("Published native self-test: OK");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
