using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace TccG15.Controls;

/// <summary>
/// Desenho do teclado do notebook (layout de G15 com teclado numérico), dividido em N zonas da esquerda
/// para a direita. Cada tecla usa a cor da sua zona; clicar numa tecla seleciona a zona.
/// Efeitos animados (Pulse, Breath, Spectrum, Rainbow) são simulados na tela para dar uma prévia.
/// </summary>
public sealed class KeyboardMap : Control
{
    private readonly record struct KeyCap(double X, double Y, double W, double H, string Label);

    private static readonly List<KeyCap> Keys = new();
    private static readonly double TotalW;
    private static readonly double TotalH;

    private static readonly Color[] Rainbow =
    {
        Color.FromRgb(0xFF, 0x00, 0x00), Color.FromRgb(0xFF, 0xA5, 0x00), Color.FromRgb(0xFF, 0xFF, 0x00),
        Color.FromRgb(0x00, 0xFF, 0x00), Color.FromRgb(0x00, 0xBF, 0xFF), Color.FromRgb(0x00, 0x00, 0xFF),
        Color.FromRgb(0x80, 0x00, 0xFF),
    };
    private static readonly int[] PhaseMs = { 499, 1500, 2500 };   // mesmas durações do serviço
    private static readonly int[] PulseMs = { 700, 1200, 2000 };   // aproximação visual do Pulse

    private Color[] _zones = { Color.FromRgb(0x00, 0xE5, 0xFF) };
    private int _selected = -1;
    private int _hover = -1;
    private bool _on = true;
    private string _effect = "Static";
    private int _level = 1;

    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// Estilo do G-Helper (janela do ROG Azoth): contorno de cada tecla na cor da zona e legenda clara.
    /// Sem isso, o estilo do Alienware Command Center: contorno neutro e legenda na cor da zona.
    /// </summary>
    public bool OutlineByZone { get; set; }

    /// <summary>Índice da zona clicada.</summary>
    public event Action<int>? ZoneClicked;

