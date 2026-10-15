namespace AlienGHelper.Core.Lighting;

/// <summary>Pedido de iluminação (um por "aplicar").</summary>
public sealed record LightingRequest(
    int Device,
    IReadOnlyList<byte> Lights,
    IReadOnlyList<(byte R, byte G, byte B)> Colors,   // uma cor por luz
    string Effect,                                    // Static, Pulse, Breath, Spectrum, Rainbow
    int Duration,                                     // 0 = Low, 1 = Med, 2 = High
    int Brightness255,                                // 0..255 (hardware)
    bool Persist = false);                            // também grava na memória do teclado (animação padrão)

/// <summary>
/// Controle do teclado via APIv4 (USB HID), sem programas externos.
/// Aplicações são serializadas e só a mais recente importa ("latest wins").
/// </summary>
public sealed class KeyboardLightingService : IDisposable
{
    /// <summary>Paleta do arco-íris (mesma ordem do OpenRGB: vermelho, laranja, amarelo, verde, azul-claro, azul, roxo).</summary>
    private static readonly (byte R, byte G, byte B)[] Rainbow =
    {
        (0xFF, 0x00, 0x00), (0xFF, 0xA5, 0x00), (0xFF, 0xFF, 0x00), (0x00, 0xFF, 0x00),
        (0x00, 0xBF, 0xFF), (0x00, 0x00, 0xFF), (0x80, 0x00, 0xFF),
    };

    /// <summary>Duração de cada fase para Low / Med / High (valores SHORT/MED/LONG do AlienwareController do OpenRGB).</summary>
    private static readonly ushort[] DurationMs = { 499, 1500, 2500 };

    /// <summary>Tempo das transições: ALIENWARE_TEMPO_MAX (0xFA = 250), o padrão do AlienwareController do OpenRGB.</summary>
    private const ushort EffectTempo = 0xFA;

    /// <summary>
    /// No Pulse a velocidade vem do tempo, não da duração (a duração não mudava nada no hardware).
    /// Faixa ALIENWARE_TEMPO_MIN..MAX do OpenRGB (100..250); 0xFA é o valor "steady" do SDK, então
    /// tempo maior = pulso mais lento: Low = 100 (rápido), Med = 175, High = 250 (lento).
    /// </summary>
    private static readonly ushort[] PulseTempo = { 100, 175, 250 };

    /// <summary>Duração da fase do Pulse: o padrão do OpenRGB (2000 ms).</summary>
    private const ushort PulseDurationMs = 2000;

    private readonly object _devLock = new();
    private readonly object _queueLock = new();
    private LightingRequest? _pending;
    private bool _running;

    private List<AlienFxDeviceInfo> _devices = new();
    private AlienFxV4Device? _open;
    private string? _openPath;

    /// <summary>(sucesso, mensagem). Disparado em thread de background.</summary>
    public event Action<bool, string>? Finished;

    public IReadOnlyList<AlienFxDeviceInfo> Devices => _devices;
    public List<AlienFxDeviceInfo> Supported => _devices.Where(d => d.IsSupported).ToList();

    /// <summary>Procura dispositivos (pode levar alguns ms: chamar em background). Retorna quantos são suportados.</summary>
    public int Refresh()
    {
        lock (_devLock)
        {
            CloseDevice();
            try { _devices = AlienFxHid.Enumerate(); }
            catch { _devices = new List<AlienFxDeviceInfo>(); }
            return _devices.Count(d => d.IsSupported);
        }
    }

    public void Submit(LightingRequest request)
    {
        lock (_queueLock)
        {
            _pending = request;
            if (_running) return;
            _running = true;
        }
        Task.Run(Pump);
    }

    private void Pump()
    {
        while (true)
        {
            LightingRequest? next;
            lock (_queueLock)
            {
                next = _pending;
                _pending = null;
                if (next is null) { _running = false; return; }
            }
            var (ok, msg) = Apply(next);
            Finished?.Invoke(ok, msg);
        }
    }

