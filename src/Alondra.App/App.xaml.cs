using System.IO;
using System.Windows;
using Alondra.App.ViewModels;
using Alondra.Core.Config;

namespace Alondra.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    public static string[] Arguments { get; private set; } = [];

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        Arguments = e.Args;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "alondra", "logs");
                Directory.CreateDirectory(log);
                File.WriteAllText(Path.Combine(log, "crash.txt"), args.Exception.ToString());
            }
            catch (IOException)
            {
            }

            MessageBox.Show(args.Exception.Message, "Alondra", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown();
        };
        var home = Arguments.FirstOrDefault(argument => argument.StartsWith("--home=", StringComparison.Ordinal))?.Substring("--home=".Length);
        var demo = Arguments.Contains("--demo");
        if (demo || Arguments.Contains("--perf"))
            home = Path.Combine(Path.GetTempPath(), "alondra-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            Services = await AppServices.CreateAsync(home);
        }
        catch (ConfigFormatException ex)
        {
            MessageBox.Show(ex.Message, "Alondra", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        Services.Config.DemoMode = demo;
        // Diagnostics for demo runs: --theme=light|dark overrides the theme for this session only.
        if (demo && Arguments.FirstOrDefault(argument => argument.StartsWith("--theme=", StringComparison.Ordinal)) is { } theme
            && Enum.TryParse<ThemePreference>(theme["--theme=".Length..], ignoreCase: true, out var forced))
            Services.Config.Appearance.Theme = forced;
        ThemeService.Apply(Services.Config.Appearance);
        var window = new MainWindow(new MainViewModel(Services));
        MainWindow = window;
        window.Show();
    }
}
