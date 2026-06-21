using System.IO;
using System.Windows;
using System.Windows.Threading;
using VideoMetadataEditor.Services;
using VideoMetadataEditor.Views;
using AppSplashScreen = VideoMetadataEditor.Views.SplashScreen;

namespace VideoMetadataEditor;

public partial class App : System.Windows.Application
{
    public static ConfigService ConfigService { get; private set; } = null!;
    public static bool IsDarkTheme { get; private set; } = true;

    private AppSplashScreen? _splash;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep error logging - it saved us once, keep it forever
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            LogError("UnhandledException", ex.ExceptionObject?.ToString());

        // Catches fire-and-forget task exceptions (e.g. _ = SaveAsync() that throws).
        // In .NET 8 these are swallowed silently without this handler.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
        {
            LogError("UnobservedTaskException", ex.Exception?.ToString());
            ex.SetObserved(); // prevent process termination on older runtimes
        };

        DispatcherUnhandledException += (s, ex) =>
        {
            LogError("DispatcherUnhandledException", ex.Exception?.ToString());
            ex.Handled = true;
        };

        // Route safely-wrapped background-task errors to the same log file.
        Services.BackgroundTask.OnError = (context, ex) =>
            LogError(context, ex.ToString());

        try
        {
            base.OnStartup(e);

            // Install the WPF-backed command requery provider before any ViewModel (and
            // thus any RelayCommand) is constructed. This preserves the automatic
            // enable/disable behaviour the UI relies on, now routed through the
            // platform-neutral CommandRequery seam.
            ViewModels.CommandRequery.Provider = new ViewModels.WpfCommandRequery();

            ConfigService = new ConfigService();
            ConfigService.Load();
            IsDarkTheme = ConfigService.Settings.IsDarkTheme;

            if (!IsDarkTheme)
                ApplyTheme(false);

            // Validate Trakt token health 8 seconds after startup
            // so the main window is fully loaded before the background check runs
            Services.BackgroundTask.RunDelayed(8000, async () =>
            {
                // MainWindow is a UI-thread-only property — read it (and its
                // DataContext) on the dispatcher, then continue on the pool.
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null) return;

                var vm = await dispatcher.InvokeAsync(() =>
                    System.Windows.Application.Current?.MainWindow?.DataContext
                        as VideoMetadataEditor.ViewModels.MainViewModel);

                if (vm?.Settings?.TraktConnected == true)
                    await vm.EnsureTraktTokenValidAsync().ConfigureAwait(false);
            }, "Startup Trakt check");

            // Detect MKVToolNix (mkvpropedit) for MKV artwork embedding
            Services.MkvPropEditService.Detect();
            Services.NativeLibraryExtractor.EnsureExtracted();

            // Inject language strings AFTER theme so Clear() doesn't wipe them
            var savedLang = ConfigService.Settings.LanguageCode ?? "System";
            LanguageService.ApplyOnStartup(savedLang);

            // Show splash if enabled - purely synchronous, timer-driven close
            if (ConfigService.Settings.ShowSplashScreen)
            {
                _splash = new AppSplashScreen();
                _splash.Show();
            }

            // Create and show main window immediately - no waiting, no async
            var main = new MainWindow();
            MainWindow = main;
            main.Show();

            // Close splash 2 seconds after main window is fully rendered
            if (_splash != null)
            {
                main.ContentRendered += (_, _) =>
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        _splash?.Close();
                        _splash = null;
                    };
                    timer.Start();
                };
            }
        }
        catch (Exception ex)
        {
            LogError("OnStartup", ex.ToString());
            MessageBox.Show(
                $"Startup error:\n\n{ex.Message}\n\nCheck error.log next to the EXE.",
                "Video Metadata Editor - Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void LogError(string context, string? message)
    {
        try
        {
            var log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
            File.AppendAllText(log,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}]\n{message}\n\n");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] {ex.GetType().Name}: {ex.Message}"); }
    }

    public static void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        ApplyTheme(IsDarkTheme);
        ConfigService.Settings.IsDarkTheme = IsDarkTheme;
        _ = ConfigService.SaveAsync();
    }

    public static void ApplyTheme(bool dark)
    {
        var dict = new ResourceDictionary
        {
            Source = new Uri(
                dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml",
                UriKind.Relative)
        };
        var common = new ResourceDictionary
        {
            Source = new Uri("Themes/CommonStyles.xaml", UriKind.Relative)
        };
        // Preserve language dicts before clearing (tagged with __tag key)
        var langDicts = Current.Resources.MergedDictionaries
            .Where(d => d.Contains("__tag"))
            .ToList();

        Current.Resources.MergedDictionaries.Clear();
        Current.Resources.MergedDictionaries.Add(dict);
        Current.Resources.MergedDictionaries.Add(common);

        // Re-inject language strings after theme (theme Clear() would have wiped them)
        foreach (var ld in langDicts)
            Current.Resources.MergedDictionaries.Add(ld);

        // If no language was previously injected (first theme apply before lang init),
        // inject now using saved setting
        if (!langDicts.Any() && ConfigService != null)
        {
            var code = ConfigService.Settings.LanguageCode ?? "System";
            LanguageService.ApplyOnStartup(code);
        }
    }
}
