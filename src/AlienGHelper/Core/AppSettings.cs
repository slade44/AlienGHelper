using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TccG15.Core;

public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThermalMode Mode { get; set; } = ThermalMode.Balanced;
    public FanCurve CpuCurve { get; set; } = FanCurve.DefaultCpu();
    public FanCurve GpuCurve { get; set; } = FanCurve.DefaultGpu();
    /// <summary>"Clamp to grid" no editor de curvas.</summary>
    public bool ClampFanCurve { get; set; } = true;
    public int CpuThresholdTemp { get; set; } = 95;
    public int GpuThresholdTemp { get; set; } = 85;
    public bool FailSafeOn { get; set; } = true;
    public bool MinimizeOnClose { get; set; } = true;
    /// <summary>Já avisou que fechar a janela deixa o app na bandeja (só na primeira vez).</summary>
    public bool TrayHintShown { get; set; }
    public RgbSettings Rgb { get; set; } = new();
    public AppearanceSettings Appearance { get; set; } = new();
    /// <summary>Perfis por aplicativo/jogo. A lista é sempre substituída por uma nova ao mudar.</summary>
    public List<AppProfile> AppProfiles { get; set; } = new();
    public bool AppProfilesEnabled { get; set; } = true;
    /// <summary>Contador de FPS/frametime (sessão ETW) na aba Monitor.</summary>
    public bool FpsMonitorEnabled { get; set; } = true;
    /// <summary>Gráfico Activity na janela Monitor (desligado = oculto).</summary>
    public bool ActivityEnabled { get; set; } = true;
    /// <summary>Métrica do gráfico Activity: Temperature, Usage, Clock, Power ou Fans.</summary>
    public string ActivityMetric { get; set; } = "Temperature";
    /// <summary>Aba Power (limites da CPU via PawnIO, GPU via NVAPI).</summary>
    public PowerSettings Power { get; set; } = new();
    /// <summary>
    /// Valor bruto que a BIOS aceita como "máximo de verdade" de cada fan [CPU, GPU], medido pela calibração.
    /// 100 % da curva = este valor (100 = sem calibração; a BIOS aceita até 255).
    /// </summary>
    public int[] FanMaxBoost { get; set; } = { 100, 100 };
    /// <summary>RPM medido no máximo calibrado [CPU, GPU]; 0 = não calibrado.</summary>
    public int[] FanMaxRpm { get; set; } = { 0, 0 };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TccG15", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
                s.Sanitize();
                return s;
            }
        }
        catch { /* arquivo corrompido: volta ao padrão */ }
        return new AppSettings();
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, Serialize());
        }
        catch { }
    }

    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { }
    }

    private void Sanitize()
    {
        if (!Enum.IsDefined(Mode)) Mode = ThermalMode.Balanced;
        CpuCurve = (CpuCurve ?? FanCurve.DefaultCpu()).Normalize(FanCurve.DefaultCpu());
        GpuCurve = (GpuCurve ?? FanCurve.DefaultGpu()).Normalize(FanCurve.DefaultGpu());
        Rgb = (Rgb ?? new RgbSettings()).Sanitized();
        Appearance = (Appearance ?? new AppearanceSettings()).Sanitized();
        Power = (Power ?? new PowerSettings()).Sanitized();
        AppProfiles = (AppProfiles ?? new List<AppProfile>())
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Exe))
            .Select(p => p.Sanitized())
            .GroupBy(p => p.Exe, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .Take(AppProfile.MaxProfiles)
            .ToList();
        CpuThresholdTemp = Math.Clamp(CpuThresholdTemp, 50, 100);
        GpuThresholdTemp = Math.Clamp(GpuThresholdTemp, 50, 90);
    }
}

/// <summary>
/// Aba Power. Imutável: a UI sempre troca o objeto inteiro (<c>with</c>), porque o
/// AppController o serializa em outra thread.
/// </summary>
public sealed record PowerSettings
{
    public const int MinWatts = 10, MaxWatts = 110;

