namespace AlienGHelper.Core;

public sealed record Snapshot(
    int? GpuTemp, int? GpuRpm,
    int? CpuTemp, int? CpuRpm,
    ThermalMode Mode,
    bool FailSafeEnabled, bool FailSafeTripped, DateTimeOffset? LastHighTemp,
    int? GpuFanSet, int? CpuFanSet,    // velocidade que a curva está aplicando (null fora do modo Fan Curve)
    string? ProfileName = null);       // perfil de aplicativo ativo, se houver

/// <summary>
/// Cérebro do app (sem UI): polling de 1 s, fail-safe, troca de modo,
/// aplicação das curvas de fan e persistência.
/// Eventos são disparados em thread de background: a UI deve usar Dispatcher.
/// </summary>
public sealed class AppController
{
    public const int PollPeriodMs = 1000;
    /// <summary>A velocidade sobe na hora, mas desce no máximo isto por segundo (evita "serrote" nos fans).</summary>
    public const int RampDownPerTick = 3;
    /// <summary>Reenvia a velocidade a cada N ticks mesmo sem mudança (caso a BIOS reverta).</summary>
    private const int ReapplyEveryTicks = 5;

    private readonly object _gate = new();
    private readonly ThermalController _thermal;
    private readonly FailSafe _failSafe = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _lastSavedJson = "";
    private ThermalMode? _gKeyPrevMode;
    private int _tickCounter;
    private volatile bool _calibrating;

    private int? _gpuTemp, _cpuTemp, _gpuRpm, _cpuRpm;
    private int? _gpuApplied, _cpuApplied;

    public AppSettings Settings { get; }
    public ThermalMode Mode { get; private set; } = ThermalMode.Balanced;

    public event Action<Snapshot>? Updated;
    /// <summary>(título, texto, éAlerta)</summary>
    public event Action<string, string, bool>? Notification;
    /// <summary>(mensagem, detalhe) – o app encerra, como no original.</summary>
    public event Action<string, string>? FatalError;

    private AppController(AppSettings settings, ThermalController thermal)
    {
        Settings = settings;
        _thermal = thermal;
        _failSafe.Enabled = settings.FailSafeOn;
        _failSafe.CpuLimit = settings.CpuThresholdTemp;
        _failSafe.GpuLimit = settings.GpuThresholdTemp;
    }

    /// <summary>Pode demorar (varre os IDs de fan via WMI): chamar em background.</summary>
    public static AppController Create()
    {
        var settings = AppSettings.Load();
        var awcc = AwccWmi.Open();
        return new AppController(settings, new ThermalController(awcc));
    }

