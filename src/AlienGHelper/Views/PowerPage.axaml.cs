using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using TccG15.Core;
using TccG15.Core.Power;

namespace TccG15.Views;

/// <summary>
/// Aba Power: limites da CPU (SPL / sPPT / fPPT pela SMU, via PawnIO), modo de energia e boost do Windows,
/// e GPU NVIDIA (power target, offsets de clock e clock máximo, via NvAPIWrapper).
/// </summary>
public partial class PowerPage : UserControl
{
    private const string PawnUrl = "https://pawnio.eu/";
    private const string WingetCmd = "winget install namazso.PawnIO";

    private PowerService? _svc;
    private bool _sync = true;          // ignora eventos enquanto os controles são preenchidos por código
    private string _tab = "Cpu";
    private readonly DispatcherTimer _cpuDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _gpuDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public PowerPage()
    {
        InitializeComponent();
        foreach (var m in WindowsPower.Modes) WinModeBox.Items.Add(m.Name);
        foreach (var b in WindowsPower.BoostModes) BoostBox.Items.Add(b);

        _cpuDebounce.Tick += (_, _) => { _cpuDebounce.Stop(); if (_svc?.Settings.ApplyCpu == true) _ = _svc.ApplyCpuAsync(); };
        _gpuDebounce.Tick += (_, _) => { _gpuDebounce.Stop(); if (_svc?.Settings.ApplyGpu == true) _ = _svc.ApplyGpuAsync(); };

        // Handlers ligados só depois do InitializeComponent (sliders disparam ValueChanged durante o parse)
        SplSlider.OnValueChanged(() => CpuSliderChanged(SplSlider));
        SpptSlider.OnValueChanged(() => CpuSliderChanged(SpptSlider));
        FpptSlider.OnValueChanged(() => CpuSliderChanged(FpptSlider));
        TargetSlider.OnValueChanged(GpuSliderChanged);
        CoreSlider.OnValueChanged(GpuSliderChanged);
        MemSlider.OnValueChanged(GpuSliderChanged);
        MaxClockSlider.OnValueChanged(GpuSliderChanged);
        ApplyCpuCheck.OnCheckedChanged(ApplyCpuChanged);
        ApplyGpuCheck.OnCheckedChanged(ApplyGpuChanged);
        WinModeBox.SelectionChanged += WinMode_Changed;
        BoostBox.SelectionChanged += Boost_Changed;
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) OnShown(); };

        SetTab("Cpu");
    }

    public void Initialize(PowerService service)
    {
        _svc = service;
        service.Changed += () => Dispatcher.UIThread.Post(RefreshFromService);
        LoadSettings(service.Settings);
        RefreshFromService();
    }

    public void RefreshTheme() { }

    // ---------- estado ----------

    private void LoadSettings(PowerSettings p)
    {
        _sync = true;
        var cpu = EditedLimits(p);
        SplSlider.Value = cpu[0];
        SpptSlider.Value = cpu[1];
        FpptSlider.Value = cpu[2];
        ApplyCpuCheck.IsChecked = p.ApplyCpu;
        CoreSlider.Value = p.CoreOffset;
        MemSlider.Value = p.MemoryOffset;
        MaxClockSlider.Value = p.MaxGpuClock == 0 ? MaxClockSlider.Maximum : p.MaxGpuClock;
        TargetSlider.Value = p.GpuPowerTarget;
        ApplyGpuCheck.IsChecked = p.ApplyGpu;
        _sync = false;
        UpdateLabels();
    }

    /// <summary>Relê o modo de energia, o boost e os limites (chamado quando a janela aparece).</summary>
    public void OnShown()
    {
        if (_svc is null) return;
        _sync = true;
        WinModeBox.SelectedIndex = WindowsPower.GetMode();
        BoostBox.SelectedIndex = WindowsPower.GetCpuBoost();
        _sync = false;
        UpdateClockWarning();
        if (_svc.CpuState == CpuPowerState.Ready) _ = _svc.RefreshLimitsAsync();
    }

    private void RefreshFromService()
    {
        if (_svc is null) return;

        // CPU
        var st = _svc.CpuState;
        bool ready = st == CpuPowerState.Ready;
        PawnBanner.IsVisible = st is CpuPowerState.PawnNotInstalled or CpuPowerState.AccessDenied or CpuPowerState.Error;
        if (st == CpuPowerState.PawnNotInstalled)
        {
            PawnTitle.Text = "PawnIO driver is not installed";
            PawnText.Text = $"CPU power limits talk to the Ryzen SMU through the PawnIO driver. Install it, then restart this app. With winget: {WingetCmd}";
        }
        else if (PawnBanner.IsVisible)
        {
            PawnTitle.Text = "Could not use PawnIO";
            PawnText.Text = _svc.CpuDetail + " Reinstalling PawnIO and restarting the app usually fixes it.";
        }
        CpuSliders.IsEnabled = ready;
        ApplyCpuCheck.IsEnabled = ready;
        BtnCpuStock.IsEnabled = ready && _svc.Settings.Stock.Count > 0;
        CurrentLimitsText.Text = _svc.CurrentLimits is { } l
            ? $"Now: {l.Stapm:0} / {l.Slow:0} / {l.Fast:0} W"
            : "";
        CpuResultText.Text = ready ? _svc.CpuResult ?? "" : st == CpuPowerState.Starting ? "" : _svc.CpuDetail;

        // GPU
        var g = _svc.Gpu;
        bool gpuOk = g?.IsValid == true;
        GpuNameText.Text = gpuOk ? g!.Name : "NVIDIA GPU";
        GpuControls.IsEnabled = gpuOk;
        ApplyGpuCheck.IsEnabled = gpuOk;
        BtnGpuReset.IsEnabled = gpuOk;
        if (!gpuOk)
            GpuInfoText.Text = st == CpuPowerState.Starting ? "Starting…" : g?.Error ?? _svc.GpuResult ?? "Not available.";
        else if (g!.PowerRange is { } r)
        {
            GpuInfoText.Text = g.PowerTargetEditable
                ? $"NVAPI · power target {r.Min}–{r.Max} % (default {r.Default} %)"
                : "NVAPI · power target is locked by the laptop firmware";
            _sync = true;
            TargetSlider.Minimum = r.Min;
            TargetSlider.Maximum = Math.Max(r.Max, r.Min + 1);
            _sync = false;
        }
        else GpuInfoText.Text = "NVAPI · power target not exposed by this GPU";
        PowerTargetRow.IsVisible = gpuOk && g!.PowerTargetEditable;
        GpuResultText.Text = gpuOk ? _svc.GpuResult ?? "" : "";

        // Cabeçalho
        bool any = ready || gpuOk;
        StatusDot.Fill = new SolidColorBrush(st == CpuPowerState.Starting ? Colors.Gray
            : ready ? Color.FromRgb(0x5B, 0xE3, 0x7D) : any ? Color.FromRgb(0xFF, 0xB8, 0x4D) : Color.FromRgb(0xFF, 0x6B, 0x6B));
        StatusText.Text = st == CpuPowerState.Starting ? "Starting…" : ready ? _svc.CpuDetail : $"CPU: {_svc.CpuDetail}";
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        SplVal.Text = $"{(int)SplSlider.Value} W";
        SpptVal.Text = $"{(int)SpptSlider.Value} W";
        FpptVal.Text = $"{(int)FpptSlider.Value} W";
        TargetVal.Text = $"{(int)TargetSlider.Value} %";
        CoreVal.Text = $"{(int)CoreSlider.Value:+0;-0;0} MHz";
        MemVal.Text = $"{(int)MemSlider.Value:+0;-0;0} MHz";
        MaxClockVal.Text = MaxClockSlider.Value >= MaxClockSlider.Maximum ? "No limit" : $"{(int)MaxClockSlider.Value} MHz";
    }

    // ---------- abas ----------

    private void Tab_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control { Tag: string tag }) SetTab(tag);
    }

    /// <summary>"Cpu", "Gpu" ou "Adv".</summary>
    public void SetTab(string tab)
    {
        _tab = tab is "Gpu" or "Adv" ? tab : "Cpu";
        TabCpu.SetActive(_tab == "Cpu");
        TabGpu.SetActive(_tab == "Gpu");
        TabAdv.SetActive(_tab == "Adv");
        CpuPanel.IsVisible = _tab == "Cpu";
        GpuPanel.IsVisible = _tab == "Gpu";
        AdvPanel.IsVisible = _tab == "Adv";
        if (_tab == "Adv") OnShown();
    }

    // ---------- CPU ----------

    private void CpuSliderChanged(Slider changed)
    {
        if (_sync || _svc is null) return;
        int spl = (int)SplSlider.Value, sppt = (int)SpptSlider.Value, fppt = (int)FpptSlider.Value;

        // mantém SPL ≤ sPPT ≤ fPPT empurrando os vizinhos
        if (changed == SplSlider) { sppt = Math.Max(sppt, spl); fppt = Math.Max(fppt, sppt); }
        else if (changed == SpptSlider) { spl = Math.Min(spl, sppt); fppt = Math.Max(fppt, sppt); }
        else { sppt = Math.Min(sppt, fppt); spl = Math.Min(spl, sppt); }

        _sync = true;
        SplSlider.Value = spl; SpptSlider.Value = sppt; FpptSlider.Value = fppt;
        _sync = false;
        UpdateLabels();

        _svc.Update(p => WithEdited(p, spl, sppt, fppt));
        _cpuDebounce.Stop();
        _cpuDebounce.Start();
    }

    private static int[] EditedLimits(PowerSettings p) => new[] { p.Spl, p.Sppt, p.Fppt };

    private static PowerSettings WithEdited(PowerSettings p, int spl, int sppt, int fppt) =>
        p with { Spl = spl, Sppt = sppt, Fppt = fppt };

    /// <summary>Relê as configurações (ex.: depois de escolher Eco/Balanced/Performance na janela principal).</summary>
    public void Reload()
    {
        if (_svc is not null) LoadSettings(_svc.Settings);
    }

    private void ApplyCpuChanged()
    {
        if (_sync || _svc is null) return;
        bool on = ApplyCpuCheck.IsChecked == true;
        _svc.Update(p => p with { ApplyCpu = on });
        CpuResultText.Text = on ? "Applying…" : "Restoring…";
        _ = _svc.ApplyCpuAsync();
    }

    private void CpuStock_Click(object? sender, RoutedEventArgs e)
    {
        if (_svc is null) return;
        var stock = _svc.Settings.Stock;
        string key = $"{_svc.Mode}/{WindowsPower.GetMode()}";
        int[]? v = stock.TryGetValue(key, out var m) ? m : stock.Values.FirstOrDefault();
        if (v is null) return;
        var p = _svc.Update(s => WithEdited(s, v[0], v[1], v[2]));
        LoadSettings(p);
        if (p.ApplyCpu) _ = _svc.ApplyCpuAsync();
    }

    private void WinMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_sync || WinModeBox.SelectedIndex < 0) return;
        if (!WindowsPower.SetMode(WinModeBox.SelectedIndex))
        {
            CpuResultText.Text = "Windows refused the power mode (it only works with the Balanced power plan, and not in Battery Saver).";
            _sync = true; WinModeBox.SelectedIndex = WindowsPower.GetMode(); _sync = false;
        }
        UpdateClockWarning();
        _svc?.NotifyWindowsModeChanged();   // relê (ou reaplica) os limites que o firmware gravou
    }

    private void Boost_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_sync || BoostBox.SelectedIndex < 0) return;
        if (!WindowsPower.SetCpuBoost(BoostBox.SelectedIndex))
        {
            CpuResultText.Text = "Could not change the CPU boost mode of the active power plan.";
            _sync = true; BoostBox.SelectedIndex = WindowsPower.GetCpuBoost(); _sync = false;
        }
        UpdateClockWarning();
    }

    private void UpdateClockWarning()
    {
        var plan = WindowsPower.ReadPlanCpu();
        bool balanced = WindowsPower.GetMode() == 1;
        ClockWarn.IsVisible = plan.Locked;
        ClockWarnText.Text =
            (balanced ? "Your power plan is holding the CPU near base clock in Balanced. "
                      : "Your power plan limits the CPU clock (Best Performance hides it, Balanced does not). ") +
            $"Plugged-in settings: {plan}.";
        PlanInfoText.Text = $"Power plan (plugged in): {plan}";
    }

    private void UnlockClock_Click(object? sender, RoutedEventArgs e)
    {
        if (!WindowsPower.UnlockClock())
            CpuResultText.Text = "Windows refused to change the active power plan.";
        _sync = true; BoostBox.SelectedIndex = WindowsPower.GetCpuBoost(); _sync = false;
        UpdateClockWarning();
    }

    private void PawnSite_Click(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(PawnUrl) { UseShellExecute = true }); } catch { }
    }

    private async void PawnCopy_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clip) await clip.SetTextAsync(WingetCmd);
            BtnPawnCopy.Content = "Copied";
        }
        catch { }
    }

    // ---------- GPU ----------

    private void GpuSliderChanged()
    {
        if (_sync || _svc is null) return;
        UpdateLabels();
        int target = (int)TargetSlider.Value, core = (int)CoreSlider.Value, mem = (int)MemSlider.Value;
        int max = MaxClockSlider.Value >= MaxClockSlider.Maximum ? 0 : (int)MaxClockSlider.Value;
        _svc.Update(p => p with
        {
            GpuPowerTarget = target, CoreOffset = core, MemoryOffset = mem, MaxGpuClock = max,
        });
        _gpuDebounce.Stop();
        _gpuDebounce.Start();
    }

    private void ApplyGpuChanged()
    {
        if (_sync || _svc is null) return;
        bool on = ApplyGpuCheck.IsChecked == true;
        _svc.Update(p => p with { ApplyGpu = on });
        GpuResultText.Text = on ? "Applying…" : "Restoring…";
        _ = _svc.ApplyGpuAsync();
    }

    private void GpuReset_Click(object? sender, RoutedEventArgs e)
    {
        if (_svc is null) return;
        int def = _svc.Gpu?.PowerRange?.Default ?? 100;
        var p = _svc.Update(s => s with { GpuPowerTarget = def, CoreOffset = 0, MemoryOffset = 0, MaxGpuClock = 0 });
        LoadSettings(p);
        if (p.ApplyGpu) _ = _svc.ApplyGpuAsync();
    }
}