    /// <summary>
    /// Acende cada luz sozinha (branco, uma por vez) para você descobrir qual ID é qual zona.
    /// Ao terminar, reaplica <paramref name="restore"/>.
    /// </summary>
    public Task<(bool Ok, string Message)> IdentifyAsync(int device, IReadOnlyList<byte> lights,
        LightingRequest restore, Action<string>? progress)
    {
        return Task.Run(() =>
        {
            (bool, string) result;
            lock (_devLock)
            {
                try
                {
                    var dev = EnsureDevice(device, out string? err);
                    if (dev is null) return (false, err ?? "No device");

                    dev.SetBrightness(255, lights);
                    foreach (var id in lights)
                    {
                        progress?.Invoke($"Light {id}");
                        dev.SetMultiColor(lights, 0, 0, 0);
                        dev.SetMultiColor(new[] { id }, 255, 255, 255);
                        dev.UpdateColors();
                        Thread.Sleep(900);
                    }
                    result = (true, "Done");
                }
                catch (Exception ex)
                {
                    CloseDevice();
                    result = (false, ex.Message);
                }
            }
            Submit(restore);
            return result;
        });
    }

    // ---------- internos ----------

    private (bool Ok, string Message) Apply(LightingRequest r)
    {
        lock (_devLock)
        {
            try
            {
                var dev = EnsureDevice(r.Device, out string? err);
                if (dev is null) return (false, err ?? "No device");

                if (TryApply(dev, r)) return Persisted(dev, r);

                // Pode ter sido um handle velho (suspensão, troca de energia): reabre uma vez
                CloseDevice();
                dev = EnsureDevice(r.Device, out err);
                if (dev is not null && TryApply(dev, r)) return Persisted(dev, r);
                return (false, err ?? "The device did not accept the command");
            }
            catch (Exception ex)
            {
                CloseDevice();
                return (false, ex.Message);
            }
        }
    }

    /// <summary>Com <see cref="LightingRequest.Persist"/>, grava o mesmo efeito como animação padrão do teclado.</summary>
    private static (bool, string) Persisted(AlienFxV4Device dev, LightingRequest r)
    {
        if (!r.Persist) return (true, "");
        var items = Enumerable.Range(0, r.Lights.Count)
            .Select(i => (r.Lights[i], (IReadOnlyList<V4Phase>)PhasesFor(r, i)))
            .ToList();
        return dev.SaveAsDefault(items)
            ? (true, "saved")
            : (false, "Applied, but the keyboard did not accept saving to its memory");
    }

    /// <summary>Fases de uma luz para o efeito pedido (as mesmas usadas ao aplicar).</summary>
    private static V4Phase[] PhasesFor(LightingRequest r, int i)
    {
        var c = r.Colors[i];
        ushort duration = DurationMs[Math.Clamp(r.Duration, 0, 2)];
        switch (r.Effect)
        {
            case "Spectrum" or "Rainbow":
                int shift = r.Effect == "Rainbow" ? i % Rainbow.Length : 0;
                return Enumerable.Range(0, Rainbow.Length)
                    .Select(k => Rainbow[(k + shift) % Rainbow.Length])
                    .Select(p => new V4Phase(V4Phase.ModeMorph, duration, EffectTempo, p.R, p.G, p.B))
                    .ToArray();
            case "Breath":
                return new[]
                {
                    new V4Phase(V4Phase.ModeMorph, duration, EffectTempo, c.R, c.G, c.B),
                    new V4Phase(V4Phase.ModeMorph, duration, EffectTempo, 0, 0, 0),
                };
            case "Pulse":
                return new[] { new V4Phase(V4Phase.ModePulse, PulseDurationMs, PulseTempo[Math.Clamp(r.Duration, 0, 2)], c.R, c.G, c.B) };
            default:
                // cor fixa: o mesmo pacote padrão do COMMV4_colorSet (modo 0, 0x07D0, tempo 0xFA)
                return new[] { new V4Phase(V4Phase.ModeColor, 0x07d0, 0xfa, c.R, c.G, c.B) };
        }
    }

