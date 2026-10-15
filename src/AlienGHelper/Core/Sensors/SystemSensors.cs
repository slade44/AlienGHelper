using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace AlienGHelper.Core.Sensors;

/// <summary>Uma leitura dos sensores. Campos nulos = não disponível neste computador.</summary>
public sealed record SensorSnapshot(
    double? CpuPowerW,
    double? CpuClockMHz,
    double? CpuUsagePercent,
    double RamUsedGB,
    double RamTotalGB,
    string? GpuName,
    double? GpuPowerW,
    double? GpuPowerLimitW,
    double? GpuUsagePercent,
    double? GpuClockMHz,
    double? VramUsedGB,
    double? VramTotalGB);

/// <summary>
/// Sensores do sistema sem drivers de terceiros:
///  - CPU: contadores do Windows (% Processor Performance × clock base = clock atual, como no Gerenciador de Tarefas;
///    % Processor Utility = uso) e "Energy Meter" (RAPL do pacote) para a potência, quando o Windows o expõe;
///  - RAM: GlobalMemoryStatusEx;
///  - GPU dedicada NVIDIA: NVML.
/// Criar e ler em background (a primeira leitura dos contadores demora).
/// </summary>
public sealed class SystemSensors : IDisposable
{
    private readonly PerformanceCounter? _cpuPerf;
    private readonly PerformanceCounter? _cpuUtil;
    private readonly PerformanceCounter? _cpuPower;
    private readonly double _baseMHz;
    private readonly NvmlGpu _gpu;

    public SystemSensors()
    {
        _cpuPerf = TryCounter("Processor Information", "% Processor Performance", "_Total");
        _cpuUtil = TryCounter("Processor Information", "% Processor Utility", "_Total")
                   ?? TryCounter("Processor Information", "% Processor Time", "_Total");
        _cpuPower = TryPackagePowerCounter();
        _baseMHz = ReadBaseClockMHz();
        _gpu = new NvmlGpu();

        // a primeira leitura de um contador sempre volta 0: "aquece" aqui
        try { _cpuPerf?.NextValue(); _cpuUtil?.NextValue(); _cpuPower?.NextValue(); } catch { }
    }

    public SensorSnapshot Read()
    {
        double? perf = Next(_cpuPerf);
        double? clock = perf is double p && _baseMHz > 0 ? p / 100.0 * _baseMHz : null;
        double? util = Next(_cpuUtil) is double u ? Math.Clamp(u, 0, 100) : null;

        var (ramUsed, ramTotal) = ReadRam();
        var vram = _gpu.Vram;

        return new SensorSnapshot(
            CpuPowerW: ReadCpuPower(),
            CpuClockMHz: clock,
            CpuUsagePercent: util,
            RamUsedGB: ramUsed,
            RamTotalGB: ramTotal,
            GpuName: _gpu.Name,
            GpuPowerW: _gpu.PowerW,
            GpuPowerLimitW: _gpu.PowerLimitW,
            GpuUsagePercent: _gpu.UsagePercent,
            GpuClockMHz: _gpu.ClockMHz,
            VramUsedGB: vram?.UsedGB,
            VramTotalGB: vram?.TotalGB);
    }

    // ---------- CPU ----------

    /// <summary>
    /// "Energy Meter" → "Power" da instância do pacote (RAPL_Package0_PKG em Intel). O valor é tratado como mW;
    /// se vier absurdo (> 1000 W), assume µW. Nem todo notebook expõe esse contador (AMD costuma não ter).
    /// </summary>
    private double? ReadCpuPower()
    {
        if (Next(_cpuPower) is not double raw || raw <= 0) return null;
        double w = raw / 1000.0;
        if (w > 1000) w = raw / 1_000_000.0;
        return w is > 0 and < 500 ? w : null;
    }

    private static PerformanceCounter? TryPackagePowerCounter()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("Energy Meter")) return null;
            var cat = new PerformanceCounterCategory("Energy Meter");
            string? inst = cat.GetInstanceNames()
                .Where(n => n.Contains("PKG", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return inst is null ? null : TryCounter("Energy Meter", "Power", inst);
        }
        catch { return null; }
    }

    private static double ReadBaseClockMHz()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            foreach (var o in s.Get())
                if (o["MaxClockSpeed"] is uint mhz) return mhz;
        }
        catch { }
        return 0;
    }

    // ---------- RAM ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private static (double Used, double Total) ReadRam()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0);
        const double GB = 1073741824.0;
        return ((m.ullTotalPhys - m.ullAvailPhys) / GB, m.ullTotalPhys / GB);
    }

    // ---------- utilidades ----------

    private static PerformanceCounter? TryCounter(string category, string counter, string instance)
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(category)) return null;
            var cat = new PerformanceCounterCategory(category);
            if (!cat.CounterExists(counter)) return null;
            return new PerformanceCounter(category, counter, instance, readOnly: true);
        }
        catch { return null; }
    }

    private static double? Next(PerformanceCounter? c)
    {
        if (c is null) return null;
        try { return c.NextValue(); }
        catch { return null; }
    }

    public void Dispose()
    {
        _cpuPerf?.Dispose();
        _cpuUtil?.Dispose();
        _cpuPower?.Dispose();
        _gpu.Dispose();
    }
}
