using Microsoft.Win32;

namespace AlienGHelper.Core.Power;

public enum CpuPowerState { Starting, NotAmd, PawnNotInstalled, AccessDenied, Unsupported, Error, Ready }

/// <summary>
/// Orquestra os limites de potência: CPU (SMU via PawnIO) e GPU (NVAPI).
/// - CPU: reaplica a cada 30 s (o firmware de Rembrandt/Phoenix volta os limites sozinho sob carga),
///   3 s depois de cada troca de modo térmico (o BIOS da Dell grava os dele) e ao acordar.
/// - Limites de fábrica são lidos por modo térmico enquanto "Apply" está desligado, para restaurar depois.
/// Tudo roda fora da UI; os eventos chegam em thread de background.
/// </summary>
public sealed class PowerService : IDisposable
{
    /// <summary>O PMF e o firmware devolvem os limites sozinhos: reenvio frequente (escritas baratas na mailbox).</summary>
    private const int ReapplyMs = 10_000;

    private readonly AppController _controller;
    private readonly object _settingsGate = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private RyzenSmu? _smu;
    private Timer? _reapply;
    private ThermalMode? _mode;
    private int _winMode = -2;
    private CancellationTokenSource? _modeDelay;
    private int? _appliedMaxClock;
    private bool _disposed;

    public CpuPowerState CpuState { get; private set; } = CpuPowerState.Starting;
    public string CpuDetail { get; private set; } = "Starting…";
    public PowerLimits? CurrentLimits { get; private set; }
    public string? CpuResult { get; private set; }

    public NvidiaControl? Gpu { get; private set; }
    public ThermalMode Mode => _mode ?? _controller.Mode;
    public string? GpuResult { get; private set; }

    /// <summary>Estado, limites lidos ou resultado de uma aplicação mudaram.</summary>
    public event Action? Changed;

    public PowerService(AppController controller) => _controller = controller;

    public PowerSettings Settings
    {
        get { lock (_settingsGate) return _controller.Settings.Power; }
    }

    /// <summary>Troca a configuração de forma atômica (UI e serviço usam o mesmo lock).</summary>
    public PowerSettings Update(Func<PowerSettings, PowerSettings> change)
    {
        lock (_settingsGate)
        {
            var next = change(_controller.Settings.Power).Sanitized();
            _controller.Settings.Power = next;
            return next;
        }
    }

    // ---------- ciclo de vida ----------

    public void Start() => Task.Run(async () =>
    {
        await _work.WaitAsync();
        try
        {
            InitCpu();
            InitGpu();
            _mode = _controller.Mode;
            if (CpuState == CpuPowerState.Ready)
            {
                if (Settings.ApplyCpu) ApplyCpuCore();
                else CaptureStock();
                CurrentLimits = _smu!.ReadLimits() ?? CurrentLimits;
            }
            if (Gpu?.IsValid == true && Settings.ApplyGpu) ApplyGpuCore();
        }
        catch (Exception ex) { CpuDetail = ex.Message; }
        finally { _work.Release(); }

        Changed?.Invoke();
        _controller.Updated += OnSnapshot;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _winMode = WindowsPower.GetMode();
        _reapply = new Timer(_ => Reapply(), null, ReapplyMs, ReapplyMs);
    });

