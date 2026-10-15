namespace AlienGHelper.Core;

public enum FailSafeAction { None, TripToGMode, RestorePrevious }

/// <summary>
/// Lógica do fail-safe, portada de AppGUI.updateAppState().
/// Sem dependência de UI: recebe temperaturas e diz o que fazer.
/// </summary>
public sealed class FailSafe
{
    public const double TriggerDelaySec = 8;
    public const double ResetAfterOkSec = 60;

    public bool Enabled { get; set; } = true;
    public int GpuLimit { get; set; } = 85;
    public int CpuLimit { get; set; } = 95;

    /// <summary>Modo ativo antes do disparo (null = fail-safe não está acionado).</summary>
    public ThermalMode? TrippedFrom { get; set; }
    public bool IsTripped => TrippedFrom.HasValue;
    public DateTimeOffset? LastHighTemp { get; private set; }

    private double _lastHighTs;          // segundos (relógio monotônico)
    private double? _highStartTs;

    private static double Now => Environment.TickCount64 / 1000.0;

    public void Reset()
    {
        _lastHighTs = 0;
        _highStartTs = null;
        TrippedFrom = null;
        LastHighTemp = null;
    }

    public FailSafeAction Evaluate(int? gpuTemp, int? cpuTemp, ThermalMode current)
    {
        double now = Now;
        bool high = gpuTemp is null || gpuTemp >= GpuLimit || cpuTemp is null || cpuTemp >= CpuLimit;

        if (high)
        {
            _lastHighTs = now;
            LastHighTemp = DateTimeOffset.Now;
        }
        _highStartTs = high ? (_highStartTs ?? now) : null;

        if (Enabled && current != ThermalMode.GMode && high && now - _highStartTs!.Value > TriggerDelaySec)
        {
            TrippedFrom = current;
            return FailSafeAction.TripToGMode;
        }

        if (TrippedFrom.HasValue && now - _lastHighTs > ResetAfterOkSec)
            return FailSafeAction.RestorePrevious;

        return FailSafeAction.None;
    }
}
