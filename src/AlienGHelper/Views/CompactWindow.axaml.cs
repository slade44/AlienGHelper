using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TccG15.Controls;
using TccG15.Core;
using TccG15.Core.Power;
using TccG15.Core.Sensors;

namespace TccG15.Views;

/// <summary>
/// Interface compacta, no estilo do G-Helper: modo térmico, modo de energia do Windows, um resumo dos
/// sensores e a iluminação do teclado numa coluna só. Curvas de fan, limites de energia, perfis e
/// preferências abrem na janela completa (<see cref="MainWindow"/>), que continua dona dos serviços.
/// </summary>
public partial class CompactWindow : Window
{
    private readonly AppHost _host = null!;
    private readonly AppController _controller = null!;
    private readonly Action _exit = null!;
    private readonly DispatcherTimer _sensorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _sensorBusy;
    private bool _exiting;
    private bool _sync;          // atualização por código: os handlers não reagem
    private bool _autorunBusy;

    /// <summary>Só para o designer do Avalonia.</summary>
    public CompactWindow() => InitializeComponent();

    public CompactWindow(AppHost host, Action exit)
    {
        InitializeComponent();
        _host = host;
        _controller = host.Controller;
        var controller = host.Controller;
        _exit = exit;
        AppTheme.Register(this);
        AppTheme.Changed += RefreshKeyboard;

        foreach (var e in RgbSettings.EffectNames) KbEffect.Items.Add(e);
        KbEffect.SelectionChanged += (_, _) =>
        {
            if (!_sync && KbEffect.SelectedItem is string fx) _host.Keyboard.Page.QuickSetEffect(fx);
        };
        KbOn.OnCheckedChanged(() => { if (!_sync) _host.Keyboard.Page.QuickSetOn(KbOn.IsChecked == true); });
        KbBright.OnValueChanged(() =>
        {
            KbBrightVal.Text = $"{(int)KbBright.Value} %";
            if (!_sync) _host.Keyboard.Page.QuickSetBrightness((int)KbBright.Value);
        });
        KbColor.Flyout = BuildColorFlyout();
        _host.Keyboard.Page.QuickStateChanged += () => Dispatcher.UIThread.Post(RefreshKeyboard);

        RunOnStartup.OnCheckedChanged(RunOnStartup_Changed);

        controller.Updated += snap => Dispatcher.UIThread.Post(() => ApplySnapshot(snap));

        _sensorTimer.Tick += (_, _) => PollSensors();
        InitBattery();
        host.Power.Changed += () => Dispatcher.UIThread.Post(RefreshPower);
        Opened += (_, _) => RefreshAll();
    }