    private void InitCpu()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported || !IsAmd())
        {
            CpuState = CpuPowerState.NotAmd;
            CpuDetail = "CPU power limits need an AMD Ryzen processor.";
            return;
        }

        var smu = new RyzenSmu();
        var r = smu.Initialize(out string? error);
        switch (r)
        {
            case PawnIO.ConnectResult.NotInstalled:
                CpuState = CpuPowerState.PawnNotInstalled;
                CpuDetail = "PawnIO driver is not installed.";
                smu.Dispose();
                return;
            case PawnIO.ConnectResult.AccessDenied:
                CpuState = CpuPowerState.AccessDenied;
                CpuDetail = "Access to PawnIO was denied (run as administrator).";
                smu.Dispose();
                return;
            case PawnIO.ConnectResult.OtherError:
                CpuState = CpuPowerState.Error;
                CpuDetail = error ?? "Could not open the PawnIO driver.";
                smu.Dispose();
                return;
        }
        if (!smu.IsReady)
        {
            CpuState = CpuPowerState.Unsupported;
            CpuDetail = error ?? "CPU not supported.";
            smu.Dispose();
            return;
        }
        _smu = smu;
        CpuState = CpuPowerState.Ready;
        CpuDetail = $"{smu.CodeNameText} · SMU {smu.SmuVersionText} · PawnIO";
    }

    private static bool IsAmd()
    {
        var (_, ebx, ecx, edx) = System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0);
        Span<int> regs = stackalloc int[] { ebx, edx, ecx };
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(regs).SequenceEqual("AuthenticAMD"u8);
    }

    private void InitGpu()
    {
        try { Gpu = new NvidiaControl(); }
        catch (Exception ex) { Gpu = null; GpuResult = ex.Message; }
    }

    // ---------- CPU ----------

    /// <summary>Aplica (ou, com Apply desligado, restaura os de fábrica) e relê os limites.</summary>
    public Task ApplyCpuAsync() => Run(() =>
    {
        if (Settings.ApplyCpu) ApplyCpuCore();
        else RestoreStockCore();
        CurrentLimits = _smu?.ReadLimits() ?? CurrentLimits;
    });

    public Task RefreshLimitsAsync() => Run(() => CurrentLimits = _smu?.ReadLimits() ?? CurrentLimits);

    private void ApplyCpuCore()
    {
        if (_smu is null) return;
        var p = Settings;
        var v = p.LimitsFor(Mode);   // por modo térmico, se "Limits per thermal mode" estiver ligado
        var (spl, sppt, fppt) = _smu.SetLimits(v[0], v[1], v[2]);
        string where = p.LinkModes && Mode != ThermalMode.Custom ? $" ({Mode.DisplayName()})" : "";
        CpuResult = spl == SmuStatus.OK && sppt == SmuStatus.OK && fppt == SmuStatus.OK
            ? $"Applied {v[0]} / {v[1]} / {v[2]} W{where}"
            : $"SMU answered: SPL {spl}, sPPT {sppt}, fPPT {fppt}";
    }

    private void RestoreStockCore()
    {
        if (_smu is null) return;
        var stock = Settings.Stock;
        int[]? v = stock.TryGetValue(ModeKey, out var m) ? m : stock.Values.FirstOrDefault();
        if (v is null)
        {
            CpuResult = "Limits stay until the next thermal mode change or reboot.";
            return;
        }
        _smu.SetLimits(v[0], v[1], v[2]);
        CpuResult = $"Restored stock {v[0]} / {v[1]} / {v[2]} W";
    }

    /// <summary>Limites de fábrica dependem do modo térmico da Dell e do modo de energia do Windows.</summary>
    private string ModeKey => $"{Mode}/{WindowsPower.GetMode()}";

    private void CaptureStock()
    {
        if (_smu is null || Settings.ApplyCpu) return;
        if (_smu.ReadLimits() is not { } l) return;
        CurrentLimits = l;
        var vals = new[] { (int)Math.Round(l.Stapm), (int)Math.Round(l.Slow), (int)Math.Round(l.Fast) };
        string key = ModeKey;
        Update(p => p with { Stock = new Dictionary<string, int[]>(p.Stock) { [key] = vals } });
    }

    // ---------- GPU ----------

    public Task ApplyGpuAsync() => Run(() =>
    {
        if (Settings.ApplyGpu) ApplyGpuCore();
        else ResetGpuCore();
    });

    private void ApplyGpuCore()
    {
        if (Gpu is not { IsValid: true } g) return;
        var p = Settings;
        var errors = new List<string>();
        if (g.PowerTargetEditable && g.SetPowerTarget(p.GpuPowerTarget) is { } e1) errors.Add(e1);
        // ordem do undervolt: primeiro trava o clock, depois sobe a curva (evita um pico acima do alvo)
        int max = p.EffectiveMaxClock;
        if (_appliedMaxClock != max)
        {
            if (g.SetMaxClock(max) is { } e3) errors.Add(e3);
            else _appliedMaxClock = max;
        }
        if (g.SetClockOffsets(p.EffectiveCoreOffset, p.MemoryOffset) is { } e2) errors.Add(e2);
        GpuResult = errors.Count == 0
            ? p.UndervoltOn ? $"Undervolt applied: up to {p.UvTarget} MHz at the voltage of {p.UvTarget - p.UvOffset} MHz" : "Applied"
            : string.Join(" · ", errors.Distinct());
    }

    private void ResetGpuCore()
    {
        if (Gpu is not { IsValid: true } g) return;
        var errors = new List<string>();
        if (g.PowerTargetEditable && g.PowerRange is { } r && g.SetPowerTarget(r.Default) is { } e1) errors.Add(e1);
        if (g.SetClockOffsets(0, 0) is { } e2) errors.Add(e2);
        if (_appliedMaxClock is not (null or 0))
        {
            if (g.SetMaxClock(0) is { } e3) errors.Add(e3);
            else _appliedMaxClock = 0;
        }
        GpuResult = errors.Count == 0 ? "Restored driver defaults" : string.Join(" · ", errors.Distinct());
    }

    // ---------- gatilhos ----------

    private void OnSnapshot(Snapshot s)
    {
        if (_mode == s.Mode) return;
        _mode = s.Mode;
        ScheduleAfterChange(3000);
    }

    /// <summary>
    /// O modo de energia do Windows também muda os limites: no Ryzen 6000 a Dell usa o AMD PMF
    /// ("static slider"), que grava STAPM/sPPT/fPPT diferentes para Best Performance, Balanced e
    /// Best Power Efficiency. Chamado pela UI e pelo timer quando o modo muda.
    /// </summary>
    public void NotifyWindowsModeChanged()
    {
        _winMode = WindowsPower.GetMode();
        ScheduleAfterChange(2500);
    }

    private void ScheduleAfterChange(int delayMs)
    {
        _modeDelay?.Cancel();
        var cts = _modeDelay = new CancellationTokenSource();
        // o BIOS grava os limites do modo novo logo após a troca: espera ele terminar
        Task.Delay(delayMs, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            _ = Run(() =>
            {
                if (Settings.ApplyCpu) ApplyCpuCore();
                else CaptureStock();
                CurrentLimits = _smu?.ReadLimits() ?? CurrentLimits;
            });
        }, TaskScheduler.Default);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Task.Delay(5000).ContinueWith(_ => Run(() =>
        {
            if (Settings.ApplyCpu) ApplyCpuCore();
            if (Settings.ApplyGpu) { _appliedMaxClock = null; ApplyGpuCore(); }
        }), TaskScheduler.Default);
    }

    private void Reapply()
    {
        // troca do modo de energia feita fora do app (Configurações, ícone da bateria)
        int win = WindowsPower.GetMode();
        if (win != _winMode) { NotifyWindowsModeChanged(); return; }
        if (!Settings.ApplyCpu || _smu is null) return;
        if (!_work.Wait(0)) return;   // algo já está rodando: pula esta rodada
        try { ApplyCpuCore(); }
        catch { }
        finally { _work.Release(); }
    }

    private Task Run(Action action) => Task.Run(async () =>
    {
        if (_disposed) return;
        await _work.WaitAsync();
        try { action(); }
        catch (Exception ex) { CpuResult = ex.Message; }
        finally { _work.Release(); }
        Changed?.Invoke();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reapply?.Dispose();
        _controller.Updated -= OnSnapshot;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _smu?.Dispose();
    }
}
