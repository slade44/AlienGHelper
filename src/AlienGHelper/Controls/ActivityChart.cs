using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TccG15.Controls;

/// <summary>
/// Gráfico de atividade com duas linhas (CPU e GPU). A métrica é escolhida pelo usuário:
/// temperatura, uso, clock, potência ou rotação dos fans. Janela de 1, 5 ou 15 min.
/// Passe o mouse para ver os valores de cada instante.
/// </summary>
public sealed class ActivityChart : Control
{
    public const string Temperature = "Temperature";
    public const string Usage = "Usage";
    public const string Clock = "Clock";
    public const string Power = "Power";
    public const string Fans = "Fans";

    /// <summary>Métricas disponíveis: chave, nome na lista e unidade.</summary>
    public static readonly (string Key, string Label, string Unit)[] Metrics =
    {
        (Temperature, "Temperature", "°C"),
        (Usage, "Usage", "%"),
        (Clock, "Clock", "MHz"),
        (Power, "Power", "W"),
        (Fans, "Fan speed", "RPM"),
    };

    public static string UnitOf(string metric) => Metrics.FirstOrDefault(m => m.Key == metric).Unit ?? "";

    private const double PadL = 38, PadR = 12, PadT = 20, PadB = 24;
    private const int KeepSeconds = 15 * 60 + 5;
    private const double GapSeconds = 6;

    public static readonly Color CpuColor = Color.FromRgb(0xFF, 0xA6, 0x57);
    public static readonly Color GpuColor = Color.FromRgb(0x6C, 0xB6, 0xFF);

    private readonly record struct Sample(DateTime Time, double? Cpu, double? Gpu);

    private readonly Dictionary<string, List<Sample>> _data = Metrics.ToDictionary(m => m.Key, _ => new List<Sample>());
    private string _metric = Temperature;
    private int _rangeSec = 300;
    private int? _cpuLimit, _gpuLimit;
    private double? _hoverX;

    public ActivityChart()
    {
        ClipToBounds = true;
        MinHeight = 120;
        AppTheme.Changed += InvalidateVisual;
    }

    public string Metric
    {
        get => _metric;
        set { _metric = _data.ContainsKey(value) ? value : Temperature; InvalidateVisual(); }
    }

    /// <summary>Registra uma amostra de CPU e GPU para a métrica (só redesenha se ela estiver à mostra).</summary>
    public void AddSample(string metric, double? cpu, double? gpu)
    {
        if (_data.TryGetValue(metric, out var list)) Add(list, cpu, gpu, _metric == metric);
    }

    public void AddTemperature(int? cpu, int? gpu) => AddSample(Temperature, cpu, gpu);

    public void AddUsage(double? cpu, double? gpu) => AddSample(Usage, cpu, gpu);

    public void SetRange(int seconds)
    {
        _rangeSec = Math.Max(10, seconds);
        InvalidateVisual();
    }

    /// <summary>Limites do fail-safe (linhas tracejadas no modo temperatura); null = não desenhar.</summary>
    public void SetLimits(int? cpuLimit, int? gpuLimit)
    {
        _cpuLimit = cpuLimit;
        _gpuLimit = gpuLimit;
    }

    private void Add(List<Sample> list, double? cpu, double? gpu, bool visible)
    {
        var now = DateTime.UtcNow;
        if (list.Count > 0 && (now - list[^1].Time).TotalMilliseconds < 400) return;
        list.Add(new Sample(now, cpu, gpu));
        var cutoff = now.AddSeconds(-KeepSeconds);
        int old = list.FindIndex(s => s.Time >= cutoff);
        if (old > 0) list.RemoveRange(0, old);
        if (visible) InvalidateVisual();
    }