    /// <summary>Aplica SPL/sPPT/fPPT na SMU (e reaplica a cada 30 s, depois de trocar de modo e ao acordar).</summary>
    public bool ApplyCpu { get; init; }
    /// <summary>SPL = STAPM, potência sustentada.</summary>
    public int Spl { get; init; } = 45;
    /// <summary>sPPT = Slow PPT, boost longo.</summary>
    public int Sppt { get; init; } = 54;
    /// <summary>fPPT = Fast PPT, boost curto.</summary>
    public int Fppt { get; init; } = 65;
    /// <summary>Limites de fábrica lidos da SMU por modo térmico (para restaurar ao desligar). [SPL, sPPT, fPPT]</summary>
    public Dictionary<string, int[]> Stock { get; init; } = new();

    /// <summary>
    /// Limites próprios para Silent, Balanced e Turbo: com isto ligado (e "Apply" ligado), trocar de modo
    /// troca também o TDP. O modo Manual continua usando <see cref="Spl"/>/<see cref="Sppt"/>/<see cref="Fppt"/>.
    /// </summary>
    public bool LinkModes { get; init; }
    /// <summary>Por modo ("Quiet", "Balanced", "GMode"): [SPL, sPPT, fPPT] em W.</summary>
    public Dictionary<string, int[]> ModeLimits { get; init; } = DefaultModeLimits();

    public static readonly string[] LinkedModes = { "Quiet", "Balanced", "GMode" };

    /// <summary>Predefinições de TDP da janela principal: [SPL, sPPT, fPPT] em W.</summary>
    public static readonly (string Name, int[] Limits)[] Presets =
    {
        ("Eco", new[] { 15, 15, 20 }),
        ("Balanced", new[] { 45, 54, 65 }),
        ("Performance", new[] { 90, 100, 110 }),
    };

    /// <summary>Nome da predefinição que bate com os limites aplicados; null = Custom ou desligado.</summary>
    public string? ActivePreset => !ApplyCpu ? null
        : Presets.FirstOrDefault(x => x.Limits[0] == Spl && x.Limits[1] == Sppt && x.Limits[2] == Fppt).Name;

    public static Dictionary<string, int[]> DefaultModeLimits() => new()
    {
        ["Quiet"] = new[] { 25, 30, 35 },
        ["Balanced"] = new[] { 45, 54, 65 },
        ["GMode"] = new[] { 54, 65, 80 },
    };

    /// <summary>Limites que valem no modo térmico dado.</summary>
    public int[] LimitsFor(ThermalMode mode) =>
        LinkModes && mode != ThermalMode.Custom && ModeLimits.TryGetValue(mode.ToString(), out var v)
            ? v
            : new[] { Spl, Sppt, Fppt };

    public bool ApplyGpu { get; init; }
    /// <summary>Power target em % do padrão (só se o BIOS deixar mudar).</summary>
    public int GpuPowerTarget { get; init; } = 100;
    public int CoreOffset { get; init; }
    public int MemoryOffset { get; init; }
    /// <summary>Clock máximo do núcleo em MHz; 0 = sem limite.</summary>
    public int MaxGpuClock { get; init; }
    /// <summary>
    /// Undervolt da GPU: trava o clock em <see cref="UvTarget"/> e sobe a curva V/F em <see cref="UvOffset"/>,
    /// então o clock alvo sai com a tensão que antes dava só (alvo − offset). Substitui Core Offset e Max Clock.
    /// </summary>
    public bool UndervoltOn { get; init; }
    public int UvTarget { get; init; } = 1700;
    public int UvOffset { get; init; } = 150;

    public int EffectiveCoreOffset => UndervoltOn ? UvOffset : CoreOffset;
    public int EffectiveMaxClock => UndervoltOn ? UvTarget : MaxGpuClock;

    /// <summary>SPL ≤ sPPT ≤ fPPT dentro da faixa permitida.</summary>
    public static int[] Order(int[] v)
    {
        int spl = Math.Clamp(v[0], MinWatts, MaxWatts);
        int sppt = Math.Clamp(v[1], spl, MaxWatts);
        return new[] { spl, sppt, Math.Clamp(v[2], sppt, MaxWatts) };
    }

