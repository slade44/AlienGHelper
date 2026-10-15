using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AlienGHelper.Core;
using AlienGHelper.Views;

namespace AlienGHelper;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppController? _controller;
    private TrayService? _tray;
    private HotKeyService? _hotKey;
    private AppHost? _host;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            ReportCrash(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) WriteCrashLog(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception);
            e.SetObserved();
        };

        _ = StartAsync(desktop.Args ?? Array.Empty<string>());
    }

    private async Task StartAsync(string[] args)
    {
        try
        {
            await StartCoreAsync(args);
        }
        catch (Exception ex)
        {
            // Antes, um erro aqui sumia (tarefa sem await) e o processo ficava aberto sem janela
            Trace($"FAILED: {ex}");
            WriteCrashLog(ex);
            var root = ex.InnerException ?? ex;
            await Fatal("The app failed to start.",
                $"{root.GetType().Name}: {root.Message}\n\nDetails: {CrashLogPath}\nSteps: {StartupLogPath}");
        }
    }

    private async Task StartCoreAsync(string[] args)
    {
        _singleInstance = new Mutex(true, @"Global\AlienGHelper_SingleInstance", out bool created);
        if (!created)
        {
            Trace("another instance holds the mutex");
            await Dialogs.ShowAsync(null, "Oh-oh",
                "Another instance of this app is already running.\n\nIf no window or tray icon is visible, end \"AlienGHelper\" in Task Manager and open the app again.");
            _desktop?.Shutdown(1);
            return;
        }

        ResetTrace();   // só a instância que abre de fato recomeça o registro
        Trace("start");
        bool startMinimized = args.Contains("--minimized");

        try
        {
            Trace("AppController.Create");
            var create = Task.Run(AppController.Create);
            if (await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(30))) != create)
            {
                Trace("AppController.Create timed out");
                await Fatal("Starting is taking too long (no answer from the Dell/AWCC WMI interface).",
                    "Try again after a reboot. If it keeps happening, send the file:\n" + StartupLogPath);
                return;
            }
            _controller = await create;
        }
        catch (AwccException ex)
        {
            await Fatal(ex.Message, ex.Detail);
            return;
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            await Fatal("Unexpected error while starting.", ex.Message);
            return;
        }

        _controller.FatalError += (msg, detail) => Dispatcher.UIThread.Post(() => _ = Fatal(msg, detail));

        // Bandeja: o mesmo NotifyIcon com menu temático da versão WPF (o loop de mensagens do Avalonia o atende)
        Trace("tray");
        _tray = new TrayService();
        Trace("windows");
        _host = new AppHost(_controller, _tray, ExitApp);
        var main = _host.Main;
        if (_desktop is not null) _desktop.MainWindow = main;

        _tray.ShowRequested += () => Dispatcher.UIThread.Post(main.ShowFromTray);
        _tray.ExitRequested += () => Dispatcher.UIThread.Post(ExitApp);
        _tray.ModeRequested += m => Task.Run(() => _controller.SetMode(m));

        var look = _controller.Settings.Appearance;
        bool dark = look.Theme == "Dark" || (look.Theme == "System" && TrayMenuRenderer.SystemIsDark());
        var accent = AppTheme.TryParseHex(look.Accent, out var ac) ? ac : AppTheme.DefaultAccent;
        _tray.ApplyTheme(dark, accent.R, accent.G, accent.B);

        _controller.Updated += s => Dispatcher.UIThread.Post(() => _tray?.Update(s));
        _controller.Notification += (t, x, w) => Dispatcher.UIThread.Post(() => _tray?.ShowBalloon(t, x, w));

        Trace("hotkey");
        try
        {
            _hotKey = new HotKeyService(HotKeyService.GModeKey);
            _hotKey.Pressed += () => Task.Run(_controller.ToggleGMode);
        }
        catch (Exception ex)
        {
            // sem o atalho do G-Mode o app funciona; não impede a abertura
            Trace($"hotkey failed: {ex.Message}");
            WriteCrashLog(ex);
        }

        if (_desktop is not null) _desktop.ShutdownRequested += (_, _) => Cleanup();   // logoff/desligamento do Windows

        Trace("controller start");
        var start = Task.Run(_controller.Start);
        // a janela abre mesmo que algum driver demore a responder
        if (await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(8))) != start)
            Trace("controller start still running, showing window anyway");
        else
            await start;

        Trace(startMinimized ? "started minimized" : "show window");
        if (!startMinimized) main.ShowFromTray();
        else _host.TrimSoon();
        Trace("ready");

        // troca a tarefa "Run on Startup" do nome antigo (TCC_G15) pela nova, sem travar a abertura
        _ = Task.Run(Autorun.MigrateLegacy);
    }

    // ---------- registro da inicialização (%APPDATA%\AlienGHelper\startup.log) ----------

    private static string StartupLogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienGHelper", "startup.log");

    private static void ResetTrace()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StartupLogPath)!);
            System.IO.File.WriteAllText(StartupLogPath, "");
        }
        catch { }
    }

    internal static void Trace(string step)
    {
        try { System.IO.File.AppendAllText(StartupLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {step}\n"); }
        catch { }
    }

    // ---------- erros ----------

    private static string CrashLogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienGHelper", "crash.log");

    internal static void WriteCrashLog(Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CrashLogPath)!);
            System.IO.File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    private bool _crashShown;

    /// <summary>Mostra o erro em vez de fechar em silêncio, e grava em %APPDATA%\AlienGHelper\crash.log.</summary>
    private void ReportCrash(Exception ex)
    {
        WriteCrashLog(ex);
        if (_crashShown) return;
        _crashShown = true;
        var root = ex.InnerException ?? ex;
        _ = Dialogs.ShowAsync(_host?.Main, "Alien G-Helper – error",
            $"{root.GetType().Name}: {root.Message}\n\nDetails were saved to:\n{CrashLogPath}")
            .ContinueWith(_ => _crashShown = false);
    }

    public void ExitApp()
    {
        if (_exiting) return;
        Cleanup();
        _desktop?.Shutdown(0);
    }

    private void Cleanup()
    {
        if (_exiting && _controller is null) return;
        _exiting = true;
        _host?.MarkExiting();
        _controller?.Shutdown();
        _controller = null;
        _hotKey?.Dispose();
        _hotKey = null;
        _tray?.Dispose();
        _tray = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    private async Task Fatal(string message, string? detail)
    {
        if (_exiting) return;
        _exiting = true;
        Trace($"fatal: {message} {detail}");
        try { await Dialogs.ShowAsync(_host?.Main, "Oh-oh", string.IsNullOrEmpty(detail) ? message : $"{message}\n\n{detail}"); }
        catch (Exception ex) { WriteCrashLog(ex); }
        try { _controller?.Shutdown(); } catch { }
        _host?.MarkExiting();
        _hotKey?.Dispose();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        _desktop?.Shutdown(1);
    }
}
