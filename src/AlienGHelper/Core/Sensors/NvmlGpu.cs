using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AlienGHelper.Core.Sensors;

/// <summary>
/// GPU NVIDIA via NVML (nvml.dll, instalada junto com o driver). Sem drivers ou pacotes extras.
/// Se não houver GPU NVIDIA ou a DLL não existir, <see cref="Available"/> fica falso.
/// </summary>
internal sealed class NvmlGpu : IDisposable
{
    private const int Success = 0;
    private const int ClockGraphics = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory { public ulong Total, Free, Used; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu, Memory; }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization util);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)]
    private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
    private IntPtr _device;
    public bool Available { get; }
    public string? Name { get; }

    public NvmlGpu()
    {
        try
        {
            // Drivers antigos deixam a DLL em NVSMI em vez de System32: carrega de lá se for o caso.
            if (!NativeLibrary.TryLoad("nvml.dll", out _))
            {
                string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation", "NVSMI", "nvml.dll");
                if (!File.Exists(alt) || !NativeLibrary.TryLoad(alt, out _)) return;
            }

            if (nvmlInit_v2() != Success) return;
            if (nvmlDeviceGetHandleByIndex_v2(0, out _device) != Success) return;

            var sb = new StringBuilder(96);
            if (nvmlDeviceGetName(_device, sb, (uint)sb.Capacity) == Success) Name = sb.ToString();
            Available = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Available = false;
        }
    }

    public double? PowerW => Read(() => nvmlDeviceGetPowerUsage(_device, out uint mw) == Success ? mw / 1000.0 : (double?)null);

    public double? PowerLimitW => Read(() => nvmlDeviceGetEnforcedPowerLimit(_device, out uint mw) == Success ? mw / 1000.0 : (double?)null);

    public double? UsagePercent => Read(() => nvmlDeviceGetUtilizationRates(_device, out var u) == Success ? u.Gpu : (double?)null);

    public double? ClockMHz => Read(() => nvmlDeviceGetClockInfo(_device, ClockGraphics, out uint mhz) == Success ? mhz : (double?)null);

    public (double UsedGB, double TotalGB)? Vram => !Available ? null : SafeVram();

    private (double, double)? SafeVram()
    {
        try
        {
            return nvmlDeviceGetMemoryInfo(_device, out var m) == Success
                ? (m.Used / 1073741824.0, m.Total / 1073741824.0)
                : null;
        }
        catch { return null; }
    }

    private double? Read(Func<double?> f)
    {
        if (!Available) return null;
        try { return f(); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (!Available) return;
        try { nvmlShutdown(); } catch { }
    }
}
