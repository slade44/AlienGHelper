using Avalonia.Controls;
using Avalonia.Threading;
using AlienGHelper.Core;
using AlienGHelper.Core.Lighting;
using AlienGHelper.Core.Power;
using AlienGHelper.Core.Sensors;

namespace AlienGHelper.Views;

/// <summary>
/// Dono dos serviços e das janelas da versão compacta. Como no G-Helper, há uma janela principal
/// (<see cref="CompactWindow"/>) e janelas próprias para Fans + Power, Keyboard, Monitor e Extra.
/// A principal e a do teclado (que mantém a iluminação) são criadas no início; Fans, Monitor e Extra só
/// na primeira vez que são abertas. Fechar qualquer uma só esconde; os serviços (iluminação, perfis,
/// limites de energia) rodam aqui e funcionam com elas fechadas. Com tudo escondido, a memória é liberada.
/// </summary>
public sealed class AppHost
{
    public AppController Controller { get; }
    public TrayService Tray { get; }
    public PowerService Power { get; }
    public KeyboardLightingService Lighting { get; } = new();
    public AppProfileWatcher Profiles { get; }
    public FpsMonitor Fps { get; } = new();
    public SystemSensors? Sensors { get; private set; }

    public CompactWindow Main { get; }
    public KeyboardWindow Keyboard { get; }

    private FansWindow? _fans;
    private MonitorWindow? _monitor;
    private ExtraWindow? _extra;
    private WindowIcon? _icon;

    /// <summary>Criadas na primeira vez que são abertas (cada janela do Avalonia custa memória).</summary>
    public FansWindow Fans => _fans ??= Prepare(new FansWindow(this));
    public MonitorWindow Monitor => _monitor ??= Prepare(new MonitorWindow(this));
    public ExtraWindow Extra => _extra ??= Prepare(new ExtraWindow(this));

    /// <summary>A janela já existe (para atualizar sem criá-la).</summary>
    public FansWindow? FansIfOpen => _fans;

    public bool Exiting { get; private set; }

    /// <summary>Os sensores ficaram prontos (são criados em background).</summary>
    public event Action? SensorsReady;

