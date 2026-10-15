using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TccG15.Core;

namespace TccG15;

/// <summary>
/// Aparência do app no Avalonia: tema (System/Dark/Light), estilo da janela (Mica, Acrylic, Tabbed ou Flat),
/// cor de destaque e fundo do modo Flat. As cores ficam em recursos (DynamicResource) do App, então a
/// maior parte da UI se atualiza sozinha; quem desenha à mão escuta <see cref="Changed"/>.
/// </summary>
public static class AppTheme
{
    public static readonly Color DefaultAccent = Color.FromRgb(0xA6, 0xE0, 0x8E);

    public static Color Accent { get; private set; } = DefaultAccent;
    public static IBrush AccentBrush { get; private set; } = new SolidColorBrush(DefaultAccent);
    /// <summary>Cor de texto legível sobre a cor de destaque.</summary>
    public static IBrush OnAccentBrush { get; private set; } = new SolidColorBrush(OnAccent(DefaultAccent));
    public static bool IsDark { get; private set; } = true;

    /// <summary>Cores de texto para os controles desenhados à mão (gráficos, teclado).</summary>
    public static IBrush TextSecondaryBrush => new SolidColorBrush(IsDark ? Color.FromArgb(0xC5, 255, 255, 255) : Color.FromArgb(0x9E, 0, 0, 0));
    public static Color TextTertiary => IsDark ? Color.FromArgb(0x87, 255, 255, 255) : Color.FromArgb(0x72, 0, 0, 0);
    public static Color StrongStroke => IsDark ? Color.FromArgb(0x8B, 255, 255, 255) : Color.FromArgb(0x72, 0, 0, 0);

    /// <summary>Fonte dos controles desenhados à mão (curvas, teclado).</summary>
    public static readonly FontFamily UiFont = new("Segoe UI");

    public static event Action? Changed;

    /// <summary>Janelas que seguem a aparência (a completa e a compacta).</summary>
    private static readonly List<Window> _windows = new();
    private static AppearanceSettings _current = new();
    private static bool _hooked;

    public static AppearanceSettings Current => _current;

    /// <summary>Aplica as configurações em todas as janelas registradas (pode ser chamado a qualquer momento).</summary>
    public static void Apply(Window window, AppearanceSettings settings)
    {
        Register(window);
        _current = settings.Sanitized();

        Accent = TryParseHex(_current.Accent, out var a) ? a : DefaultAccent;
        AccentBrush = new SolidColorBrush(Accent);
        OnAccentBrush = new SolidColorBrush(OnAccent(Accent));

        var app = Application.Current!;
        if (!_hooked)
        {
            _hooked = true;
            // tema do Windows mudou (modo System)
            app.ActualThemeVariantChanged += (_, _) => ApplyColors();
        }

        bool studio = _current.Style == "Studio";
        app.RequestedThemeVariant = studio ? ThemeVariant.Dark : _current.Theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "Light" => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };

        foreach (var w in _windows) ApplyWindowStyle(w);
        ApplyColors();
    }

    /// <summary>Passa a aplicar a aparência atual nesta janela também.</summary>
    public static void Register(Window window)
    {
        if (_windows.Contains(window)) return;
        _windows.Add(window);
        // o backdrop real mudou (ex.: Mica indisponível no Windows 10) ou a janela abriu
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty) ApplyColors();
        };
        window.Opened += (_, _) => ApplyColors();
        ApplyWindowStyle(window);
    }

    private static void ApplyWindowStyle(Window window)
    {
        window.Classes.Set("studio", _current.Style == "Studio");
        // Avalonia não tem "Mica Alt": Tabbed usa o Mica com um véu um pouco mais forte nos cartões
        window.TransparencyLevelHint = _current.Style switch
        {
            "Acrylic" => new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Mica, WindowTransparencyLevel.None },
            "Flat" or "Studio" => new[] { WindowTransparencyLevel.None },
            _ => new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None },
        };
    }

    private static void ApplyColors()
    {
        var app = Application.Current;
        if (_windows.Count == 0 || app is null) return;

        IsDark = app.ActualThemeVariant == ThemeVariant.Dark;
        var res = app.Resources;

        // cor de destaque: nos nossos recursos e nos do tema Fluent (checkbox, slider, toggle...)
        res["AccentBrush"] = AccentBrush;
        res["OnAccentBrush"] = OnAccentBrush;
        res["AccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(0x1F, Accent.R, Accent.G, Accent.B));
        bool studio = _current.Style == "Studio";
        // seletores: no Studio o ativo é "botão branco" (como o primário do shadcn/ui); nos demais, a cor de destaque
        res["SegActiveBrush"] = studio ? new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)) : AccentBrush;
        res["SegActiveTextBrush"] = studio ? new SolidColorBrush(Color.FromRgb(0x09, 0x09, 0x0B)) : OnAccentBrush;
        res["SystemAccentColor"] = Accent;
        res["SystemAccentColorLight1"] = Shift(Accent, 18);
        res["SystemAccentColorLight2"] = Shift(Accent, 36);
        res["SystemAccentColorLight3"] = Shift(Accent, 54);
        res["SystemAccentColorDark1"] = Shift(Accent, -18);
        res["SystemAccentColorDark2"] = Shift(Accent, -36);
        res["SystemAccentColorDark3"] = Shift(Accent, -54);

        if (studio)
        {
            // paleta "zinc" escura: fundo quase preto, cartões um tom acima, bordas finas e cinzas neutros
            var v = StudioVariants.TryGetValue(_current.StudioVariant, out var pal) ? pal : StudioVariants["Zinc"];
            for (int k = 0; k < StudioKeys.Length; k++)
                res[StudioKeys[k]] = new SolidColorBrush(Color.Parse(v.Colors[k]));
            foreach (var w in _windows)
            {
                w.Background = new SolidColorBrush(Color.Parse(v.Background));
                SetDarkFrame(w, true);
            }
            Changed?.Invoke();
            return;
        }
        foreach (var key in StudioKeys)
            if (key is not ("CardBrush" or "CardStrokeBrush")) res.Remove(key);

        bool flatStyle = _current.Style == "Flat";
        var solidBg = flatStyle ? FlatBackground(IsDark, _current.FlatBackground)
                                : IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        foreach (var w in _windows)
        {
            bool noBackdrop = w.ActualTransparencyLevel == WindowTransparencyLevel.None;
            w.Background = flatStyle || noBackdrop ? new SolidColorBrush(solidBg) : Brushes.Transparent;
            SetDarkFrame(w, IsDark);
        }

        if (flatStyle)
        {
            var card = IsDark ? Shift(solidBg, 10) : (Luma(solidBg) > 245 ? Colors.White : Shift(solidBg, 10));
            var border = IsDark ? Shift(solidBg, 26) : Shift(solidBg, -22);
            res["CardBrush"] = new SolidColorBrush(card);
            res["CardStrokeBrush"] = new SolidColorBrush(border);
        }
        else if (_current.Style == "Tabbed")
        {
            res["CardBrush"] = new SolidColorBrush(IsDark ? Color.FromArgb(0x18, 255, 255, 255) : Color.FromArgb(0xD0, 255, 255, 255));
            res.Remove("CardStrokeBrush");
        }
        else ClearCardOverrides(res);

        Changed?.Invoke();
    }

    /// <summary>Chaves de recurso trocadas pelo Studio (mesma ordem dos valores em <see cref="StudioVariants"/>).</summary>
    private static readonly string[] StudioKeys =
    {
        "CardBrush", "CardStrokeBrush", "ControlFillBrush", "ControlFillSecondaryBrush", "ControlStrokeBrush",
        "SubtleFillBrush", "TextPrimaryBrush", "TextSecondaryBrush", "TextTertiaryBrush", "LayerBrush",
    };

    /// <summary>
    /// Paletas do Studio: fundo da janela + os 10 recursos acima.
    /// Zinc/Slate/Stone seguem as escalas neutras do Tailwind (as mesmas do shadcn/ui).
    /// </summary>
    public static readonly Dictionary<string, (string Background, string[] Colors, string Swatch)> StudioVariants = new()
    {
        ["Zinc"] = ("#09090B", new[] { "#0E0E10", "#232326", "#141416", "#1C1C1F", "#27272A", "#18181B", "#FAFAFA", "#A1A1AA", "#71717A", "#18181B" }, "#27272A"),
        ["Slate"] = ("#020617", new[] { "#0B1222", "#1E293B", "#0F172A", "#162033", "#1E293B", "#131C2E", "#F8FAFC", "#94A3B8", "#64748B", "#0F172A" }, "#1E293B"),
        ["Stone"] = ("#0C0A09", new[] { "#12100E", "#292524", "#171412", "#1F1B18", "#292524", "#1C1917", "#FAFAF9", "#A8A29E", "#78716C", "#1C1917" }, "#292524"),
        ["Midnight"] = ("#0A0B14", new[] { "#10121F", "#22253A", "#151829", "#1C2035", "#272B44", "#171A2C", "#F5F6FF", "#A3A8C9", "#6E7396", "#151829" }, "#272B44"),
        ["Forest"] = ("#070B09", new[] { "#0C120F", "#1D2A23", "#101813", "#16211B", "#22322A", "#131C17", "#F3FAF6", "#9FB5A9", "#6B8276", "#101813" }, "#22322A"),
        ["OLED"] = ("#000000", new[] { "#050505", "#1A1A1A", "#0A0A0A", "#141414", "#222222", "#101010", "#FFFFFF", "#A3A3A3", "#6B6B6B", "#111111" }, "#000000"),
    };

    private static void ClearCardOverrides(Avalonia.Controls.IResourceDictionary res)
    {
        res.Remove("CardBrush");
        res.Remove("CardStrokeBrush");
    }

    // ---------- moldura escura (barra de título e bordas do Windows) ----------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void SetDarkFrame(Window window, bool dark)
    {
        var h = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (h == IntPtr.Zero) return;
        int v = dark ? 1 : 0;
        try { DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)); }
        catch (DllNotFoundException) { }
    }

    // ---------- cores ----------

    public static readonly string[] AccentPresets =
        { "#A6E08E", "#4ADE80", "#FF5A1F", "#FFB020", "#4C9AFF", "#22D3EE", "#A78BFA", "#F472B6", "#F87171" };

    public static readonly string[] DarkBackgrounds = { "#0F0F12", "#16171B", "#1C1C1E", "#0E1218", "#15110E" };
    public static readonly string[] LightBackgrounds = { "#F3F3F5", "#FFFFFF", "#EEF1F5", "#F6F1EA" };

    /// <summary>Fundo do Flat: o escolhido, se combinar com o tema (escuro no Dark, claro no Light); senão o padrão.</summary>
    public static Color FlatBackground(bool dark, string? hex)
    {
        if (TryParseHex(hex, out var c) && (dark ? Luma(c) < 90 : Luma(c) > 170)) return c;
        TryParseHex(dark ? DarkBackgrounds[0] : LightBackgrounds[0], out c);
        return c;
    }

    public static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

    private static Color OnAccent(Color c) => Luma(c) > 150 ? Color.FromRgb(0x14, 0x16, 0x14) : Colors.White;

    private static Color Shift(Color c, int d) => Color.FromRgb(
        (byte)Math.Clamp(c.R + d, 0, 255), (byte)Math.Clamp(c.G + d, 0, 255), (byte)Math.Clamp(c.B + d, 0, 255));

    public static IBrush Brush(Color c) => new SolidColorBrush(c);

    public static bool TryParseHex(string? s, out Color c)
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

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
