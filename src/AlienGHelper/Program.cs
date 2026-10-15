using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace TccG15;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // números com ponto decimal (11.0 GB, 16.7 ms) em qualquer idioma do Windows;
        // o resto do formato regional (datas etc.) continua o do sistema
        var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ".";
        culture.NumberFormat.PercentDecimalSeparator = ".";
        if (culture.NumberFormat.NumberGroupSeparator == ".") culture.NumberFormat.NumberGroupSeparator = " ";
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = "Segoe UI" })
            .LogToTrace();
}