    public AppHost(AppController controller, TrayService tray, Action exit)
    {
        Controller = controller;
        Tray = tray;

        Task.Run(() => new SystemSensors()).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (Exiting) { t.Result.Dispose(); return; }
            Sensors = t.Result;
            SensorsReady?.Invoke();
        }), TaskContinuationOptions.OnlyOnRanToCompletion);

        Power = new PowerService(controller);
        Profiles = new AppProfileWatcher(controller);

        // a principal usa o teclado; a aparência salva vale para todas as janelas
        Keyboard = new KeyboardWindow(this);
        AppTheme.Apply(Keyboard, controller.Settings.Appearance);
        Main = new CompactWindow(this, exit);

        try
        {
            using var ico = typeof(AppHost).Assembly.GetManifestResourceStream("AlienGHelper.app.ico");
            if (ico is not null) _icon = new WindowIcon(ico);
        }
        catch { }
        if (_icon is not null) Main.Icon = _icon;
        Prepare(Keyboard);

        Power.Start();
        Controller.Updated += _ => Dispatcher.UIThread.Post(ApplyProfilePower);

        AppTheme.Changed += () =>
        {
            Tray.ApplyTheme(AppTheme.IsDark, AppTheme.Accent.R, AppTheme.Accent.G, AppTheme.Accent.B);
            Keyboard.Page.RefreshTheme();
            _extra?.ProfilesView.RefreshTheme();
        };
    }

    /// <summary>Janela de detalhe: ícone do app e fechar só esconde (os serviços continuam rodando).</summary>
    private T Prepare<T>(T w) where T : Window
    {
        if (_icon is not null) w.Icon = _icon;
        w.Closing += (_, e) =>
        {
            if (Exiting) return;
            e.Cancel = true;
            w.Hide();
            TrimSoon();
        };
        return w;
    }

    // ---------- memória ----------

    private DispatcherTimer? _trim;

    /// <summary>
    /// Com todas as janelas escondidas (só o ícone da bandeja), devolve ao Windows a memória que a interface
    /// usou: coleta o lixo do .NET e esvazia o working set. O que for preciso volta sozinho ao abrir de novo.
    /// </summary>
    public void TrimSoon()
    {
        _trim ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _trim!.Stop();
            if (Exiting || AnyVisible) return;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1); } catch { }
        });
        _trim.Stop();
        _trim.Start();
    }

    private bool AnyVisible =>
        Main.IsVisible || Keyboard.IsVisible || _fans?.IsVisible == true || _monitor?.IsVisible == true || _extra?.IsVisible == true;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    /// <summary>Abre (ou traz para a frente) uma janela de detalhe, ao lado da janela principal.</summary>
    public void Show(Window w)
    {
        if (!w.IsVisible)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = NextTo(w);
            w.Show();
        }
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>À esquerda da janela principal, como o G-Helper; se não couber, centralizada na tela.</summary>
    private Avalonia.PixelPoint NextTo(Window w)
    {
        var screen = Main.Screens.ScreenFromWindow(Main) ?? Main.Screens.Primary;
        double scale = screen?.Scaling ?? 1;
        int width = (int)(w.Width * scale), height = (int)(w.Height * scale);
        var area = screen?.WorkingArea ?? new Avalonia.PixelRect(0, 0, 1920, 1080);
        if (Main.IsVisible)
        {
            int x = Main.Position.X - width - (int)(12 * scale);
            int y = Main.Position.Y;
            if (x >= area.X) return new Avalonia.PixelPoint(x, Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height)));
        }
        return new Avalonia.PixelPoint(area.X + Math.Max(0, (area.Width - width) / 2), area.Y + Math.Max(0, (area.Height - height) / 2));
    }

    // ---------- energia por perfil de aplicativo ----------

    private string? _profilePowerKey;
    private PowerSettings? _powerBeforeProfile;

    /// <summary>
    /// Perfil ativo com "Power" definido: aplica Eco/Balanced/Performance enquanto o app roda e, quando ele
    /// fecha (ou o perfil deixa de pedir), volta os limites que estavam antes.
    /// </summary>
    private void ApplyProfilePower()
    {
        if (Exiting) return;
        var prof = Controller.ActiveProfile;
        string? preset = prof?.PowerPreset;
        string? key = prof is null || preset is null ? null : $"{prof.Exe}|{preset}";
        if (key == _profilePowerKey) return;
        _profilePowerKey = key;

        if (key is not null)
        {
            var l = PowerSettings.Presets.First(x => x.Name == preset).Limits;
            _powerBeforeProfile ??= Power.Settings;
            Power.Update(p => p with { ApplyCpu = true, Spl = l[0], Sppt = l[1], Fppt = l[2] });
        }
        else if (_powerBeforeProfile is { } prev)
        {
            _powerBeforeProfile = null;
            Power.Update(p => p with { ApplyCpu = prev.ApplyCpu, Spl = prev.Spl, Sppt = prev.Sppt, Fppt = prev.Fppt });
        }
        else return;

        _ = Power.ApplyCpuAsync();
        _fans?.PowerPageView.Reload();
    }

    public void ShowFans(string tab = "Cpu")
    {
        Fans.SelectPowerTab(tab);
        Show(Fans);
    }

    /// <summary>Leitura avulsa dos sensores; null se ainda não estiverem prontos.</summary>
    public Task<SensorSnapshot?> ReadSensorsAsync()
    {
        var sensors = Sensors;
        if (sensors is null || Exiting) return Task.FromResult<SensorSnapshot?>(null);
        return Task.Run<SensorSnapshot?>(() =>
        {
            try { return sensors.Read(); }
            catch { return null; }
        });
    }

    public void MarkExiting()
    {
        if (Exiting) return;
        Exiting = true;
        Main.MarkExiting();
        _monitor?.MarkExiting();
        try { Fps.Dispose(); } catch { }
        try { Profiles.Dispose(); } catch { }
        try { Power.Dispose(); } catch { }
        try { Sensors?.Dispose(); } catch { }
        try { Lighting.Dispose(); } catch { }
        Sensors = null;
    }
}
