using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AlienGHelper.Controls;
using AlienGHelper.Core;
using AlienGHelper.Core.Sensors;

namespace AlienGHelper.Views;

/// <summary>
/// Janela Monitor no estilo do G-Helper: fans, processador, GPU, memória, gráfico de atividade e FPS.
/// Os sensores só são lidos com a janela aberta (consultar a GPU a cada segundo pode impedir que ela durma).
/// </summary>
public partial class MonitorWindow : Window
{
    private static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x34, 0xA8, 0x53));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.FromRgb(0xF1, 0xC2, 0x32));
    private static readonly IBrush Red = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
    private static readonly (int Seconds, string Label)[] Ranges = { (60, "1 min"), (300, "5 min"), (900, "15 min") };
    private static readonly NumberFormatInfo Spaced = new() { NumberGroupSeparator = "." };   // 2.100 RPM

    private readonly AppHost _host = null!;
    private readonly AppController _controller = null!;
    private readonly DispatcherTimer _sensorTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _fpsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _sensorBusy;
    private bool _exiting;
    private bool _sync = true;
    private Snapshot? _last;
    private SensorSnapshot? _lastSensors;

    /// <summary>Só para o designer do Avalonia.</summary>
    public MonitorWindow() => InitializeComponent();

    public MonitorWindow(AppHost host)
    {
        InitializeComponent();
        _host = host;
        _controller = host.Controller;
        AppTheme.Register(this);
        var s = _controller.Settings;

        foreach (var m in ActivityChart.Metrics) MetricBox.Items.Add(m.Label);
        foreach (var r in Ranges) RangeBox.Items.Add(r.Label);
        SetActivityMetric(s.ActivityMetric);
        RangeBox.SelectedIndex = 1;
        Activity.SetRange(300);

        _sensorTimer.Tick += (_, _) => PollSensors();
        host.SensorsReady += PollSensors;
        Opened += (_, _) => { _sensorTimer.Start(); PollSensors(); };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible) { _sensorTimer.Start(); PollSensors(); }
        };

        // FPS (ETW, como o PresentMon)
        _fpsTimer.Tick += (_, _) => UpdateFps();
        ActivityToggle.IsChecked = s.ActivityEnabled;
        SetActivityEnabled(s.ActivityEnabled);
        ActivityToggle.OnCheckedChanged(() =>
        {
            if (_sync) return;
            bool on = ActivityToggle.IsChecked == true;
            _controller.Settings.ActivityEnabled = on;
            SetActivityEnabled(on);
        });

        FpsToggle.IsChecked = s.FpsMonitorEnabled;
        FpsToggle.OnCheckedChanged(() =>
        {
            if (_sync) return;
            bool on = FpsToggle.IsChecked == true;
            _controller.Settings.FpsMonitorEnabled = on;
            SetFpsEnabled(on);
        });
        // a contagem de FPS (sessão ETW) só roda com esta janela aberta
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty || _exiting) return;
            try
            {
                if (IsVisible) SetFpsEnabled(_controller.Settings.FpsMonitorEnabled);
                else { _fpsTimer.Stop(); _host.Fps.Stop(); }
            }
            catch (Exception ex) { App.WriteCrashLog(ex); }
        };

        _controller.Updated += snap => Dispatcher.UIThread.Post(() => ApplySnapshot(snap));

        // nome da CPU (WMI lento) em background
        Task.Run(() => HardwareInfo.GetCpuName()).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrEmpty(t.Result)) CpuModel.Text = t.Result;
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
        _sync = false;
    }

    public void MarkExiting()
    {
        _exiting = true;
        _sensorTimer.Stop();
        _fpsTimer.Stop();
    }

    private bool Showing => IsVisible && WindowState != WindowState.Minimized;

    // ---------- sensores ----------

    private void PollSensors()
    {
        if (_exiting || _sensorBusy) return;
        if (!Showing) { _sensorTimer.Stop(); return; }
        _sensorBusy = true;
        _host.ReadSensorsAsync().ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            _sensorBusy = false;
            if (t.Status == TaskStatus.RanToCompletion && t.Result is { } snap) ApplySensors(snap);
        }));
    }

    private static string Num(double v, int dec = 1) => v.ToString(dec == 0 ? "0" : "0.0", CultureInfo.CurrentCulture);

    private void ApplySensors(SensorSnapshot s)
    {
        _lastSensors = s;
        if (_activityOn)
        {
            Activity.AddUsage(s.CpuUsagePercent, s.GpuUsagePercent);
            Activity.AddSample(ActivityChart.Clock, s.CpuClockMHz, s.GpuClockMHz);
            Activity.AddSample(ActivityChart.Power, s.CpuPowerW, s.GpuPowerW);
        }
        UpdateActivityLegend();

        // processador
        SensCpuPower.Text = s.CpuPowerW is double cw ? $"{Num(cw)} W" : "—";
        SensCpuClock.Text = s.CpuClockMHz is double mhz ? $"{mhz:0} MHz" : "—";
        SetBar(SensCpuUsage, SensCpuUsageBar, s.CpuUsagePercent is double cu ? $"{Num(cu, 0)} %" : "—", s.CpuUsagePercent);
        double? ramPct = s.RamTotalGB > 0 ? s.RamUsedGB / s.RamTotalGB * 100 : null;
        SetBar(SensRam, SensRamBar, s.RamTotalGB > 0 ? $"{Num(s.RamUsedGB)} / {Num(s.RamTotalGB)} GB" : "—", ramPct);

        // GPU dedicada
        GpuModel.Text = s.GpuName ?? "No NVIDIA GPU detected";
        double? powerPct = s.GpuPowerW is double gw && s.GpuPowerLimitW is double gl && gl > 0 ? gw / gl * 100 : null;
        SetBar(SensGpuPower, SensGpuPowerBar, s.GpuPowerW is double gp ? $"{Num(gp)} W" : "—", powerPct);
        SensGpuClock.Text = s.GpuClockMHz is double gc ? $"{gc:0} MHz" : "—";
        SetBar(SensGpuUsage, SensGpuUsageBar, s.GpuUsagePercent is double gu ? $"{Num(gu, 0)} %" : "—", s.GpuUsagePercent);
        double? vramPct = s.VramUsedGB is double vu && s.VramTotalGB is double vt && vt > 0 ? vu / vt * 100 : null;
        SetBar(SensVram, SensVramBar,
            s.VramUsedGB is double v ? s.VramTotalGB is double vtot ? $"{Num(v)} / {Num(vtot)} GB" : $"{Num(v)} GB" : "—",
            vramPct);
    }

    private static void SetBar(TextBlock value, ProgressBar bar, string text, double? percent)
    {
        value.Text = text;
        bar.Opacity = percent is null ? 0 : 1;
        bar.Value = Math.Clamp(percent ?? 0, 0, 100);
        bar.Foreground = percent is >= 90 ? Red : percent is >= 75 ? Yellow : Green;
    }

    private void ApplySnapshot(Snapshot s)
    {
        _last = s;
        if (_activityOn)
        {
            Activity.AddTemperature(s.CpuTemp, s.GpuTemp);
            Activity.AddSample(ActivityChart.Fans, s.CpuRpm, s.GpuRpm);
        }
        Activity.SetLimits(s.FailSafeEnabled ? _controller.Settings.CpuThresholdTemp : null,
                           s.FailSafeEnabled ? _controller.Settings.GpuThresholdTemp : null);
        UpdateActivityLegend();

        TitleText.Text = s.ProfileName is null
            ? $"Monitor · Fan Control: {s.Mode.DisplayName()}"
            : $"Monitor · Fan Control: {s.Mode.DisplayName()} · {s.ProfileName}";
        SetTemp(CpuTemp, s.CpuTemp, 85, 95);
        SetTemp(GpuTemp, s.GpuTemp, 72, 85);
        CpuRpm.Text = Rpm(s.CpuRpm);
        GpuRpm.Text = Rpm(s.GpuRpm);
    }

    private static void SetTemp(TextBlock t, int? temp, int warn, int crit)
    {
        if (temp is int v)
        {
            t.Text = $"{v} °C";
            t.Foreground = v >= crit ? Red : v >= warn ? Yellow : Green;
        }
        else
        {
            t.Text = "-- °C";
            t.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private static string Rpm(int? rpm) => rpm is int r ? $"{r.ToString("#,0", Spaced)} RPM" : "-- RPM";

    // ---------- gráfico de atividade ----------

    private void SetActivityMetric(string metric)
    {
        int idx = Array.FindIndex(ActivityChart.Metrics, m => m.Key == metric);
        if (idx < 0) { idx = 0; metric = ActivityChart.Temperature; }
        Activity.Metric = metric;
        if (MetricBox.SelectedIndex != idx) MetricBox.SelectedIndex = idx;
        UpdateActivityLegend();
    }

    private void Metric_Changed(object? sender, SelectionChangedEventArgs e)
    {
        int idx = MetricBox.SelectedIndex;
        if (idx < 0 || idx >= ActivityChart.Metrics.Length) return;
        string key = ActivityChart.Metrics[idx].Key;
        if (key == Activity.Metric) return;
        SetActivityMetric(key);
        if (_controller is not null) _controller.Settings.ActivityMetric = key;
    }

    private void Range_Changed(object? sender, SelectionChangedEventArgs e)
    {
        int idx = RangeBox.SelectedIndex;
        if (idx >= 0 && idx < Ranges.Length) Activity.SetRange(Ranges[idx].Seconds);
    }

    private void UpdateActivityLegend()
    {
        string unit = ActivityChart.UnitOf(Activity.Metric);
        (double? cpu, double? gpu) = Activity.Metric switch
        {
            ActivityChart.Usage => (_lastSensors?.CpuUsagePercent, _lastSensors?.GpuUsagePercent),
            ActivityChart.Clock => (_lastSensors?.CpuClockMHz, _lastSensors?.GpuClockMHz),
            ActivityChart.Power => (_lastSensors?.CpuPowerW, _lastSensors?.GpuPowerW),
            ActivityChart.Fans => ((double?)_last?.CpuRpm, (double?)_last?.GpuRpm),
            _ => ((double?)_last?.CpuTemp, (double?)_last?.GpuTemp),
        };
        string fmt = Activity.Metric == ActivityChart.Power ? "0.0" : "0";
        string F(double? v) => v is double d ? $"{d.ToString(fmt, CultureInfo.CurrentCulture)} {unit}" : "--";
        ActCpuLegend.Text = $"CPU {F(cpu)}";
        ActGpuLegend.Text = $"GPU {F(gpu)}";
    }

    private bool _activityOn = true;

    private void SetActivityEnabled(bool on)
    {
        _activityOn = on;
        ActivityBody.IsVisible = on;
        ActivityOptions.IsVisible = on;
    }

    // ---------- FPS / frametime ----------

    private void SetFpsEnabled(bool on)
    {
        var fps = _host.Fps;
        if (on)
        {
            bool ok = fps.Start();
            if (ok) _fpsTimer.Start(); else _fpsTimer.Stop();
            FpsBody.IsVisible = ok;
            FpsStatus.Text = ok ? "Waiting for a game… (DirectX 9, 10, 11 and 12; Vulkan/OpenGL games are not counted)" : fps.Error ?? "";
        }
        else
        {
            _fpsTimer.Stop();
            fps.Stop();
            FpsBody.IsVisible = false;   // desligado: oculta números e gráfico
            FpsGame.Text = "";
            FpsStatus.Text = "FPS monitor is off.";
        }
        ClearFps();
    }

    private void ClearFps()
    {
        FpsValue.Text = "--";
        FpsFrameTime.Text = "--";
        FpsLow.Text = "--";
        FpsGraph.SetValues(Array.Empty<double>());
    }

    private void UpdateFps()
    {
        // lê sempre (barato), mas só redesenha com a janela à mostra
        var snap = _host.Fps.Read();
        if (!Showing) return;
        if (snap is null)
        {
            if (_host.Fps.Error is { } err) FpsStatus.Text = err;
            else if (FpsValue.Text != "--") { ClearFps(); FpsGame.Text = ""; FpsStatus.Text = "No game presenting frames right now."; }
            return;
        }
        FpsGame.Text = snap.Process;
        FpsValue.Text = $"{snap.Fps:0}";
        FpsFrameTime.Text = $"{snap.FrameTimeMs:0.0} ms";
        FpsLow.Text = snap.Low1Fps is double l ? $"{l:0} FPS" : "--";
        FpsGraph.SetValues(snap.RecentFrameTimes);
        FpsStatus.Text = "Frametime of the last frames. Spikes in the line are stutters.";
    }
}
