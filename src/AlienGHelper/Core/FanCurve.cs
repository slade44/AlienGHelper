namespace AlienGHelper.Core;

public sealed class CurvePoint
{
    public int Temp { get; set; }
    public int Speed { get; set; }
    public CurvePoint() { }
    public CurvePoint(int temp, int speed) { Temp = temp; Speed = speed; }
}

/// <summary>
/// Curva temperatura (°C) → velocidade do fan em % (0..100).
/// Abaixo do primeiro ponto e acima do último a curva é plana; entre pontos, interpolação linear.
/// </summary>
public sealed class FanCurve
{
    public const int MinTemp = 20;
    public const int MaxTemp = 100;
    public const int MaxSpeed = 100;
    public const int MinPoints = 2;
    public const int MaxPoints = 12;

    public List<CurvePoint> Points { get; set; } = new();

    public static FanCurve DefaultCpu() => From((40, 20), (55, 35), (70, 55), (80, 80), (90, 100));
    public static FanCurve DefaultGpu() => From((40, 20), (55, 35), (65, 55), (75, 80), (85, 100));

    private static FanCurve From(params (int T, int S)[] pts) =>
        new() { Points = pts.Select(p => new CurvePoint(p.T, p.S)).ToList() };

    [System.Text.Json.Serialization.JsonIgnore]
    public int MaxPointSpeed => Points.Count == 0 ? MaxSpeed : Points.Max(p => p.Speed);

    public FanCurve Clone() => new()
    {
        Points = Points.Select(p => new CurvePoint(p.Temp, p.Speed)).ToList()
    };

    /// <summary>Ordena, limita aos intervalos válidos e remove temperaturas duplicadas.</summary>
    public FanCurve Normalize(FanCurve fallback)
    {
        var pts = Points
            .Select(p => new CurvePoint(Math.Clamp(p.Temp, MinTemp, MaxTemp), Math.Clamp(p.Speed, 0, MaxSpeed)))
            .OrderBy(p => p.Temp)
            .GroupBy(p => p.Temp).Select(g => g.First())
            .Take(MaxPoints)
            .ToList();
        return pts.Count >= MinPoints ? new FanCurve { Points = pts } : fallback.Clone();
    }

    public int Evaluate(double temp)
    {
        var p = Points;
        if (p.Count == 0) return MaxSpeed;
        if (temp <= p[0].Temp) return p[0].Speed;
        if (temp >= p[^1].Temp) return p[^1].Speed;
        for (int i = 1; i < p.Count; i++)
        {
            if (temp > p[i].Temp) continue;
            var a = p[i - 1];
            var b = p[i];
            double k = (temp - a.Temp) / (b.Temp - a.Temp);
            return (int)Math.Round(a.Speed + k * (b.Speed - a.Speed));
        }
        return p[^1].Speed;
    }
}