    private static double NiceStep(double raw)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * mag;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverX = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        var plot = new Rect(PadL, PadT, Math.Max(10, w - PadL - PadR), Math.Max(10, h - PadT - PadB));
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var textBrush = AppTheme.TextSecondaryBrush;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)), 1);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(22, 128, 128, 128)), null, plot, 6, 6);

        bool usage = _metric == Usage;
        bool temp = _metric == Temperature;
        string unit = UnitOf(_metric);
        var data = _data[_metric];
        var now = DateTime.UtcNow;
        var start = now.AddSeconds(-_rangeSec);
        var visible = data.Where(s => s.Time >= start.AddSeconds(-GapSeconds)).ToList();

        double lo = 0, hi = 100, step = 25;
        if (!usage && !temp)
        {
            // clock, potência, RPM: de 0 até um pouco acima do maior valor, com 4 divisões "redondas"
            var all = visible.SelectMany(s => new[] { s.Cpu, s.Gpu }).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            double max = all.Count > 0 ? all.Max() : _metric switch { Clock => 4000, Power => 60, _ => 5000 };
            step = NiceStep(Math.Max(1, max * 1.1) / 4);
            hi = Math.Max(step, Math.Ceiling(max * 1.1 / step) * step);
        }
        else if (temp)
        {
            lo = 30; hi = 90; step = 10;
            var vals = visible.SelectMany(s => new[] { s.Cpu, s.Gpu }).Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (vals.Count > 0)
            {
                lo = Math.Floor((vals.Min() - 5) / 10.0) * 10;
                hi = Math.Ceiling((vals.Max() + 5) / 10.0) * 10;
            }
            lo = Math.Max(0, lo);
            hi = Math.Min(120, Math.Max(hi, lo + 30));
        }

        double X(DateTime t) => plot.Left + (t - start).TotalSeconds / _rangeSec * plot.Width;
        double Y(double v) => plot.Bottom - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * plot.Height;

        for (double v = lo; v <= hi + 0.01; v += step)
        {
            double y = Y(v);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            CurveEditor.DrawText(dc, $"{v:0}", new Point(plot.Left - 8, y - 7), textBrush, 11, right: true);
        }
        CurveEditor.DrawText(dc, unit, new Point(plot.Left - 8, plot.Top - 14), textBrush, 10, right: true);

        int tickSec = _rangeSec <= 60 ? 15 : _rangeSec <= 300 ? 60 : 180;
        for (int s = 0; s <= _rangeSec; s += tickSec)
        {
            double x = X(now.AddSeconds(-s));
            dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            string label = s == 0 ? "now" : s < 60 ? $"-{s}s" : $"-{s / 60}m";
            CurveEditor.DrawText(dc, label, new Point(Math.Min(x, plot.Right - 8), plot.Bottom + 6), textBrush, 11, center: true);
        }

        if (temp)
        {
            void Limit(int? l, Color c)
            {
                if (l is not int v || v < lo || v > hi) return;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(120, c.R, c.G, c.B)), 1.2, DashStyle.Dash);
                dc.DrawLine(pen, new Point(plot.Left, Y(v)), new Point(plot.Right, Y(v)));
            }
            Limit(_cpuLimit, CpuColor);
            Limit(_gpuLimit, GpuColor);
        }

        void Series(Func<Sample, double?> sel, Color color)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool open = false;
                DateTime prev = default;
                foreach (var s in visible)
                {
                    var v = sel(s);
                    if (v is null || (open && (s.Time - prev).TotalSeconds > GapSeconds))
                    {
                        if (open) ctx.EndFigure(false);
                        open = false;
                    }
                    if (v is null) continue;
                    var pt = new Point(X(s.Time), Y(v.Value));
                    if (!open) { ctx.BeginFigure(pt, false); open = true; }
                    else ctx.LineTo(pt);
                    prev = s.Time;
                }
                if (open) ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geo);
        }

        using (dc.PushClip(plot))
        {
            Series(s => s.Cpu, CpuColor);
            Series(s => s.Gpu, GpuColor);
        }

        if (visible.Count == 0)
        {
            string msg = _metric is Temperature or Fans ? "Waiting for data…" : "Recorded while this page is open…";
            CurveEditor.DrawText(dc, msg, new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2 - 8), textBrush, 12, center: true);
        }

        if (_hoverX is double hx && hx >= plot.Left && hx <= plot.Right && visible.Count > 0)
        {
            var t = start.AddSeconds((hx - plot.Left) / plot.Width * _rangeSec);
            var near = visible.MinBy(s => Math.Abs((s.Time - t).TotalSeconds));
            if (Math.Abs((near.Time - t).TotalSeconds) < 3)
            {
                double x = X(near.Time);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(110, 200, 200, 200)), 1),
                    new Point(x, plot.Top), new Point(x, plot.Bottom));
                if (near.Cpu is double c) dc.DrawEllipse(new SolidColorBrush(CpuColor), null, new Point(x, Y(c)), 4, 4);
                if (near.Gpu is double g) dc.DrawEllipse(new SolidColorBrush(GpuColor), null, new Point(x, Y(g)), 4, 4);

                int ago = (int)Math.Round((now - near.Time).TotalSeconds);
                string Fmt(double? v) => v is double d ? $"{d:0} {unit}" : "--";
                string text = $"CPU {Fmt(near.Cpu)}  ·  GPU {Fmt(near.Gpu)}  ·  {(ago <= 0 ? "now" : $"-{ago}s")}";
                var ft = CurveEditor.MakeText(text, Brushes.White, 12, bold: true);
                double bx = Math.Clamp(x - ft.Width / 2 - 6, 0, Math.Max(0, w - ft.Width - 12));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 32, 32, 32)), null,
                    new Rect(bx, plot.Top + 4, ft.Width + 12, ft.Height + 6), 4, 4);
                dc.DrawText(ft, new Point(bx + 6, plot.Top + 7));
            }
        }
    }
}
