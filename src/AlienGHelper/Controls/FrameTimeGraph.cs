using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AlienGHelper.Controls;

/// <summary>Gráfico de frametime (ms) dos últimos quadros: linha na cor de destaque, escala automática.</summary>
public sealed class FrameTimeGraph : Control
{
    private double[] _values = Array.Empty<double>();

    public FrameTimeGraph()
    {
        ClipToBounds = true;
        MinHeight = 56;
        AppTheme.Changed += InvalidateVisual;
    }

    public void SetValues(double[] frameTimesMs)
    {
        _values = frameTimesMs;
        InvalidateVisual();
    }

    /// <summary>
    /// Frametime por quadro (ms), do mais antigo (esquerda) ao mais recente (direita): linha baixa = jogo fluido,
    /// picos para cima = travadas. Escala de ms à esquerda, média tracejada e o último frametime à direita.
    /// </summary>
    public override void Render(DrawingContext dc)
    {
        const double padL = 44, padR = 8, padT = 18, padB = 6;
        double w = Bounds.Width, h = Bounds.Height;
        var plot = new Rect(padL, padT, Math.Max(10, w - padL - padR), Math.Max(10, h - padT - padB));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(22, 128, 128, 128)), null, new Rect(0, 0, w, h), 6, 6);

        var text = new SolidColorBrush(AppTheme.TextTertiary);
        var title = CurveEditor.MakeText("Frametime, ms", text, 10);
        dc.DrawText(title, new Point(padL, 3));
        if (_values.Length < 2 || w < 60 || h < 40) return;

        // escala em passos "redondos": 10, 20, 40, 50, 100 ms…
        double peak = _values.Max();
        double max = peak <= 10 ? 10 : peak <= 20 ? 20 : peak <= 40 ? 40 : peak <= 50 ? 50 : peak <= 100 ? 100 : Math.Ceiling(peak / 50) * 50;
        double Y(double v) => plot.Bottom - Math.Min(v, max) / max * plot.Height;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), 1);
        foreach (double mark in new[] { 0, max / 2, max })
        {
            dc.DrawLine(grid, new Point(plot.Left, Y(mark)), new Point(plot.Right, Y(mark)));
            var ft = CurveEditor.MakeText($"{mark:0.#}", text, 9.5);
            dc.DrawText(ft, new Point(padL - 6 - ft.Width, Y(mark) - ft.Height / 2));
        }
        // 60 FPS (16.7 ms) como referência, se couber
        if (16.7 < max)
        {
            var refPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), 1, new DashStyle(new double[] { 2, 3 }, 0));
            dc.DrawLine(refPen, new Point(plot.Left, Y(16.7)), new Point(plot.Right, Y(16.7)));
        }

        double avg = _values.Average();
        var avgPen = new Pen(new SolidColorBrush(Color.FromArgb(150, AppTheme.Accent.R, AppTheme.Accent.G, AppTheme.Accent.B)), 1,
            new DashStyle(new double[] { 4, 3 }, 0));
        dc.DrawLine(avgPen, new Point(plot.Left, Y(avg)), new Point(plot.Right, Y(avg)));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double step = plot.Width / (_values.Length - 1);
            ctx.BeginFigure(new Point(plot.Left, Y(_values[0])), false);
            for (int i = 1; i < _values.Length; i++) ctx.LineTo(new Point(plot.Left + i * step, Y(_values[i])));
            ctx.EndFigure(false);
        }
        using (dc.PushClip(plot.Inflate(2)))
            dc.DrawGeometry(null, new Pen(AppTheme.AccentBrush, 1.5, lineJoin: PenLineJoin.Round), geo);

        var info = CurveEditor.MakeText($"last {_values[^1]:0.0} ms · avg {avg:0.0} ms · max {peak:0.0} ms", text, 10);
        dc.DrawText(info, new Point(plot.Right - info.Width, 3));
    }
}
