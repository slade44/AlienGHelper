using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Win32;
using TccG15.Core;
using TccG15.Core.Lighting;

namespace TccG15.Views;

public partial class KeyboardPage : UserControl
{
    private static readonly string[] PresetHex =
    {
        "#000000", "#FFFFFF", "#FF0000", "#FF8000", "#FFFF00", "#80FF00", "#00FF00",
        "#00FFFF", "#0080FF", "#0000FF", "#8000FF", "#FF00FF", "#FF0080", "#808080",
    };

    private static Color Accent => AppTheme.Accent;
    private const int MaxLogLines = 80;
    private const uint WakeIdleMs = 5000;   // inatividade mínima para considerar que a luz do teclado pode ter apagado

    private AppSettings _settings = null!;
    private KeyboardLightingService _svc = null!;
    private readonly Color[] _colors = new Color[RgbSettings.MaxZones];   // cores por zona
    private Color _allColor = Color.FromRgb(0x00, 0xE5, 0xFF);               // cor de "All zones"
    private bool _on = true;
    private List<int> _lights = new() { 0, 1, 2, 3 };
    private readonly DispatcherTimer _debounce;
    /// <summary>Gravação na memória do teclado: só depois de alguns segundos sem mudanças (poupa a flash).</summary>
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly List<string> _log = new();
    private readonly DispatcherTimer _idleWatch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _wasIdle;
    private int _sel;
    private bool _perZone;
    private bool _loading = true;   // durante a inicialização os handlers não fazem nada
    private bool _sync;             // evita eco ao atualizar controles por código