    public PowerSettings Sanitized()
    {
        int spl = Math.Clamp(Spl, MinWatts, MaxWatts);
        int sppt = Math.Clamp(Sppt, spl, MaxWatts);
        int fppt = Math.Clamp(Fppt, sppt, MaxWatts);
        return this with
        {
            Spl = spl, Sppt = sppt, Fppt = fppt,
            LinkModes = false,
            UndervoltOn = false,   // undervolt da GPU removido da versão compacta   // removido da interface: um conjunto de limites para todos os modos
            Stock = (Stock ?? new()).Where(kv => kv.Value is { Length: 3 })
                                    .ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            ModeLimits = LinkedModes.ToDictionary(k => k, k =>
                (ModeLimits ?? new()).TryGetValue(k, out var v) && v is { Length: 3 } ? Order(v) : DefaultModeLimits()[k]),
            GpuPowerTarget = Math.Clamp(GpuPowerTarget, 10, 200),
            CoreOffset = Math.Clamp(CoreOffset, -250, 250),
            MemoryOffset = Math.Clamp(MemoryOffset, -500, 500),
            MaxGpuClock = MaxGpuClock < 400 ? 0 : Math.Min(MaxGpuClock, 3000),
            UvTarget = Math.Clamp(UvTarget, 900, 2400),
            UvOffset = Math.Clamp(UvOffset, 0, 250),
        };
    }
}

/// <summary>Configuração da aba Keyboard. Sempre substituída por um objeto novo (atômico) ao mudar.</summary>
public sealed class RgbSettings
{
    public const int MaxZones = 8;
    public static readonly string[] EffectNames = { "Static", "Pulse", "Breath", "Spectrum", "Rainbow" };

    public int Device { get; set; } = 0;
    public string Lights { get; set; } = "0,1,2,3";
    public bool PerZone { get; set; } = false;
    /// <summary>Cor usada em "All zones" (separada das cores por zona, que ficam em Colors).</summary>
    public string AllColor { get; set; } = "#00E5FF";
    /// <summary>Estado do botão Turn on/off. Desligado = luzes apagadas até o usuário ligar de novo.</summary>
    public bool LightsOn { get; set; } = true;
    public string[] Colors { get; set; } = Enumerable.Repeat("#00E5FF", MaxZones).ToArray();
    public string Effect { get; set; } = "Static";
    public int Brightness { get; set; } = 100;   // 0..100 (vira 0..255 no hardware)
    /// <summary>Duração das fases dos efeitos: 0 = Low (rápido), 1 = Med, 2 = High (lento).</summary>
    public int Duration { get; set; } = 1;
    public bool ApplyOnStart { get; set; } = false;
    /// <summary>Reaplica a iluminação quando o teclado acorda (após inatividade, suspensão ou bloqueio).</summary>
    public bool RestoreOnWake { get; set; } = true;
    /// <summary>Grava as cores na memória do teclado, para valerem com o app fechado, ao ligar e ao acordar.</summary>
    public bool SaveToKeyboard { get; set; } = true;

    public RgbSettings Sanitized()
    {
        var colors = (Colors ?? Array.Empty<string>()).Take(MaxZones).ToList();
        while (colors.Count < MaxZones) colors.Add("#00E5FF");
        return new RgbSettings
        {
            Device = Math.Clamp(Device, 0, 7),
            Lights = string.IsNullOrWhiteSpace(Lights) ? "0,1,2,3" : Lights,
            PerZone = PerZone,
            AllColor = IsHex(AllColor) ? AllColor : "#00E5FF",
            LightsOn = LightsOn,
            Colors = colors.Select(c => IsHex(c) ? c : "#00E5FF").ToArray(),
            Effect = EffectNames.Contains(Effect) ? Effect : "Static",
            Brightness = Math.Clamp(Brightness, 0, 100),
            Duration = Math.Clamp(Duration, 0, 2),
            ApplyOnStart = ApplyOnStart,
            RestoreOnWake = RestoreOnWake,
            SaveToKeyboard = SaveToKeyboard,
        };
    }

