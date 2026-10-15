using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using TccG15.Core;

namespace TccG15.Views;

/// <summary>
/// Janela "Fans and Power" (como a do G-Helper): limites de CPU/GPU à esquerda (<see cref="PowerPage"/>)
/// e as curvas de fan à direita. Marcar "Apply Custom Fan Curve" = modo Manual.
/// </summary>
public partial class FansWindow : Window
{
    private static readonly ThermalMode[] ModeOrder = { ThermalMode.Quiet, ThermalMode.Balanced, ThermalMode.GMode, ThermalMode.Custom };

    private readonly AppController _controller = null!;
    private FanCurve _cpuCurve = FanCurve.DefaultCpu();
    private FanCurve _gpuCurve = FanCurve.DefaultGpu();
    private ThermalMode _lastAutoMode = ThermalMode.Balanced;   // modo para voltar ao desmarcar a curva
    private bool _sync = true;

    public PowerPage PowerPageView => PowerView;

    /// <summary>Só para o designer do Avalonia.</summary>
    public FansWindow() => InitializeComponent();

    public FansWindow(AppHost host)
    {
        InitializeComponent();
        _controller = host.Controller;
        AppTheme.Register(this);

        var s = _controller.Settings;
        _cpuCurve = s.CpuCurve.Clone();
        _gpuCurve = s.GpuCurve.Clone();

        // Curvas no estilo do G-Helper: CPU azul, GPU vermelha
        CpuEditor.CurveColor = Color.FromRgb(0x3F, 0xA9, 0xF5);
        GpuEditor.CurveColor = Color.FromRgb(0xE5, 0x39, 0x35);
        CpuEditor.SetCurve(_cpuCurve);
        GpuEditor.SetCurve(_gpuCurve);
        CpuEditor.CurveChanged += c => { _cpuCurve = c; PushCurves(); };
        GpuEditor.CurveChanged += c => { _gpuCurve = c; PushCurves(); };
        ClampToggle.IsChecked = s.ClampFanCurve;
        CpuEditor.SnapToGrid = GpuEditor.SnapToGrid = s.ClampFanCurve;

        foreach (var m in ModeOrder) ModeBox.Items.Add(m.DisplayName());
        ModeBox.SelectedIndex = Array.IndexOf(ModeOrder, _controller.Mode);
        ModeBox.SelectionChanged += (_, _) =>
        {
            if (_sync || ModeBox.SelectedIndex < 0) return;
            var mode = ModeOrder[ModeBox.SelectedIndex];
            Task.Run(() => _controller.SetMode(mode));
        };

        ClampToggle.OnCheckedChanged(() =>
        {
            bool on = ClampToggle.IsChecked == true;
            CpuEditor.SnapToGrid = GpuEditor.SnapToGrid = on;
            if (!_sync) _controller.Settings.ClampFanCurve = on;
        });
        ApplyCurveCheck.OnCheckedChanged(() =>
        {
            if (_sync) return;
            var mode = ApplyCurveCheck.IsChecked == true ? ThermalMode.Custom : _lastAutoMode;
            Task.Run(() => _controller.SetMode(mode));
        });

        PowerView.Initialize(host.Power);
        ShowCalibration();
        _controller.Updated += snap => Dispatcher.UIThread.Post(() => ApplySnapshot(snap));
        Opened += (_, _) => PowerView.OnShown();
        Activated += (_, _) => PowerView.OnShown();
        _sync = false;
    }

    /// <summary>"Cpu", "Gpu" ou "Adv".</summary>
    public void SelectPowerTab(string tab) => PowerView.SetTab(tab);

    /// <summary>As curvas mudaram fora daqui (ex.: "Restore defaults" na janela Extra).</summary>
    public void ReloadCurves()
    {
        var s = _controller.Settings;
        _cpuCurve = s.CpuCurve.Clone();
        _gpuCurve = s.GpuCurve.Clone();
        CpuEditor.SetCurve(_cpuCurve);
        GpuEditor.SetCurve(_gpuCurve);
        _sync = true;
        ClampToggle.IsChecked = s.ClampFanCurve;
        _sync = false;
    }