    public KeyboardPage()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _svc.Submit(BuildRequest());
        };
        KeyMap.ZoneClicked += SelectZone;
        Wheel.ColorPicked += c => { if (!_loading) SetColor(c); };
        BrightSlider.OnValueChanged(Level_ValueChanged);
        DurationSlider.OnValueChanged(Level_ValueChanged);
        PowerToggle.OnCheckedChanged(Power_Changed);
        ApplyStartToggle.OnCheckedChanged(Startup_Changed);
        RestoreWakeToggle.OnCheckedChanged(Startup_Changed);
        SaveKeyboardToggle.OnCheckedChanged(() =>
        {
            Startup_Changed();
            if (!_loading && SaveKeyboardToggle.IsChecked == true) ScheduleSave();
        });
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            if (_svc.Supported.Count == 0 || SaveKeyboardToggle.IsChecked != true) return;
            _svc.Submit(BuildRequest() with { Persist = true });
        };
        // Esc fecha o painel Personalizado (ouvido antes dos filhos, como o PreviewKeyDown do WPF)
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && CustomPanel.IsVisible)
            {
                CloseCustomPanel();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    // ---------- painel Personalizado (abre sobre Color / Preset colors) ----------

    private void CustomOpen_Click(object? sender, RoutedEventArgs e) => CustomPanel.IsVisible = true;

    private void CustomClose_Click(object? sender, RoutedEventArgs e) => CloseCustomPanel();

    private void CloseCustomPanel() => CustomPanel.IsVisible = false;

    public void Initialize(AppSettings settings, KeyboardLightingService service)
    {
        _settings = settings;
        _svc = service;
        var r = settings.Rgb;

        for (int i = 0; i < _colors.Length; i++)
            _colors[i] = TryParseHex(r.Colors[i], out var c) ? c : Color.FromRgb(0x00, 0xE5, 0xFF);
        _allColor = TryParseHex(r.AllColor, out var ac) ? ac : Color.FromRgb(0x00, 0xE5, 0xFF);
        _on = r.LightsOn;
        _perZone = r.PerZone;
        _lights = ParseLights(r.Lights);

        foreach (var e in RgbSettings.EffectNames) EffectBox.Items.Add(e);
        EffectBox.SelectedItem = r.Effect;
        for (int d = 0; d < 8; d++) DeviceBox.Items.Add(d);
        DeviceBox.SelectedItem = r.Device;
        BrightSlider.Value = r.Brightness;
        DurationSlider.Value = r.Duration;
        LightsBox.Text = string.Join(",", _lights);
        ApplyStartToggle.IsChecked = r.ApplyOnStart;
        RestoreWakeToggle.IsChecked = r.RestoreOnWake;
        SaveKeyboardToggle.IsChecked = r.SaveToKeyboard;
        PowerToggle.IsChecked = _on;

        BuildPresets();
        UpdateUi();

        _svc.Finished += (ok, msg) => Dispatcher.UIThread.Post(() => ShowResult(ok, msg));
        StartWakeWatch();
        _loading = false;
        QuickStateChanged?.Invoke();
        if (!_on) ShowOffHint();

        // Detecta o teclado em background; ao terminar, atualiza a tela e (se pedido) aplica ao iniciar
        Task.Run(_svc.Refresh).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            RefreshDevice();
            if (_settings.Rgb.ApplyOnStart && _svc.Supported.Count > 0) _svc.Submit(BuildRequest());
            // garante que a memória do teclado tenha as cores atuais (ex.: depois de usar o AWCC)
            if (_svc.Supported.Count > 0) ScheduleSave();
        }));
    }

    /// <summary>Redesenha o que usa a cor de destaque (chamado quando a aparência muda).</summary>
    public void RefreshTheme()
    {
        if (_settings is null || _loading) return;
        UpdateUi();
    }

    // ---------- controles rápidos (janela compacta) ----------
    // Passam pela mesma lógica da página: salvam as configurações, aplicam e gravam na memória do teclado.

    /// <summary>O estado mostrado mudou (cor, efeito, brilho, ligado/desligado).</summary>
    public event Action? QuickStateChanged;

    public static IReadOnlyList<string> PresetColors => PresetHex;
    public bool IsReady => _settings is not null && !_loading;
    public bool LightsOn => _on;
    public string LightingEffect => SelectedEffect;
    public Color MainColor => CurrentColor;
    public int Brightness => (int)BrightSlider.Value;

    public void QuickSetOn(bool on)
    {
        if (!IsReady || on == _on) return;
        PowerToggle.IsChecked = on;   // dispara Power_Changed
    }

    public void QuickSetEffect(string effect)
    {
        if (!IsReady || effect == SelectedEffect || !RgbSettings.EffectNames.Contains(effect)) return;
        EffectBox.SelectedItem = effect;   // dispara Effect_Changed
    }

    /// <summary>Uma cor para o teclado todo (como no G-Helper): volta para "All zones".</summary>
    public void QuickSetColor(Color c)
    {
        if (!IsReady) return;
        _perZone = false;
        _allColor = c;
        UpdateUi();
        Changed();
    }

    public void QuickSetBrightness(int percent)
    {
        if (!IsReady) return;
        BrightSlider.Value = Math.Clamp(percent, 0, 100);   // dispara Level_ValueChanged
    }

    // ---------- estado ----------

    private string SelectedEffect => EffectBox.SelectedItem as string ?? "Static";

    private Color ZoneColor(int i) => _perZone ? _colors[i] : _allColor;

    private Color CurrentColor => _perZone ? _colors[_sel] : _allColor;

    /// <summary>Monta o pedido a partir do estado da tela. Com as luzes desligadas, manda preto.</summary>
    private LightingRequest BuildRequest()
    {
        var lights = _lights.Select(l => (byte)l).ToArray();
        var colors = lights.Select((_, i) => _on ? (ZoneColor(i).R, ZoneColor(i).G, ZoneColor(i).B)
                                                 : (R: (byte)0, G: (byte)0, B: (byte)0)).ToArray();
        string effect = _on ? SelectedEffect : "Static";
        int dev = DeviceBox.SelectedItem is int d ? d : 0;
        int hw = (int)Math.Round(BrightSlider.Value * 255.0 / 100.0);
        return new LightingRequest(dev, lights, colors, effect, (int)DurationSlider.Value, hw);
    }

    private void Changed()
    {
        if (_loading) return;
        SaveSettings();
        if (!_on)
        {
            // Luzes desligadas: a mudança fica salva e só aparece quando o usuário ligar de novo
            ShowOffHint();
            return;
        }
        _debounce.Stop();
        _debounce.Start();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        if (SaveKeyboardToggle.IsChecked != true) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void SaveSettings() => _settings.Rgb = new RgbSettings
    {
        Device = DeviceBox.SelectedItem is int d ? d : 0,
        Lights = string.Join(",", _lights),
        PerZone = _perZone,
        AllColor = Hex(_allColor),
        LightsOn = _on,
        Colors = _colors.Select(Hex).ToArray(),
        Effect = SelectedEffect,
        Brightness = (int)BrightSlider.Value,
        Duration = (int)DurationSlider.Value,
        ApplyOnStart = ApplyStartToggle.IsChecked == true,
        RestoreOnWake = RestoreWakeToggle.IsChecked == true,
        SaveToKeyboard = SaveKeyboardToggle.IsChecked == true,
    };

    // ---------- seleção ----------

    private void SelectZone(int index)
    {
        if (_loading || index < 0 || index >= _lights.Count) return;
        bool changedScope = !_perZone;
        _perZone = true;
        _sel = index;
        UpdateUi();
        if (changedScope) Changed();   // passar de "All zones" para uma zona muda o teclado
    }

    private void SelectAll()
    {
        if (_loading || !_perZone) return;
        _perZone = false;
        UpdateUi();
        Changed();
    }

    /// <summary>Aplica a cor na seleção atual (todas as zonas ou a zona escolhida).</summary>
    private void SetColor(Color c)
    {
        if (_perZone) _colors[_sel] = c;
        else _allColor = c;
        UpdateUi();
        Changed();
    }

    // ---------- UI ----------

    private void UpdateUi()
    {
        _sel = Math.Clamp(_sel, 0, Math.Max(0, _lights.Count - 1));
        _sync = true;

        var c = CurrentColor;
        Swatch.Background = new SolidColorBrush(c);
        SwatchTitle.Text = _perZone ? $"Zone {_sel + 1}" : "All zones";
        SwatchHex.Text = $"{Hex(c)}  ·  {c.R}, {c.G}, {c.B}";
        BoxR.Text = c.R.ToString(); BoxG.Text = c.G.ToString(); BoxB.Text = c.B.ToString();
        HexBox.Text = Hex(c);
        Wheel.SetColor(c);

        BrightVal.Text = $"{(int)BrightSlider.Value} %";
        DurationRow.IsVisible = SelectedEffect != "Static";

        KeyMap.SetState(Enumerable.Range(0, _lights.Count).Select(ZoneColor).ToArray(),
            _perZone ? _sel : -1, _on, SelectedEffect, (int)DurationSlider.Value);
        BuildZoneList();
        _sync = false;
        QuickStateChanged?.Invoke();
    }

    /// <summary>Lista de zonas no estilo do AWCC: nome + quadrado com a cor da zona.</summary>
    private void BuildZoneList()
    {
        ZoneList.Children.Clear();
        ZoneList.Children.Add(MakeZoneItem("All zones", _allColor, !_perZone, SelectAll));
        for (int i = 0; i < _lights.Count; i++)
        {
            int idx = i;
            ZoneList.Children.Add(MakeZoneItem($"Zone {i + 1}", _colors[i], _perZone && _sel == i, () => SelectZone(idx)));
        }
    }

    private static Border MakeZoneItem(string text, Color color, bool active, Action onClick)
    {
        var label = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        if (active) label.FontWeight = FontWeight.SemiBold;
        else label.Classes.Add("secondary");
        var square = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(14, 0, 0, 0),
            Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(label);
        panel.Children.Add(square);

        var item = new Border
        {
            Padding = new Thickness(12, 7, 10, 7),
            Margin = new Thickness(0, 0, 8, 8),
            Child = panel,
        };
        item.Classes.Add("item");
        item.Classes.Set("active", active);
        item.PointerReleased += (_, _) => onClick();
        return item;
    }

    private static Border MakeSquare(Color color, string tip)
    {
        var sq = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(0),
            Margin = new Thickness(0, 0, 2, 2),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = new SolidColorBrush(color),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
        };
        ToolTip.SetTip(sq, tip);
        return sq;
    }

    private void BuildPresets()
    {
        Presets.Children.Clear();
        foreach (var hex in PresetHex)
        {
            TryParseHex(hex, out var c);
            var sq = MakeSquare(c, hex);
            sq.PointerReleased += (_, _) => SetColor(c);
            Presets.Children.Add(sq);
        }
    }

    /// <summary>Mostra o que foi detectado (chamar na thread da UI).</summary>
    private void RefreshDevice()
    {
        var supported = _svc.Supported;
        bool ok = supported.Count > 0;
        DevDot.Fill = new SolidColorBrush(ok ? Color.FromRgb(0x34, 0xA8, 0x53)
                                             : _svc.Devices.Count > 0 ? Color.FromRgb(0xF1, 0xC2, 0x32)
                                                                      : Color.FromRgb(0xF4, 0x43, 0x36));
        var first = supported.FirstOrDefault() ?? _svc.Devices.FirstOrDefault();
        DeviceSubtitle.Text = first is null
            ? "No Alienware lighting device found"
            : $"{(string.IsNullOrWhiteSpace(first.Description) ? "AlienFX device" : first.Description)}" +
              $"{(ok ? "" : " · not supported")} · {_lights.Count} zones";

        if (_svc.Devices.Count == 0)
        {
            DeviceInfo.Text = "No Alienware USB lighting device (VID 187C) was found. " +
                              "Some keyboards are not controlled over USB HID; if Rescan finds nothing, this model may not be supported.";
            AppendLog("Scan: no device found");
        }
        else
        {
            DeviceInfo.Text = string.Join("\n", _svc.Devices.Select(d =>
                d.IsSupported ? $"#{supported.IndexOf(d)}  {d}" : $"–   {d}"));
            foreach (var d in _svc.Devices) AppendLog($"Scan: {d}");
        }
    }

    private void ShowResult(bool ok, string message)
    {
        if (ok && message == "saved")
        {
            AppendLog("Saved to the keyboard memory (used with the app closed, at boot and after sleep)");
            if (!_on) ShowOffHint();
            return;
        }
        if (ok && !_on) { ShowOffHint(); return; }
        if (ok)
        {
            ResultText.Text = "";
            AppendLog($"Applied: {SelectedEffect}, {(_perZone ? "per zone" : "all zones")}, {(int)BrightSlider.Value}%");
        }
        else
        {
            ResultText.Text = message;
            ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
            AppendLog($"Error: {message}");
        }
    }

    private void ShowOffHint()
    {
        ResultText.Text = "Lights are off – changes are saved and shown when you turn them on";
        ResultText.ClearValue(TextBlock.ForegroundProperty);   // volta ao estilo (texto terciário)
    }

    private void AppendLog(string text)
    {
        _log.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
        if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
        LogBox.Text = string.Join("\n", _log);
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    // ---------- reaplicar quando o teclado acorda ----------

    /// <summary>
    /// O app usa o espaço de animação temporário do controlador (o mesmo do OpenRGB e do alienfx-tools),
    /// que não é gravado na memória do teclado. Quando o firmware apaga a luz por inatividade, ou depois
    /// de suspender, ele pode reacender sem as nossas cores. Por isso reaplicamos a iluminação:
    ///  - na primeira tecla/mouse depois de ≥ 5 s sem uso;
    ///  - ao voltar da suspensão e ao desbloquear o Windows.
    /// </summary>
    private void StartWakeWatch()
    {
        _idleWatch.Tick += (_, _) =>
        {
            uint idle = IdleMilliseconds();
            if (idle >= WakeIdleMs) { _wasIdle = true; return; }
            if (_wasIdle && idle < 1000)
            {
                _wasIdle = false;
                Reapply("activity after idle");
            }
        };
        _idleWatch.Start();

        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) ReapplyLater("resume from sleep");
        };
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
                ReapplyLater("Windows unlocked");
        };
    }

    /// <summary>Eventos de sistema chegam em outra thread e o USB pode demorar a voltar: espera um pouco.</summary>
    private void ReapplyLater(string reason) =>
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(1500);
            Reapply(reason);
        });

    private void Reapply(string reason)
    {
        if (!_on || RestoreWakeToggle.IsChecked != true || _svc.Supported.Count == 0) return;
        _svc.Submit(BuildRequest());
        AppendLog($"Re-applied ({reason})");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Há quanto tempo não há entrada de teclado/mouse na sessão.</summary>
    private static uint IdleMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.dwTime) : 0;
    }

    // ---------- eventos ----------

    private void RgbBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitRgb(); e.Handled = true; }
    }

    private void RgbBox_LostFocus(object? sender, RoutedEventArgs e) => CommitRgb();

    private void CommitRgb()
    {
        if (_loading || _sync) return;
        static byte Parse(string t, byte fallback) =>
            int.TryParse(t.Trim(), out int v) ? (byte)Math.Clamp(v, 0, 255) : fallback;
        var cur = CurrentColor;
        var c = Color.FromRgb(Parse(BoxR.Text ?? "", cur.R), Parse(BoxG.Text ?? "", cur.G), Parse(BoxB.Text ?? "", cur.B));
        if (c != cur) SetColor(c);
        else UpdateUi();   // texto inválido: volta ao valor atual
    }

    private void HexBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; }
    }

    private void HexBox_LostFocus(object? sender, RoutedEventArgs e) => CommitHex();

    private void CommitHex()
    {
        if (_loading || _sync) return;
        if (TryParseHex(HexBox.Text, out var c)) SetColor(c);
        else UpdateUi();
    }

    private void Level_ValueChanged()
    {
        if (_loading || _sync) return;
        UpdateUi();
        Changed();
    }

    private void Effect_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateUi();
        Changed();
    }

    private void Device_Changed(object? sender, SelectionChangedEventArgs e) => Changed();

    private void Lights_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _lights = ParseLights(LightsBox.Text);
        LightsBox.Text = string.Join(",", _lights);
        UpdateUi();
        Changed();
    }

    private void Startup_Changed()
    {
        if (_loading) return;
        SaveSettings();
    }

    private void Power_Changed()
    {
        if (_loading) return;
        bool want = PowerToggle.IsChecked == true;
        if (want == _on) return;
        _on = want;
        UpdateUi();
        SaveSettings();
        _debounce.Stop();
        _svc.Submit(BuildRequest());   // aplica na hora: preto ao desligar, cores salvas ao ligar
        ScheduleSave();                // e grava no teclado (desligado = fica apagado também ao ligar o PC)
        if (_on) ResultText.Text = "";
        else ShowOffHint();
        AppendLog(_on ? "Lights on" : "Lights off");
    }

    private async void Rescan_Click(object? sender, RoutedEventArgs e)
    {
        BtnRescan.IsEnabled = false;
        DeviceSubtitle.Text = "Searching…";
        await Task.Run(_svc.Refresh);
        RefreshDevice();
        BtnRescan.IsEnabled = true;
    }

    private async void Identify_Click(object? sender, RoutedEventArgs e)
    {
        if (_svc.Supported.Count == 0)
        {
            AppendLog("Identify: no supported device. Press Rescan first.");
            return;
        }

        BtnIdentify.IsEnabled = false;
        var lights = _lights.Select(l => (byte)l).ToArray();
        AppendLog($"Identify: lighting each light alone, ~1 s each: {string.Join(", ", _lights)}");
        var (ok, msg) = await _svc.IdentifyAsync(
            DeviceBox.SelectedItem is int d ? d : 0,
            lights,
            BuildRequest(),
            id => Dispatcher.UIThread.Post(() => AppendLog($"Identify: light {id} is lit")));
        AppendLog(ok ? "Identify: done, your colors were restored" : $"Identify: {msg}");
        BtnIdentify.IsEnabled = true;
    }

    // ---------- utilidades ----------

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseHex(string? s, out Color c)
    {
        s = (s ?? "").Trim().TrimStart('#');
        if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
        {
            c = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }
        c = default;
        return false;
    }

    private static List<int> ParseLights(string? text)
    {
        var ids = (text ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => int.TryParse(t, out int v) ? v : -1)
            .Where(v => v is >= 0 and <= 255)
            .Distinct()
            .Take(RgbSettings.MaxZones)
            .ToList();
        return ids.Count > 0 ? ids : new List<int> { 0, 1, 2, 3 };
    }
}