    public void Start()
    {
        lock (_gate)
        {
            ApplyMode(_profile?.Mode ?? Settings.Mode);
        }
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollPeriodMs));
                Tick();
                while (await timer.WaitForNextTickAsync(token))
                    Tick();
            }
            catch (OperationCanceledException) { }
        });
    }

    // ---------- ações da UI ----------

    public bool SetMode(ThermalMode mode)
    {
        lock (_gate)
        {
            if (_calibrating) return false;   // a calibração precisa do modo Manual até terminar
            bool ok = ApplyMode(mode);
            Publish();
            return ok;
        }
    }

    /// <summary>Atalho G-Mode (tecla F17 do notebook): alterna G-Mode ⇄ modo anterior.</summary>
    public void ToggleGMode()
    {
        lock (_gate)
        {
            if (_calibrating) return;
            if (Mode == ThermalMode.GMode)
            {
                ApplyMode(_gKeyPrevMode ?? ThermalMode.Balanced);
            }
            else
            {
                _gKeyPrevMode = Mode;
                ApplyMode(ThermalMode.GMode);
            }
            NotifyCurrentMode(failSafe: false);
            Publish();
        }
    }

    public void SetCurves(FanCurve cpu, FanCurve gpu)
    {
        lock (_gate)
        {
            Settings.CpuCurve = cpu.Normalize(FanCurve.DefaultCpu());
            Settings.GpuCurve = gpu.Normalize(FanCurve.DefaultGpu());
            if (Mode == ThermalMode.Custom && !_calibrating)
            {
                _cpuApplied = _gpuApplied = null; // aplica o novo alvo imediatamente
                ApplyCurves();
            }
            Publish();
        }
    }

    public void ConfigureFailSafe(bool enabled, int gpuLimit, int cpuLimit)
    {
        lock (_gate)
        {
            bool toggled = enabled != _failSafe.Enabled;
            _failSafe.Enabled = enabled;
            _failSafe.GpuLimit = gpuLimit;
            _failSafe.CpuLimit = cpuLimit;
            Settings.FailSafeOn = enabled;
            Settings.GpuThresholdTemp = gpuLimit;
            Settings.CpuThresholdTemp = cpuLimit;
            if (toggled) _failSafe.Reset();
            Publish();
        }
    }

    public void ResetSettings()
    {
        lock (_gate)
        {
            AppSettings.Delete();
            var d = new AppSettings();
            Settings.CpuCurve = d.CpuCurve;
            Settings.GpuCurve = d.GpuCurve;
            Settings.MinimizeOnClose = d.MinimizeOnClose;
            ConfigureFailSafe(d.FailSafeOn, d.GpuThresholdTemp, d.CpuThresholdTemp);
            ApplyMode(d.Mode);
            Publish();
        }
    }

    // ---------- perfis por aplicativo ----------

    private AppProfile? _profile;
    private ThermalMode? _modeBeforeProfile;

    public AppProfile? ActiveProfile { get { lock (_gate) return _profile; } }

    /// <summary>No modo Manual, um perfil ativo usa as próprias curvas; senão, as curvas principais.</summary>
    private FanCurve ActiveCpuCurve => _profile is { Mode: ThermalMode.Custom } p ? p.CpuCurve : Settings.CpuCurve;
    private FanCurve ActiveGpuCurve => _profile is { Mode: ThermalMode.Custom } p ? p.GpuCurve : Settings.GpuCurve;

    /// <summary>
    /// Ativa o perfil do aplicativo em execução (ou null quando nenhum está aberto).
    /// Ao sair do último perfil, volta para o modo que estava antes.
    /// </summary>
    public void ActivateProfile(AppProfile? profile)
    {
        lock (_gate)
        {
            if (_calibrating) return;   // o vigia tenta de novo em 3 s
            if (ReferenceEquals(profile, _profile)) return;

            if (profile is null)
            {
                var back = _modeBeforeProfile ?? Settings.Mode;
                string name = _profile!.Name;
                _profile = null;
                _modeBeforeProfile = null;
                _cpuApplied = _gpuApplied = null;
                if (Mode != back) ApplyMode(back);
                else if (Mode == ThermalMode.Custom) ApplyCurves();
                Notification?.Invoke($"Profile ended: {name}", $"Back to {back.DisplayName()}", false);
                Publish();
                return;
            }

            bool sameApp = _profile is not null &&
                           string.Equals(_profile.Exe, profile.Exe, StringComparison.OrdinalIgnoreCase);
            if (_profile is null) _modeBeforeProfile = _failSafe.TrippedFrom ?? Mode;
            _profile = profile;
            _cpuApplied = _gpuApplied = null;   // aplica a curva nova imediatamente

            if (Mode != profile.Mode && !_failSafe.IsTripped) ApplyMode(profile.Mode);
            else if (Mode == ThermalMode.Custom) ApplyCurves();

            if (!sameApp)
                Notification?.Invoke($"Profile: {profile.Name}", $"{profile.Exe} · {profile.Mode.DisplayName()}", false);
            Publish();
        }
    }

    /// <summary>Salva, para o polling e volta para Balanced (como o original ao sair).</summary>
    public void Shutdown()
    {
        _cts?.Cancel();
        try { _loop?.Wait(3000); } catch { }
        lock (_gate)
        {
            PersistSettings();
            if (Mode != ThermalMode.Balanced) _thermal.SetMode(ThermalMode.Balanced);
        }
    }

    // ---------- internos (sempre dentro de _gate) ----------

    private bool ApplyMode(ThermalMode mode)
    {
        if (!_thermal.SetMode(mode))
        {
            if (mode == ThermalMode.Quiet)
            {
                // nem todo BIOS tem o perfil silencioso: avisa e continua no modo atual
                Notification?.Invoke("Silent is not available", "This BIOS did not accept the quiet thermal profile.", true);
                return false;
            }
            FatalError?.Invoke($"Failed to set mode {mode.DisplayName()}", "Program is terminated");
            return false;
        }
        Mode = mode;
        _cpuApplied = _gpuApplied = null;
        if (mode == ThermalMode.Custom)
        {
            ReadSensors();   // temperatura atual para partir do ponto certo da curva
            ApplyCurves();
        }
        if (mode != ThermalMode.GMode) _failSafe.TrippedFrom = null; // troca manual cancela o fail-safe
        return true;
    }

    private void ReadSensors()
    {
        _gpuTemp = _thermal.GetFanRelatedTemp(ThermalController.GpuFanIdx);
        _gpuRpm = _thermal.GetFanRpm(ThermalController.GpuFanIdx);
        _cpuTemp = _thermal.GetFanRelatedTemp(ThermalController.CpuFanIdx);
        _cpuRpm = _thermal.GetFanRpm(ThermalController.CpuFanIdx);
    }

    private void ApplyCurves()
    {
        _gpuApplied = StepFan(ActiveGpuCurve, _gpuTemp, _gpuApplied, ThermalController.GpuFanIdx);
        _cpuApplied = StepFan(ActiveCpuCurve, _cpuTemp, _cpuApplied, ThermalController.CpuFanIdx);
    }

    private int StepFan(FanCurve curve, int? temp, int? applied, int fanIdx)
    {
        // Sem leitura de temperatura: por segurança usa a velocidade máxima da curva.
        int target = temp.HasValue ? curve.Evaluate(temp.Value) : curve.MaxPointSpeed;
        int next = applied is int a && target < a ? Math.Max(target, a - RampDownPerTick) : target;
        if (next != applied || _tickCounter % ReapplyEveryTicks == 0)
            _thermal.SetFanSpeed(fanIdx, ToRaw(fanIdx, next));
        return next;
    }

    /// <summary>% da curva → valor bruto da BIOS: 100 % = máximo calibrado do fan.</summary>
    private int ToRaw(int fanIdx, int percent)
    {
        var max = Settings.FanMaxBoost;
        int top = max is { Length: > 1 } && fanIdx < max.Length ? Math.Clamp(max[fanIdx], 100, 255) : 100;
        return (int)Math.Round(Math.Clamp(percent, 0, FanCurve.MaxSpeed) * top / 100.0);
    }

    // ---------- calibração dos fans ----------

    public bool IsCalibrating => _calibrating;

    /// <summary>
    /// Descobre até onde cada fan acelera de verdade: no modo Manual, manda 100, 110, 120… (valor bruto
    /// da BIOS, até 255), espera o fan estabilizar e mede o RPM. Quando o RPM para de subir, o passo
    /// anterior vira o máximo do fan. Leva 1–2 minutos com os fans no máximo. Ao terminar (ou cancelar),
    /// volta ao modo anterior.
    /// </summary>
    public async Task<(int[] Boost, int[] Rpm)> CalibrateFansAsync(IProgress<string> progress, CancellationToken ct)
    {
        int fans;
        ThermalMode previous;
        lock (_gate)
        {
            if (_calibrating) throw new InvalidOperationException("Calibration is already running.");
            fans = Math.Min(2, _thermal.FanCount);
            if (fans == 0) throw new InvalidOperationException("No fans found.");
            _calibrating = true;
            previous = _modeBeforeProfile ?? _failSafe.TrippedFrom ?? Mode;
            if (Mode != ThermalMode.Custom && !_thermal.SetMode(ThermalMode.Custom))
            {
                _calibrating = false;
                throw new InvalidOperationException("The BIOS did not accept Manual mode.");
            }
            Mode = ThermalMode.Custom;
            Publish();
        }

        var steps = Enumerable.Range(0, 16).Select(i => 100 + i * 10).Append(255).ToArray();   // 100…250, 255
        var best = Enumerable.Repeat(100, fans).ToArray();
        var bestRpm = new int[fans];
        var done = new bool[fans];
        try
        {
            foreach (int step in steps)
            {
                lock (_gate)
                    for (int i = 0; i < fans; i++)
                        if (!done[i]) _thermal.SetFanSpeed(i, step);
                progress.Report($"Testing {step}%…");

                await Task.Delay(6000, ct).ConfigureAwait(false);   // o fan leva alguns segundos para chegar na nova rotação
                var sum = new int[fans];
                for (int k = 0; k < 3; k++)
                {
                    lock (_gate)
                        for (int i = 0; i < fans; i++)
                            sum[i] += _thermal.GetFanRpm(i) ?? 0;
                    await Task.Delay(700, ct).ConfigureAwait(false);
                }

                var parts = new List<string>();
                for (int i = 0; i < fans; i++)
                {
                    int rpm = sum[i] / 3;
                    parts.Add($"{(i == 0 ? "CPU" : "GPU")} {rpm} RPM");
                    if (done[i]) continue;
                    if (step == 100) { bestRpm[i] = rpm; continue; }
                    // ainda sobe de verdade (≥ 2 % e ≥ 40 RPM)? senão o anterior era o teto
                    if (rpm >= bestRpm[i] * 1.02 && rpm - bestRpm[i] >= 40) { best[i] = step; bestRpm[i] = rpm; }
                    else done[i] = true;
                }
                progress.Report($"{step}%: {string.Join(" · ", parts)}");
                if (done.All(d => d)) break;
            }

            lock (_gate)
            {
                Settings.FanMaxBoost = new[] { best[0], fans > 1 ? best[1] : 100 };
                Settings.FanMaxRpm = new[] { bestRpm[0], fans > 1 ? bestRpm[1] : 0 };
            }
            return (best, bestRpm);
        }
        finally
        {
            lock (_gate)
            {
                _calibrating = false;
                _cpuApplied = _gpuApplied = null;
                ApplyMode(previous);
                Publish();
            }
        }
    }

    private void Tick()
    {
        try
        {
            lock (_gate)
            {
                _tickCounter++;
                ReadSensors();

                if (_calibrating) { Publish(); return; }   // a calibração controla os fans

                switch (_failSafe.Evaluate(_gpuTemp, _cpuTemp, Mode))
                {
                    case FailSafeAction.TripToGMode:
                        ApplyMode(ThermalMode.GMode); // mantém TrippedFrom (só limpa se não for G-Mode)
                        NotifyCurrentMode(failSafe: true);
                        break;
                    case FailSafeAction.RestorePrevious:
                        var prev = _failSafe.TrippedFrom!.Value;
                        ApplyMode(prev);              // limpa TrippedFrom
                        NotifyCurrentMode(failSafe: true);
                        break;
                }

                if (Mode == ThermalMode.Custom) ApplyCurves();

                PersistSettings();
                Publish();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Tick failed: {ex}");
        }
    }

    private void Publish() => Updated?.Invoke(new Snapshot(
        _gpuTemp, _gpuRpm, _cpuTemp, _cpuRpm,
        Mode, _failSafe.Enabled, _failSafe.IsTripped, _failSafe.LastHighTemp,
        Mode == ThermalMode.Custom ? _gpuApplied : null,
        Mode == ThermalMode.Custom ? _cpuApplied : null,
        _profile?.Name));

    private void PersistSettings()
    {
        // não grava o modo "forçado" pelo fail-safe nem o de um perfil de aplicativo
        Settings.Mode = _modeBeforeProfile ?? _failSafe.TrippedFrom ?? Mode;
        string json = Settings.Serialize();
        if (json == _lastSavedJson) return;
        _lastSavedJson = json;
        Settings.Save();
    }

    private void NotifyCurrentMode(bool failSafe)
    {
        string text = $"GPU {_gpuTemp?.ToString() ?? "-"} °C · CPU {_cpuTemp?.ToString() ?? "-"} °C";
        if (failSafe) text += "\nTriggered by Fail-safe";
        Notification?.Invoke($"Thermal mode: {Mode.DisplayName()}", text, failSafe);
    }
}
