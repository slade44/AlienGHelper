using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AlienGHelper.Core;

namespace AlienGHelper.Controls;

/// <summary>
/// Editor gráfico da curva temperatura → velocidade (estilo G-Helper).
///  • arrastar um ponto: move (não ultrapassa os vizinhos)
///  • duplo clique em área vazia: adiciona ponto
///  • botão direito num ponto: remove (mínimo de 2 pontos)
/// Desenha também a temperatura atual e a velocidade que está sendo aplicada.
/// </summary>
public sealed class CurveEditor : Control
{
    private const double PadL = 44, PadR = 16, PadT = 16, PadB = 30, HitRadius = 12;

    private FanCurve _curve = FanCurve.DefaultGpu();
    private int? _liveTemp, _liveSpeed;
    private int _drag = -1, _hover = -1;
    private Rect _plot;

    /// <summary>Cor da curva (null = cor de destaque do tema). CPU azul, GPU vermelha.</summary>
    public Color? CurveColor { get; set; }

    /// <summary>"Clamp to grid": pontos andam de 5 em 5 (°C e %).</summary>
    public bool SnapToGrid { get; set; }

    private bool _readOnly;
    /// <summary>Somente leitura: mostra a curva, mas não deixa mover, adicionar nem remover pontos.</summary>
    public bool IsReadOnly
    {
        get => _readOnly;
        set
        {
            if (_readOnly == value) return;
            _readOnly = value;
            _drag = -1;
            _hover = -1;
            Cursor = Cursor.Default;
            InvalidateVisual();
        }
    }

    /// <summary>Disparado ao soltar o mouse (não a cada pixel arrastado).</summary>
    public event Action<FanCurve>? CurveChanged;

    public CurveEditor()
    {
        ClipToBounds = true;
        MinHeight = 200;
        AppTheme.Changed += InvalidateVisual;
    }

    public void SetCurve(FanCurve curve)
    {
        if (_drag >= 0) return;
        _curve = curve.Clone();
        InvalidateVisual();
    }

    public void SetLive(int? temp, int? speed)
    {
        _liveTemp = temp;
        _liveSpeed = speed;
        InvalidateVisual();
    }

    private int Snap(double v) => SnapToGrid ? (int)(Math.Round(v / 5) * 5) : (int)Math.Round(v);

    // ---------- geometria ----------

    private void UpdatePlot() =>
        _plot = new Rect(PadL, PadT, Math.Max(10, Bounds.Width - PadL - PadR), Math.Max(10, Bounds.Height - PadT - PadB));

    private double X(double temp) =>
        _plot.Left + (temp - FanCurve.MinTemp) / (FanCurve.MaxTemp - FanCurve.MinTemp) * _plot.Width;

    private double Y(double speed) => _plot.Bottom - speed / FanCurve.MaxSpeed * _plot.Height;

    private double TempAt(double x) =>
        FanCurve.MinTemp + (x - _plot.Left) / _plot.Width * (FanCurve.MaxTemp - FanCurve.MinTemp);

    private double SpeedAt(double y) => (_plot.Bottom - y) / _plot.Height * FanCurve.MaxSpeed;