    static KeyboardMap()
    {
        const double Gap = 0.12;          // espaço vertical entre fileiras
        const double NumX = 15.4;         // início do teclado numérico
        double y = 0;

        void Row(double h, double startX, params (string L, double W)[] keys)
        {
            double x = startX;
            foreach (var (l, w) in keys)
            {
                if (l.Length > 0) Keys.Add(new KeyCap(x, y, w, h, l));
                x += w;
            }
        }
        (string, double) K(string l, double w = 1) => (l, w);

        // fileira de funções (mais baixa)
        Row(0.7, 0, new[] { "Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "PrtSc", "Del" }
            .Select(l => K(l)).ToArray());
        Row(0.7, NumX, K("Home"), K("PgUp"), K("PgDn"), K("End"));
        y += 0.7 + Gap;

        Row(1, 0, K("`"), K("1"), K("2"), K("3"), K("4"), K("5"), K("6"), K("7"), K("8"), K("9"), K("0"), K("-"), K("="), K("Backspace", 2));
        Row(1, NumX, K("Num"), K("/"), K("*"), K("-"));
        y += 1 + Gap;

        Row(1, 0, K("Tab", 1.5), K("Q"), K("W"), K("E"), K("R"), K("T"), K("Y"), K("U"), K("I"), K("O"), K("P"), K("["), K("]"), K("\\", 1.5));
        Row(1, NumX, K("7"), K("8"), K("9"));
        Keys.Add(new KeyCap(NumX + 3, y, 1, 2 + Gap, "+"));
        y += 1 + Gap;

        Row(1, 0, K("Caps", 1.75), K("A"), K("S"), K("D"), K("F"), K("G"), K("H"), K("J"), K("K"), K("L"), K(";"), K("'"), K("Enter", 2.25));
        Row(1, NumX, K("4"), K("5"), K("6"));
        y += 1 + Gap;

        Row(1, 0, K("Shift", 2.25), K("Z"), K("X"), K("C"), K("V"), K("B"), K("N"), K("M"), K(","), K("."), K("/"), K("Shift", 0.75), K("↑"));
        Row(1, NumX, K("1"), K("2"), K("3"));
        Keys.Add(new KeyCap(NumX + 3, y, 1, 2 + Gap, "Enter"));
        y += 1 + Gap;

        Row(1, 0, K("Ctrl", 1.25), K("Fn"), K("Win"), K("Alt", 1.25), K("", 0), K("Space", 5.5), K("Alt"), K("Ctrl"), K("←"), K("↓"), K("→"));
        Row(1, NumX, K("0", 2), K("."));
        y += 1;

        TotalW = NumX + 4;
        TotalH = y;
    }

    public KeyboardMap()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        _anim.Tick += (_, _) => InvalidateVisual();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateTimer(); };
        AttachedToVisualTree += (_, _) => UpdateTimer();
        DetachedFromVisualTree += (_, _) => _anim.Stop();
        AppTheme.Changed += InvalidateVisual;
    }

    /// <summary>Atualiza cores, seleção, liga/desliga e efeito (chamar na thread da UI).</summary>
    public void SetState(Color[] zones, int selected, bool on, string effect, int durationLevel)
    {
        _zones = zones.Length > 0 ? zones : new[] { Colors.Black };
        _selected = selected;
        _on = on;
        _effect = effect;
        _level = Math.Clamp(durationLevel, 0, 2);
        UpdateTimer();
        InvalidateVisual();
    }

    private bool Animated => _on && _effect != "Static";

    private void UpdateTimer()
    {
        if (Animated && IsEffectivelyVisible) _anim.Start();
        else _anim.Stop();
    }

    // ---------- geometria ----------

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 720 : available.Width;
        double h = w * (TotalH + 0.6) / (TotalW + 0.6);
        if (!double.IsInfinity(available.Height) && h > available.Height && available.Height > 0)
        {
            h = available.Height;
            w = h * (TotalW + 0.6) / (TotalH + 0.6);
        }
        return new Size(w, h);
    }

    private double Unit => Math.Max(1, Math.Min(Bounds.Width / (TotalW + 0.6), Bounds.Height / (TotalH + 0.6)));

    private Point Origin
    {
        get
        {
            double u = Unit;
            return new Point((Bounds.Width - TotalW * u) / 2, (Bounds.Height - TotalH * u) / 2);
        }
    }

    private int ZoneOf(KeyCap k)
    {
        int n = _zones.Length;
        return Math.Clamp((int)((k.X + k.W / 2) / TotalW * n), 0, n - 1);
    }

    private int HitZone(Point p)
    {
        double u = Unit;
        var o = Origin;
        foreach (var k in Keys)
        {
            var r = new Rect(o.X + k.X * u, o.Y + k.Y * u, k.W * u, k.H * u);
            if (r.Contains(p)) return ZoneOf(k);
        }
        return -1;
    }

    // ---------- mouse ----------

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int z = HitZone(e.GetPosition(this));
        if (z == _hover) return;
        _hover = z;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        int z = HitZone(e.GetPosition(this));
        if (z >= 0) ZoneClicked?.Invoke(z);
        e.Handled = true;
    }

    // ---------- cores (com simulação dos efeitos) ----------

    private static Color Scale(Color c, double k) =>
        Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Color PaletteAt(double pos)
    {
        int n = Rainbow.Length;
        pos %= n;
        if (pos < 0) pos += n;
        int i = (int)pos;
        return Lerp(Rainbow[i], Rainbow[(i + 1) % n], pos - i);
    }

    private Color DisplayColor(int zone)
    {
        var c = _zones[zone];
        if (!Animated) return c;

        double t = _clock.Elapsed.TotalMilliseconds;
        switch (_effect)
        {
            case "Pulse":
            {
                double k = 0.5 + 0.5 * Math.Cos(2 * Math.PI * t / PulseMs[_level]);
                return Scale(c, 0.15 + 0.85 * k);
            }
            case "Breath":
            {
                double k = 0.5 + 0.5 * Math.Cos(Math.PI * t / PhaseMs[_level]);
                return Scale(c, k);
            }
            case "Spectrum":
                return PaletteAt(t / PhaseMs[_level]);
            case "Rainbow":
                return PaletteAt(t / PhaseMs[_level] + zone);
            default:
                return c;
        }
    }

    // ---------- desenho ----------

    public override void Render(DrawingContext dc)
    {
        double u = Unit;
        var o = Origin;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size)); // hit-test

        // Estilo do Alienware Command Center: teclas só com contorno, legenda na cor da zona
        var outline = AppTheme.StrongStroke;
        var offText = AppTheme.TextTertiary;

        var colors = Enumerable.Range(0, _zones.Length).Select(DisplayColor).ToArray();
        double pad = 0.08 * u;
        double rad = 0.12 * u;
        double fontSize = Math.Max(6, 0.27 * u);
        bool labels = u >= 14;
        var typeface = new Typeface(AppTheme.UiFont, FontStyle.Normal, FontWeight.SemiBold);

        foreach (var k in Keys)
        {
            int z = ZoneOf(k);
            var c = colors[z];
            bool dim = _selected >= 0 && z != _selected;
            bool hot = z == _hover;
            bool sel = z == _selected;
            var r = new Rect(o.X + k.X * u + pad, o.Y + k.Y * u + pad, k.W * u - 2 * pad, k.H * u - 2 * pad);

            Color text;
            if (!_on) text = Color.FromArgb(0x90, offText.R, offText.G, offText.B);
            else
            {
                var vis = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 40 ? Color.FromRgb(0x5A, 0x5C, 0x63) : c;
                text = dim ? Color.FromArgb(0x70, vis.R, vis.G, vis.B) : vis;
            }

            IBrush fill = sel && _on ? new SolidColorBrush(Color.FromArgb(0x1C, c.R, c.G, c.B))
                        : hot ? new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80))
                              : Brushes.Transparent;
            var borderColor = sel && _on ? c
                            : hot ? Color.FromArgb(0xFF, outline.R, outline.G, outline.B)
                                  : Color.FromArgb((byte)(dim ? outline.A / 2 : outline.A), outline.R, outline.G, outline.B);

            double thick = sel && _on ? 1.6 : 1;
            if (OutlineByZone)
            {
                // contorno na cor da zona (cinza se apagado/preto), zona escolhida mais grossa, legenda clara
                var zc = !_on || 0.299 * c.R + 0.587 * c.G + 0.114 * c.B < 40 ? Color.FromRgb(0x5A, 0x5C, 0x63) : c;
                borderColor = dim ? Color.FromArgb(0x80, zc.R, zc.G, zc.B) : zc;
                thick = sel && _on ? 2.4 : 1.3;
                fill = hot ? new SolidColorBrush(Color.FromArgb(0x1A, zc.R, zc.G, zc.B))
                     : sel && _on ? new SolidColorBrush(Color.FromArgb(0x14, zc.R, zc.G, zc.B))
                     : Brushes.Transparent;
                var tp = AppTheme.IsDark ? Colors.White : Color.FromRgb(0x20, 0x20, 0x20);
                text = !_on ? Color.FromArgb(0x90, offText.R, offText.G, offText.B)
                     : dim ? Color.FromArgb(0x90, tp.R, tp.G, tp.B) : tp;
            }

            dc.DrawRectangle(fill, new Pen(new SolidColorBrush(borderColor), thick), r, OutlineByZone ? 0 : rad, OutlineByZone ? 0 : rad);

            if (labels)
            {
                double size = k.Label.Length > 3 ? fontSize * 0.85 : fontSize;
                var ft = new FormattedText(k.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, size, new SolidColorBrush(text))
                {
                    MaxTextWidth = Math.Max(1, r.Width - 4),
                    MaxLineCount = 1,
                    TextAlignment = TextAlignment.Center,
                };
                dc.DrawText(ft, new Point(r.X + 2, r.Y + (r.Height - ft.Height) / 2));
            }
        }
    }
}
