using System.Management;

namespace TccG15.Core;

/// <summary>Port de DetectHardware.py (operação lenta: rodar em background).</summary>
public static class HardwareInfo
{
    public static string? GetCpuName()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var o in s.Get())
                return (o["Name"] as string)?.Trim();
        }
        catch { }
        return null;
    }

    /// <summary>Assume que a GPU com mais VRAM é a principal (mesma heurística do original).</summary>
    public static string? GetGpuName()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            string? best = null;
            long bestRam = -1;
            foreach (var o in s.Get())
            {
                long ram = o["AdapterRAM"] is uint u ? u : 0;
                if (ram > bestRam)
                {
                    bestRam = ram;
                    best = (o["Name"] as string)?.Trim();
                }
            }
            return best;
        }
        catch { }
        return null;
    }
}