    private int HitPoint(Point p)
    {
        int best = -1;
        double bestDist = HitRadius;
        for (int i = 0; i < _curve.Points.Count; i++)
        {
            var pt = _curve.Points[i];
            double d = Math.Sqrt(Math.Pow(X(pt.Temp) - p.X, 2) + Math.Pow(Y(pt.Speed) - p.Y, 2));
            if (d <= bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ---------- mouse ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_readOnly) return;
        UpdatePlot();
        var point = e.GetCurrentPoint(this);
        var pos = point.Position;

        if (point.Properties.IsRightButtonPressed)
        {
            int r = HitPoint(pos);
            if (r >= 0 && _curve.Points.Count > FanCurve.MinPoints)
            {
                _curve.Points.RemoveAt(r);
                _hover = -1;
                InvalidateVisual();
                CurveChanged?.Invoke(_curve.Clone());
            }
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        int i = HitPoint(pos);
        if (i >= 0)
        {
            _drag = i;
            e.Pointer.Capture(this);
        }
        else if (e.ClickCount == 2 && _plot.Contains(pos) && _curve.Points.Count < FanCurve.MaxPoints)
        {
            int t = Math.Clamp(Snap(TempAt(pos.X)), FanCurve.MinTemp, FanCurve.MaxTemp);
            int s = Math.Clamp(Snap(SpeedAt(pos.Y)), 0, FanCurve.MaxSpeed);
            if (_curve.Points.Any(p => Math.Abs(p.Temp - t) <= 1)) return;
            _curve.Points.Add(new CurvePoint(t, s));
            _curve.Points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
            InvalidateVisual();
            CurveChanged?.Invoke(_curve.Clone());
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_readOnly) return;
        UpdatePlot();
        var pos = e.GetPosition(this);

        if (_drag >= 0)
        {
            var pts = _curve.Points;
            int lo = _drag > 0 ? pts[_drag - 1].Temp + 1 : FanCurve.MinTemp;
            int hi = _drag < pts.Count - 1 ? pts[_drag + 1].Temp - 1 : FanCurve.MaxTemp;
            pts[_drag].Temp = Math.Clamp(Snap(TempAt(pos.X)), lo, hi);
            pts[_drag].Speed = Math.Clamp(Snap(SpeedAt(pos.Y)), 0, FanCurve.MaxSpeed);
            InvalidateVisual();
            return;
        }

        int h = HitPoint(pos);
        if (h != _hover)
        {
            _hover = h;
            Cursor = h >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag < 0) return;
        _drag = -1;
        e.Pointer.Capture(null);
        InvalidateVisual();
        CurveChanged?.Invoke(_curve.Clone());
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_drag >= 0) return;
        _hover = -1;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    // ---------- desenho ----------

    public override void Render(DrawingContext dc)
    {
        UpdatePlot();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size)); // habilita hit-test

        var textBrush = AppTheme.TextSecondaryBrush;
        var accentColor = CurveColor ?? AppTheme.Accent;
        var accent = new SolidColorBrush(accentColor);
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)), 1);

        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)), null, _plot, 6, 6);

        for (int t = FanCurve.MinTemp; t <= FanCurve.MaxTemp; t += 10)
        {
            double x = X(t);
            dc.DrawLine(gridPen, new Point(x, _plot.Top), new Point(x, _plot.Bottom));
            DrawText(dc, t.ToString(), new Point(x, _plot.Bottom + 6), textBrush, 11, center: true);
        }
        for (int s = 0; s <= FanCurve.MaxSpeed; s += 20)
        {
            double y = Y(s);
            dc.DrawLine(gridPen, new Point(_plot.Left, y), new Point(_plot.Right, y));
            DrawText(dc, s == 0 ? "OFF" : $"{s}%", new Point(_plot.Left - 8, y - 7), textBrush, 11, right: true);
        }
        DrawText(dc, "°C", new Point(_plot.Right, _plot.Bottom + 6), textBrush, 11, right: true);

        var pts = _curve.Points;
        if (pts.Count > 0)
        {
            var line = new List<Point> { new(X(FanCurve.MinTemp), Y(pts[0].Speed)) };
            line.AddRange(pts.Select(p => new Point(X(p.Temp), Y(p.Speed))));
            line.Add(new Point(X(FanCurve.MaxTemp), Y(pts[^1].Speed)));

            var fill = new StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new Point(line[0].X, _plot.Bottom), true);
                foreach (var p in line) ctx.LineTo(p);
                ctx.LineTo(new Point(line[^1].X, _plot.Bottom));
                ctx.EndFigure(true);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B)), null, fill);

            var curveLine = new StreamGeometry();
            using (var ctx = curveLine.Open())
            {
                ctx.BeginFigure(line[0], false);
                foreach (var p in line.Skip(1)) ctx.LineTo(p);
                ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(accent, 2.5, lineJoin: PenLineJoin.Round), curveLine);
        }

        // temperatura atual + velocidade aplicada: mira tracejada roxa
        if (_liveTemp is int lt)
        {
            var purple = new SolidColorBrush(Color.FromRgb(0xB3, 0x88, 0xEB));
            var dash = new Pen(purple, 1.3, DashStyle.Dash);
            int speed = _liveSpeed ?? _curve.Evaluate(lt);
            double x = X(Math.Clamp(lt, FanCurve.MinTemp, FanCurve.MaxTemp));
            double y = Y(Math.Clamp(speed, 0, FanCurve.MaxSpeed));
            dc.DrawLine(dash, new Point(x, _plot.Top), new Point(x, _plot.Bottom));
            dc.DrawLine(dash, new Point(_plot.Left, y), new Point(_plot.Right, y));
            dc.DrawEllipse(purple, null, new Point(x, y), 5, 5);
        }

        for (int i = 0; i < pts.Count; i++)
        {
            bool active = i == _drag || i == _hover;
            var c = new Point(X(pts[i].Temp), Y(pts[i].Speed));
            double r = active ? 8 : 6.5;
            dc.DrawEllipse(accent, new Pen(Brushes.White, active ? 2 : 1.2), c, r, r);
        }

        int focus = _drag >= 0 ? _drag : _hover;
        if (focus >= 0 && focus < pts.Count)
        {
            var pt = pts[focus];
            var ft = MakeText($"{pt.Temp} °C  →  {pt.Speed}", Brushes.White, 12, bold: true);
            double bx = Math.Clamp(X(pt.Temp) - ft.Width / 2 - 6, 0, Math.Max(0, Bounds.Width - ft.Width - 12));
            double by = Y(pt.Speed) - 38;
            if (by < 0) by = Y(pt.Speed) + 14;
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 32, 32, 32)), null,
                new Rect(bx, by, ft.Width + 12, ft.Height + 6), 4, 4);
            dc.DrawText(ft, new Point(bx + 6, by + 3));
        }
    }

    internal static FormattedText MakeText(string text, IBrush brush, double size, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(AppTheme.UiFont, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            size, brush);

    internal static void DrawText(DrawingContext dc, string text, Point origin, IBrush brush, double size,
        bool center = false, bool right = false)
    {
        var ft = MakeText(text, brush, size);
        double x = center ? origin.X - ft.Width / 2 : right ? origin.X - ft.Width : origin.X;
        dc.DrawText(ft, new Point(x, origin.Y));
    }
}
