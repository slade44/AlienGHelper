namespace TccG15.Core;

/// <summary>Port de AWCCThermal.py: índice 0 = fan da CPU, 1 = fan da GPU.</summary>
public sealed class ThermalController
{
    public const int CpuFanIdx = 0;
    public const int GpuFanIdx = 1;

    private readonly AwccWmi _awcc;
    private readonly List<(uint FanId, uint[] SensorIds)> _fans;

    public ThermalController(AwccWmi awcc)
    {
        _awcc = awcc;
        _fans = awcc.DiscoverFans();
    }

    public int FanCount => _fans.Count;

    public int? GetFanRelatedTemp(int fanIdx)
    {
        if (fanIdx >= _fans.Count || _fans[fanIdx].SensorIds.Length == 0) return null;
        return _awcc.GetSensorTemperature(_fans[fanIdx].SensorIds[0]);
    }

    public int? GetFanRpm(int fanIdx) =>
        fanIdx < _fans.Count ? _awcc.GetFanRpm(_fans[fanIdx].FanId) : null;

    public bool SetFanSpeed(int fanIdx, int speed) =>
        fanIdx < _fans.Count && _awcc.SetFanSpeedPercent(_fans[fanIdx].FanId, speed);

    public bool SetMode(ThermalMode mode) => _awcc.ApplyThermalMode(mode);
}