    // ---------- chamadas do App ----------

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        RefreshAll();
    }

    public void MarkExiting()
    {
        _exiting = true;
        _sensorTimer.Stop();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_exiting && _controller.Settings.MinimizeOnClose)
        {
            e.Cancel = true;
            Hide();
            _sensorTimer.Stop();
            _host.TrimSoon();
            if (!_controller.Settings.TrayHintShown)
            {
                // só na primeira vez: avisa que o app continua rodando na bandeja
                _controller.Settings.TrayHintShown = true;
                _host.Tray.ShowBalloon("Alien G-Helper is still running",
                    "It keeps controlling the fans, power and lighting from the tray. " +
                    "Double-click the icon to open it, or right-click › Exit to close it.", false);
            }
            return;
        }
        if (!_exiting)
        {
            e.Cancel = true;   // deixa o App encerrar de forma ordenada
            _exit();
            return;
        }
        base.OnClosing(e);
    }

    private void RefreshAll()
    {
        RefreshPower();
        ReadBattery();
        UpdateBatteryLevel();
        RefreshKeyboard();
        RefreshAutorun();
        _sensorTimer.Start();
        PollSensors();
    }

    // ---------- modo térmico e fans ----------

    private void ApplySnapshot(Snapshot s)
    {
        ModeTitle.Text = s.ProfileName is null
            ? $"Fan Control: {s.Mode.DisplayName()}"
            : $"Fan Control: {s.Mode.DisplayName()} · {s.ProfileName}";
        CpuTempText.Text = Temp(s.CpuTemp);
        GpuTempText.Text = Temp(s.GpuTemp);
        CpuTempText.Foreground = TempBrush(s.CpuTemp, 85, 95, primary: true);
        GpuTempText.Foreground = TempBrush(s.GpuTemp, 72, 85, primary: true);
        FanStat.Text = $"CPU {Rpm(s.CpuRpm)} · GPU {Rpm(s.GpuRpm)}";

        BtnSilent.SetActive(s.Mode == ThermalMode.Quiet);
        BtnBalanced.SetActive(s.Mode == ThermalMode.Balanced);
        BtnTurbo.SetActive(s.Mode == ThermalMode.GMode);
        BtnFans.SetActive(s.Mode == ThermalMode.Custom);   // Manual = curvas próprias, como o "Fans + Power" do G-Helper

        ProfileBadge.IsVisible = s.ProfileName is not null;
        ProfileText.Text = s.ProfileName is null ? "" : $"Profile: {s.ProfileName}";
        FailSafeText.IsVisible = s.ProfileName is null;
        FailSafeText.Text = !s.FailSafeEnabled ? "Fail-safe off"
            : s.FailSafeTripped ? "Fail-safe tripped – Turbo"
            : "Fail-safe on";
    }

    private static string Temp(int? t) => t is int v ? $"{v} °C" : "-- °C";

    private static string Rpm(int? r) => r is int v ? $"{v.ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = "." })} RPM" : "-- RPM";

    private IBrush TempBrush(int? t, int warn, int crit, bool primary = false)
    {
        if (t is int v && v >= crit) return new SolidColorBrush(Color.FromRgb(0xF0, 0x6A, 0x6A));
        if (t is int w && w >= warn) return new SolidColorBrush(Color.FromRgb(0xF0, 0xC0, 0x4C));
        return this.FindResource(primary ? "TextPrimaryBrush" : "TextSecondaryBrush") as IBrush ?? Brushes.Gray;
    }

    private void Mode_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control { Tag: string tag }) return;
        if (tag == "Fans")
        {
            _host.ShowFans("Cpu");   // curvas, "Apply Custom Fan Curve" (modo Manual) e limites de energia
            return;
        }
        var mode = tag switch
        {
            "Quiet" => ThermalMode.Quiet,
            "GMode" => ThermalMode.GMode,
            _ => ThermalMode.Balanced,
        };
        Task.Run(() => _controller.SetMode(mode));
    }

    // ---------- limite de carga da bateria (BIOS Dell) ----------

    private static readonly (ChargeMode Mode, string Name, string Hint)[] ChargeModes =
    {
        (ChargeMode.Custom, "Custom", "Stops charging at the limit below: best for battery life when plugged in"),
        (ChargeMode.Adaptive, "Adaptive", "The BIOS picks the limit from how you use the laptop"),
        (ChargeMode.PrimarilyAc, "Primarily AC Use", "Lower limit for a laptop that is almost always plugged in"),
        (ChargeMode.Standard, "Standard", "Charges to 100 % at normal speed"),
        (ChargeMode.Express, "ExpressCharge", "Charges to 100 % faster (wears the battery more)"),
    };

    private readonly DispatcherTimer _chargeDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private BatteryChargeInfo? _charge;
    private bool _chargeBusy;
    private bool _chargeReady;

    private void InitBattery()
    {
        ChargeModeBox.SelectionChanged += (_, _) =>
        {
            if (_sync || !_chargeReady || SelectedChargeMode is not ChargeMode m) return;
            UpdateChargeUi();
            ApplyCharge(m);
        };
        ChargeSlider.OnValueChanged(() =>
        {
            UpdateChargeLabels();
            if (_sync || !_chargeReady) return;
            _chargeDebounce.Stop();
            _chargeDebounce.Start();
        });
        _chargeDebounce.Tick += (_, _) =>
        {
            _chargeDebounce.Stop();
            if (SelectedChargeMode == ChargeMode.Custom) ApplyCharge(ChargeMode.Custom);
        };
        ChargeLimitRow.IsVisible = false;
        // a linha de aviso só ocupa espaço quando tem texto
        BatteryHint.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) BatteryHint.IsVisible = !string.IsNullOrEmpty(BatteryHint.Text);
        };
        BatteryHint.Text = "Reading the battery settings…";
    }

    private ChargeMode? SelectedChargeMode =>
        ChargeModeBox.SelectedItem is ComboBoxItem { Tag: ChargeMode m } ? m : null;

    private int StopLimit => (int)ChargeSlider.Value;

    /// <summary>A BIOS pede início ≥ 50 % e ≥ 5 pontos abaixo do fim: recarrega quando cai 5 %.</summary>
    private int StartLimit => Math.Max(DellBattery.MinStart, StopLimit - DellBattery.MinGap);

    private void ReadBattery()
    {
        if (_chargeBusy) return;
        _chargeBusy = true;
        Task.Run(DellBattery.Read).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _chargeBusy = false;
            if (t.Status == TaskStatus.RanToCompletion) ShowCharge(t.Result);
        }));
    }

    private void ShowCharge(BatteryChargeInfo info)
    {
        _charge = info;
        _sync = true;
        if (!info.Supported)
        {
            ChargeModeBox.Items.Clear();
            ChargeModeBox.IsEnabled = false;
            ChargeLimitRow.IsVisible = false;
            BatteryTitle.Text = "Battery Charge Limit";
            BatteryHint.Text = info.Error;
            _sync = false;
            return;
        }

        ChargeModeBox.IsEnabled = true;
        ChargeModeBox.Items.Clear();
        foreach (var (mode, name, _) in ChargeModes)
            if (info.Supports(mode) || mode == info.Mode)
                ChargeModeBox.Items.Add(new ComboBoxItem { Content = name, Tag = mode });
        ChargeModeBox.SelectedItem = ChargeModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (ChargeMode)i.Tag! == info.Mode);
        if (info.Mode == ChargeMode.Custom && info.Stop is >= DellBattery.MinStop and <= DellBattery.MaxStop)
            ChargeSlider.Value = info.Stop;
        else if (ChargeSlider.Value < DellBattery.MinStop) ChargeSlider.Value = 80;
        _sync = false;
        _chargeReady = true;
        UpdateChargeUi();
    }

    private void UpdateChargeUi()
    {
        var m = SelectedChargeMode;
        ChargeLimitRow.IsVisible = m == ChargeMode.Custom;
        ChargeModeHint.Text = ChargeModes.FirstOrDefault(x => x.Mode == m).Hint ?? "";
        UpdateChargeLabels();
    }

    private void UpdateChargeLabels()
    {
        ChargeVal.Text = $"{StopLimit} %";
        var m = SelectedChargeMode;
        BatteryTitle.Text = m switch
        {
            ChargeMode.Custom => $"Battery Charge Limit: {StopLimit}%",
            ChargeMode m2 => $"Battery: {ChargeModes.First(x => x.Mode == m2).Name}",
            _ => "Battery Charge Limit",
        };
        if (m == ChargeMode.Custom)
            BatteryHint.Text = $"Charges up to {StopLimit}% and starts again below {StartLimit}%.";
        else if (_charge?.Supported == true)
            BatteryHint.Text = "";
    }

    private void ApplyCharge(ChargeMode mode)
    {
        int start = StartLimit, stop = StopLimit;
        BatteryHint.Text = "Saving…";
        Task.Run(() => DellBattery.Set(mode, start, stop)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            var (ok, msg) = t.Status == TaskStatus.RanToCompletion ? t.Result : (false, t.Exception?.GetBaseException().Message ?? "Error");
            if (!ok)
            {
                BatteryHint.Text = msg;
                ReadBattery();   // volta para o que a BIOS tem de verdade
                return;
            }
            UpdateChargeLabels();
            if (mode == ChargeMode.Custom) BatteryHint.Text = $"{msg}.";
        }));
    }

    private void UpdateBatteryLevel()
    {
        try
        {
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)) { BatteryLevel.Text = ""; return; }
            int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
            bool ac = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            bool charging = ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging);
            BatteryLevel.Text = $"{(charging ? "Charging" : ac ? "Plugged in" : "On battery")}: {pct}%";
        }
        catch { BatteryLevel.Text = ""; }
    }

    // ---------- energia da CPU (Eco / Balanced / Performance) ----------

    private void Power_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control { Tag: string tag }) return;
        if (tag == "Custom")
        {
            _host.ShowFans("Cpu");   // sliders de SPL / sPPT / fPPT, GPU e undervolt
            return;
        }
        var power = _host.Power;
        if (power.CpuState != CpuPowerState.Ready)
        {
            RefreshPower();
            return;
        }
        var preset = PowerSettings.Presets.First(x => x.Name == tag).Limits;
        power.Update(p => p with { ApplyCpu = true, Spl = preset[0], Sppt = preset[1], Fppt = preset[2] });
        _host.FansIfOpen?.PowerPageView.Reload();
        RefreshPower();
        PowerHint.IsVisible = true;
        PowerHint.Text = "Applying…";
        power.ApplyCpuAsync().ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            PowerHint.Text = power.CpuResult ?? "";
            RefreshPower();
        }));
    }

    private void RefreshPower()
    {
        var power = _host.Power;
        var p = power.Settings;
        string? active = p.ActivePreset;
        BtnEco.SetActive(active == "Eco");
        BtnPowBalanced.SetActive(active == "Balanced");
        BtnPerformance.SetActive(active == "Performance");
        BtnPowCustom.SetActive(p.ApplyCpu && active is null);

        bool ready = power.CpuState == CpuPowerState.Ready;
        PowerTitle.Text = !p.ApplyCpu ? "Power: BIOS default"
            : $"Power: {active ?? "Custom"} · {p.Spl}W";
        PowerStat.Text = power.CurrentLimits is { } l ? $"Now: {l.Stapm:0} / {l.Slow:0} / {l.Fast:0} W" : "";
        if (!ready && power.CpuState != CpuPowerState.Starting)
        {
            PowerHint.IsVisible = true;
            PowerHint.Text = $"{power.CpuDetail} CPU limits need the PawnIO driver (Fans and Power › CPU).";
        }
    }

    // ---------- sensores ----------

    private bool SensorsVisible => IsVisible && WindowState != WindowState.Minimized;

    private void PollSensors()
    {
        if (_exiting || _sensorBusy) return;
        if (!SensorsVisible) { _sensorTimer.Stop(); return; }   // não acorda a GPU dedicada à toa
        UpdateBatteryLevel();
        _sensorBusy = true;
        _host.ReadSensorsAsync().ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _sensorBusy = false;
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } s) ApplySensors(s);
        }));
    }

    private void ApplySensors(SensorSnapshot s)
    {
        static string N(double v, string fmt = "0.0") => v.ToString(fmt, CultureInfo.CurrentCulture);

        CpuUse.Text = s.CpuUsagePercent is double cu ? $"{N(cu, "0")} %" : "--";
        CpuUseBar.Value = s.CpuUsagePercent ?? 0;
        CpuMore.Text = $"{(s.CpuClockMHz is double c ? $"{c:0} MHz" : "-- MHz")} · {(s.CpuPowerW is double w ? $"{N(w)} W" : "-- W")}";

        GpuUse.Text = s.GpuName is null ? "off" : s.GpuUsagePercent is double gu ? $"{N(gu, "0")} %" : "--";
        GpuUseBar.Value = s.GpuUsagePercent ?? 0;
        GpuMore.Text = $"{(s.GpuClockMHz is double g ? $"{g:0} MHz" : "-- MHz")} · {(s.GpuPowerW is double gw ? $"{N(gw)} W" : "-- W")}";

        double? ram = s.RamTotalGB > 0 ? s.RamUsedGB / s.RamTotalGB * 100 : null;
        RamUse.Text = ram is double r ? $"{N(r, "0")} %" : "--";
        RamUseBar.Value = ram ?? 0;
        RamStat.Text = s.RamTotalGB > 0 ? $"{N(s.RamUsedGB)} / {N(s.RamTotalGB)} GB" : "-- GB";
    }

    // ---------- teclado ----------

    private void RefreshKeyboard()
    {
        var kb = _host.Keyboard.Page;
        if (!kb.IsReady) return;
        _sync = true;
        KbOn.IsChecked = kb.LightsOn;
        KbEffect.SelectedItem = kb.LightingEffect;
        KbBright.Value = kb.Brightness;
        KbBrightVal.Text = $"{kb.Brightness} %";
        KbSwatch.Background = new SolidColorBrush(kb.MainColor);
        _sync = false;

        KbEffect.IsEnabled = KbColor.IsEnabled = KbBright.IsEnabled = kb.LightsOn;
        KbHint.IsVisible = !kb.LightsOn;
        KbHint.Text = "Lights are off. Turn them on to change color, effect or brightness.";
    }

    /// <summary>Roda de cores + cores prontas, como o seletor do G-Helper.</summary>
    private Flyout BuildColorFlyout()
    {
        var wheel = new ColorWheel { Width = 180, Height = 180, HorizontalAlignment = HorizontalAlignment.Center };
        wheel.ColorPicked += c =>
        {
            KbSwatch.Background = new SolidColorBrush(c);
            _host.Keyboard.Page.QuickSetColor(c);
        };

        var presets = new WrapPanel { Width = 196, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var hex in KeyboardPage.PresetColors)
        {
            var c = Color.Parse(hex);
            var sq = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(c), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(sq, hex);
            sq.PointerReleased += (_, _) =>
            {
                wheel.SetColor(c);
                KbSwatch.Background = new SolidColorBrush(c);
                _host.Keyboard.Page.QuickSetColor(c);
            };
            presets.Children.Add(sq);
        }

        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(wheel);
        panel.Children.Add(presets);
        var flyout = new Flyout { Content = panel, Placement = PlacementMode.Bottom };
        flyout.Opening += (_, _) => { if (_host.Keyboard.Page.IsReady) wheel.SetColor(_host.Keyboard.Page.MainColor); };
        return flyout;
    }

    // ---------- rodapé ----------

    private void RefreshAutorun()
    {
        if (_autorunBusy) return;
        Task.Run(Autorun.IsEnabled).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _sync = true;
            RunOnStartup.IsChecked = t.Result;
            _sync = false;
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void RunOnStartup_Changed()
    {
        if (_sync) return;
        bool want = RunOnStartup.IsChecked == true;
        _autorunBusy = true;
        Task.Run(() => want ? Autorun.Enable() : Autorun.Disable()).ContinueWith(t => Dispatcher.UIThread.Post(async () =>
        {
            _autorunBusy = false;
            if (t.Result == 0) return;
            await Dialogs.ShowAsync(this, "Error", $"Failed to {(want ? "enable" : "disable")} autorun. Error={t.Result}");
            _sync = true;
            RunOnStartup.IsChecked = !want;
            _sync = false;
        }));
    }

    private void OpenPage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string tag }) return;
        Window w = tag switch
        {
            "Keyboard" => _host.Keyboard,
            "Monitor" => _host.Monitor,
            _ => _host.Extra,
        };
        _host.Show(w);
    }

    private void Quit_Click(object? sender, RoutedEventArgs e) => _exit();
}