    private static bool IsHex(string? s) =>
        s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>Aparência (aba Preferences). Sempre substituída por um objeto novo ao mudar.</summary>
public sealed class AppearanceSettings
{
    public static readonly string[] Themes = { "System", "Dark", "Light" };
    /// <summary>Versão compacta: só Mica e Flat (os demais estilos foram removidos).</summary>
    public static readonly string[] Styles = { "Mica", "Flat" };
    /// <summary>Variações de cor do estilo Studio.</summary>
    public static readonly string[] StudioVariants = { "Zinc", "Slate", "Stone", "Midnight", "Forest", "OLED" };

    public string Theme { get; set; } = "System";
    /// <summary>Mica (padrão), Acrylic (blur mais forte), Tabbed (Mica Alt) ou Flat (cores sólidas).</summary>
    public string Style { get; set; } = "Mica";
    public string Accent { get; set; } = "#A6E08E";
    /// <summary>Fundo do modo Flat; vazio = padrão do tema.</summary>
    public string FlatBackground { get; set; } = "";
    /// <summary>Paleta do estilo Studio (Zinc, Slate, Stone, Midnight, Forest, OLED).</summary>
    public string StudioVariant { get; set; } = "Zinc";

    public AppearanceSettings Sanitized() => new()
    {
        Theme = Themes.Contains(Theme) ? Theme : "System",
        Style = Styles.Contains(Style) ? Style : "Mica",
        Accent = IsHex(Accent) ? Accent : "#A6E08E",
        FlatBackground = "",   // Flat usa o fundo padrão do tema
        StudioVariant = StudioVariants.Contains(StudioVariant) ? StudioVariant : "Zinc",
    };

    private static bool IsHex(string? s) =>
        s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>
/// Perfil de um aplicativo/jogo: enquanto o executável estiver aberto, o app usa este modo
/// (e, no modo Manual, estas curvas). Tratado como imutável: editar = substituir por um objeto novo.
/// </summary>
public sealed class AppProfile
{
    public const int MaxProfiles = 40;

    public string Name { get; set; } = "";
    /// <summary>Nome do executável, ex.: "Cyberpunk2077.exe".</summary>
    public string Exe { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThermalMode Mode { get; set; } = ThermalMode.Custom;
    public FanCurve CpuCurve { get; set; } = FanCurve.DefaultCpu();
    public FanCurve GpuCurve { get; set; } = FanCurve.DefaultGpu();
    /// <summary>Energia da CPU enquanto o app roda: "Eco", "Balanced", "Performance" ou null (não muda).</summary>
    public string? PowerPreset { get; set; }

    /// <summary>Nome do processo sem ".exe" (como aparece em Process.ProcessName).</summary>
    [JsonIgnore]
    public string ProcessName => Path.GetFileNameWithoutExtension(Exe);

    public AppProfile Sanitized()
    {
        string exe = Path.GetFileName((Exe ?? "").Trim());
        if (exe.Length > 0 && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        return new AppProfile
        {
            Name = string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(exe) : Name.Trim(),
            Exe = exe,
            Mode = Enum.IsDefined(Mode) ? Mode : ThermalMode.Custom,
            CpuCurve = (CpuCurve ?? FanCurve.DefaultCpu()).Normalize(FanCurve.DefaultCpu()),
            GpuCurve = (GpuCurve ?? FanCurve.DefaultGpu()).Normalize(FanCurve.DefaultGpu()),
            PowerPreset = PowerSettings.Presets.Any(x => x.Name == PowerPreset) ? PowerPreset : null,
        };
    }

    public AppProfile With(Action<AppProfile> change)
    {
        var copy = new AppProfile { Name = Name, Exe = Exe, Mode = Mode, CpuCurve = CpuCurve.Clone(), GpuCurve = GpuCurve.Clone(), PowerPreset = PowerPreset };
        change(copy);
        return copy.Sanitized();
    }
}