    private void ApplySnapshot(Snapshot s)
    {
        bool manual = s.Mode == ThermalMode.Custom;
        if (!manual) _lastAutoMode = s.Mode;

        _sync = true;
        ModeBox.SelectedIndex = Array.IndexOf(ModeOrder, s.Mode);
        ApplyCurveCheck.IsChecked = manual;
        _sync = false;

        ProfileBadge.IsVisible = s.ProfileName is not null;
        ProfileBadgeText.Text = s.ProfileName is null ? "" : $"Profile: {s.ProfileName}";
        TitleText.Text = $"Fans and Power · {s.Mode.DisplayName()}";

        // os pontos só podem ser movidos com o modo Manual selecionado
        CurvesBox.Opacity = manual ? 1.0 : 0.75;
        CpuEditor.IsReadOnly = GpuEditor.IsReadOnly = !manual;
        BtnFactoryDefaults.IsEnabled = manual && _calibration is null;
        CurveEditHint.Text = manual
            ? "Double-click to add · drag to adjust · right-click to delete"
            : "Check “Apply Custom Fan Curve” to edit";
        ModeInfoText.Text = manual
            ? (s.ProfileName is null ? "" : $"Profile “{s.ProfileName}” is active: its own curves are in use.")
            : $"{s.Mode.DisplayName()} uses Dell's fan control. These curves are not applied.";

        CpuEditor.SetLive(s.CpuTemp, s.CpuFanSet);
        GpuEditor.SetLive(s.GpuTemp, s.GpuFanSet);
        CurveStat.Text = $"{Part("CPU", s.CpuTemp, s.CpuRpm, s.CpuFanSet)}    {Part("GPU", s.GpuTemp, s.GpuRpm, s.GpuFanSet)}";
    }

    private static readonly NumberFormatInfo Spaced = new() { NumberGroupSeparator = "." };   // 2.100 RPM

    private static string Part(string name, int? t, int? rpm, int? set) =>
        $"{name}: {t?.ToString() ?? "--"}°C  {(rpm is int r ? r.ToString("#,0", Spaced) : "--")} RPM{(set is int v ? $"  {v}%" : "")}";

    // ---------- calibração ----------

    private CancellationTokenSource? _calibration;

    private void ShowCalibration()
    {
        var s = _controller.Settings;
        var b = s.FanMaxBoost;
        var r = s.FanMaxRpm;
        bool calibrated = b is { Length: 2 } && r is { Length: 2 } && (r[0] > 0 || r[1] > 0);
        CalibrationText.Text = calibrated
            ? $"Calibrated top speed · CPU {b[0]}% ({r[0]} RPM) · GPU {b[1]}% ({r[1]} RPM). 100% on the curves = this speed."
            : "Not calibrated: 100% on the curves = the BIOS's 100% (the fans may go faster). Press Calibrate to find out.";
    }

    private async void Calibrate_Click(object? sender, RoutedEventArgs e)
    {
        if (_calibration is not null)
        {
            _calibration.Cancel();   // botão vira "Stop" durante a calibração
            return;
        }
        if (!await Dialogs.ConfirmAsync(this, "Calibrate fans",
                "The fans will run at full speed (and above) for about 1–2 minutes while the app measures how fast " +
                "each one really goes. It will be loud. Your thermal mode comes back at the end.\n\nStart now?"))
            return;

        _calibration = new CancellationTokenSource();
        CalibrateText.Text = "Stop";
        BtnFactoryDefaults.IsEnabled = ApplyCurveCheck.IsEnabled = ModeBox.IsEnabled = false;
        var progress = new Progress<string>(msg => CalibrationText.Text = $"Calibrating… {msg}");
        try
        {
            var (boost, rpm) = await _controller.CalibrateFansAsync(progress, _calibration.Token);
            ShowCalibration();
            await Dialogs.ShowAsync(this, "Calibrate fans",
                $"CPU fan: {boost[0]}% · {rpm[0]} RPM" + (boost.Length > 1 ? $"\nGPU fan: {boost[1]}% · {rpm[1]} RPM" : "") +
                "\n\n100% on the fan curves now means this speed.");
        }
        catch (OperationCanceledException)
        {
            CalibrationText.Text = "Calibration stopped. The previous values were kept.";
        }
        catch (Exception ex)
        {
            CalibrationText.Text = $"Calibration failed: {ex.Message}";
        }
        finally
        {
            _calibration.Dispose();
            _calibration = null;
            CalibrateText.Text = "Calibrate";
            BtnFactoryDefaults.IsEnabled = _controller.Mode == ThermalMode.Custom;
            ApplyCurveCheck.IsEnabled = ModeBox.IsEnabled = true;
        }
    }

    private void FactoryDefaults_Click(object? sender, RoutedEventArgs e)
    {
        _cpuCurve = FanCurve.DefaultCpu();
        _gpuCurve = FanCurve.DefaultGpu();
        CpuEditor.SetCurve(_cpuCurve);
        GpuEditor.SetCurve(_gpuCurve);
        PushCurves();
    }

    private void PushCurves()
    {
        var cpu = _cpuCurve.Clone();
        var gpu = _gpuCurve.Clone();
        Task.Run(() => _controller.SetCurves(cpu, gpu));
    }
}
