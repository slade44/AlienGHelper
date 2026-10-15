using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AlienGHelper.Controls;

/// <summary>
/// Roda de cores (matiz no ângulo, saturação no raio, brilho máximo), no estilo do Alienware Command Center.
/// Clique ou arraste para escolher; <see cref="ColorPicked"/> é disparado a cada mudança.
/// </summary>
public sealed class ColorWheel : Control
{
    private WriteableBitmap? _bmp;
    private int _bmpPx;
    private double _hue;        // 0..360
    private double _sat = 1;    // 0..1
    private Color _color = Color.FromRgb(0x00, 0xE5, 0xFF);
    private bool _dragging;

    public event Action<Color>? ColorPicked;

    public ColorWheel()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        SetColor(_color);
    }

    /// <summary>Posiciona o marcador na cor dada (sem disparar o evento).</summary>
    public void SetColor(Color c)
    {
        _color = c;
        ToHs(c, out _hue, out _sat);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        double d = 180;
        if (!double.IsInfinity(available.Width)) d = Math.Min(d, available.Width);
        if (!double.IsInfinity(available.Height)) d = Math.Min(d, available.Height);
        return new Size(d, d);
    }

    private double Diameter => Math.Max(10, Math.Min(Bounds.Width, Bounds.Height));
    private Point Center => new(Bounds.Width / 2, Bounds.Height / 2);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Pick(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void Pick(Point p)
    {
        var c = Center;
        double dx = p.X - c.X, dy = p.Y - c.Y;
        double r = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / (Diameter / 2));
        double h = Math.Atan2(-dy, dx) * 180 / Math.PI;
        if (h < 0) h += 360;

        _hue = h;
        _sat = r;
        _color = FromHsv(_hue, _sat, 1);
        InvalidateVisual();
        ColorPicked?.Invoke(_color);
    }

    public override void Render(DrawingContext dc)
    {
        double d = Diameter;
        var c = Center;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int px = Math.Max(16, (int)Math.Ceiling(d * scale));
        if (_bmp is null || _bmpPx != px) BuildBitmap(px);

        var rect = new Rect(c.X - d / 2, c.Y - d / 2, d, d);
        dc.DrawImage(_bmp!, rect);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), 1), c, d / 2, d / 2);

        double rad = _hue * Math.PI / 180;
        var m = new Point(c.X + Math.Cos(rad) * _sat * d / 2, c.Y - Math.Sin(rad) * _sat * d / 2);
        dc.DrawEllipse(new SolidColorBrush(_color), new Pen(Brushes.White, 2.5), m, 7, 7);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1), m, 8.5, 8.5);
    }

    private void BuildBitmap(int px)
    {
        _bmpPx = px;
        _bmp?.Dispose();
        _bmp = new WriteableBitmap(new PixelSize(px, px), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var pixels = new byte[px * px * 4];
        double rMax = px / 2.0;

        for (int y = 0; y < px; y++)
        {
            for (int x = 0; x < px; x++)
            {
                double dx = x + 0.5 - rMax, dy = y + 0.5 - rMax;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double alpha = Math.Clamp(rMax - dist, 0, 1);   // borda suavizada (1 px)
                if (alpha <= 0) continue;

                double h = Math.Atan2(-dy, dx) * 180 / Math.PI;
                if (h < 0) h += 360;
                var col = FromHsv(h, Math.Min(1, dist / rMax), 1);

                int i = (y * px + x) * 4;
                pixels[i + 0] = (byte)(col.B * alpha);
                pixels[i + 1] = (byte)(col.G * alpha);
                pixels[i + 2] = (byte)(col.R * alpha);
                pixels[i + 3] = (byte)(255 * alpha);
            }
        }

        using var fb = _bmp.Lock();
        for (int y = 0; y < px; y++)
            Marshal.Copy(pixels, y * px * 4, fb.Address + y * fb.RowBytes, px * 4);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static void ToHs(Color c, out double h, out double s)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        if (delta <= 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * ((b - r) / delta + 2);
        else h = 60 * ((r - g) / delta + 4);
        if (h < 0) h += 360;

        s = max <= 0 ? 0 : delta / max;
    }
}