    private static bool TryApply(AlienFxV4Device dev, LightingRequest r)
    {
        bool ok = dev.SetBrightness((byte)Math.Clamp(r.Brightness255, 0, 255), r.Lights);

        if (r.Effect == "Static")
        {
            // agrupa luzes de mesma cor num único comando
            foreach (var group in Enumerable.Range(0, r.Lights.Count).GroupBy(i => r.Colors[i]))
            {
                var c = group.Key;
                ok &= dev.SetMultiColor(group.Select(i => r.Lights[i]).ToArray(), c.R, c.G, c.B);
            }
        }
        else if (r.Effect is "Spectrum" or "Rainbow")
        {
            // Como no OpenRGB: 7 fases "morph" passando pelas cores do arco-íris.
            // Spectrum = mesma sequência em todas as zonas; Rainbow = sequência deslocada por zona (onda).
            ushort duration = DurationMs[Math.Clamp(r.Duration, 0, 2)];
            for (int i = 0; i < r.Lights.Count; i++)
            {
                int shift = r.Effect == "Rainbow" ? i % Rainbow.Length : 0;
                var phases = Enumerable.Range(0, Rainbow.Length)
                    .Select(k => Rainbow[(k + shift) % Rainbow.Length])
                    .Select(c => new V4Phase(V4Phase.ModeMorph, duration, EffectTempo, c.R, c.G, c.B))
                    .ToList();
                ok &= dev.SetPhases(r.Lights[i], phases);
            }
        }
        else
        {
            // Igual ao AlienwareController do OpenRGB:
            //  Pulse  = 1 fase no modo pulse com a cor (velocidade pelo tempo, ver PulseTempo);
            //  Breath = 2 fases morph: a cor e depois preto.
            ushort duration = DurationMs[Math.Clamp(r.Duration, 0, 2)];
            bool breath = r.Effect == "Breath";
            for (int i = 0; i < r.Lights.Count; i++)
            {
                var c = r.Colors[i];
                var phases = breath
                    ? new[]
                    {
                        new V4Phase(V4Phase.ModeMorph, duration, EffectTempo, c.R, c.G, c.B),
                        new V4Phase(V4Phase.ModeMorph, duration, EffectTempo, 0, 0, 0),
                    }
                    : new[] { new V4Phase(V4Phase.ModePulse, PulseDurationMs, PulseTempo[Math.Clamp(r.Duration, 0, 2)], c.R, c.G, c.B) };
                ok &= dev.SetPhases(r.Lights[i], phases);
            }
        }

        ok &= dev.UpdateColors();
        return ok;
    }

    private AlienFxV4Device? EnsureDevice(int index, out string? error)
    {
        error = null;
        var supported = Supported;
        if (supported.Count == 0)
        {
            error = _devices.Count == 0
                ? "No Alienware lighting device found. Press Rescan."
                : "A device was found, but its API is not supported (only APIv4 is implemented).";
            return null;
        }
        if (index < 0 || index >= supported.Count)
        {
            error = $"Device {index} not found (detected: {supported.Count}).";
            return null;
        }

        var info = supported[index];
        if (_open is not null && _openPath == info.Path) return _open;

        CloseDevice();
        _open = AlienFxV4Device.Open(info.Path);
        if (_open is null)
        {
            error = "Could not open the device. Is Alienware Command Center running?";
            return null;
        }
        _openPath = info.Path;
        return _open;
    }

    private void CloseDevice()
    {
        _open?.Dispose();
        _open = null;
        _openPath = null;
    }

    public void Dispose()
    {
        lock (_devLock) CloseDevice();
    }
}
