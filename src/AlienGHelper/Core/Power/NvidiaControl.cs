using System.Diagnostics;
using System.IO;
using NvAPIWrapper;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.General;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

namespace TccG15.Core.Power;

/// <summary>
/// Controle da GPU NVIDIA dedicada via NvAPIWrapper (NVAPI do driver):
/// power target (% do TGP padrão), offsets de clock do núcleo/memória (P0) e
/// limite de clock máximo (nvidia-smi -lgc, igual ao G-Helper).
/// Offsets + clock máximo travado funcionam como "undervolt" em notebooks, onde a tensão não é editável.
/// </summary>
public sealed class NvidiaControl
{
    public const int MinCoreOffset = -250, MaxCoreOffset = 250;
    public const int MinMemoryOffset = -500, MaxMemoryOffset = 500;
    public const int MinClockLimit = 400, MaxClockLimit = 3000;

    private readonly PhysicalGPU? _gpu;

    public bool IsValid => _gpu is not null;
    public string Name { get; } = "";
    public string? Error { get; }

    /// <summary>Faixa do power target em % (null = a NVAPI não expôs a política de energia).</summary>
    public (int Min, int Default, int Max)? PowerRange { get; }
    /// <summary>O BIOS do notebook trava o power target quando mínimo = máximo.</summary>
    public bool PowerTargetEditable => PowerRange is { } r && r.Max > r.Min;

    public NvidiaControl()
    {
        try
        {
            NVIDIA.Initialize();
            var all = PhysicalGPU.GetPhysicalGPUs();
            _gpu = all.FirstOrDefault(g => g.SystemType == SystemType.Laptop) ?? all.FirstOrDefault();
            if (_gpu is null) { Error = "No NVIDIA GPU found."; return; }
            Name = _gpu.FullName;
        }
        catch (Exception ex)
        {
            _gpu = null;
            Error = ex is DllNotFoundException or NVIDIAApiException or NVIDIANotSupportedException
                ? "NVAPI is not available (NVIDIA driver missing?)."
                : ex.Message;
            return;
        }

        try
        {
            var info = GPUApi.ClientPowerPoliciesGetInfo(_gpu.Handle).PowerPolicyInfoEntries;
            if (info.Length > 0)
            {
                var e = info[0];
                PowerRange = ((int)Math.Round(e.MinimumPowerInPCM / 1000.0),
                              (int)Math.Round(e.DefaultPowerInPCM / 1000.0),
                              (int)Math.Round(e.MaximumPowerInPCM / 1000.0));
            }
        }
        catch { PowerRange = null; }
    }

    // ---------- power target ----------

    public int? GetPowerTarget()
    {
        if (_gpu is null) return null;
        try
        {
            var st = GPUApi.ClientPowerPoliciesGetStatus(_gpu.Handle).PowerPolicyStatusEntries;
            return st.Length > 0 ? (int)Math.Round(st[0].PowerTargetInPCM / 1000.0) : null;
        }
        catch { return null; }
    }

    public string? SetPowerTarget(int percent)
    {
        if (_gpu is null) return Error;
        if (PowerRange is not { } r) return "Power target is not exposed by this GPU.";
        if (!PowerTargetEditable) return "Power target is locked by the laptop firmware.";
        percent = Math.Clamp(percent, r.Min, r.Max);
        try
        {
            var entry = new PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry((uint)(percent * 1000));
            GPUApi.ClientPowerPoliciesSetStatus(_gpu.Handle, new PrivatePowerPoliciesStatusV1(new[] { entry }));
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    // ---------- clocks ----------

    public (int Core, int Memory)? GetClockOffsets()
    {
        if (_gpu is null) return null;
        try
        {
            var states = GPUApi.GetPerformanceStates20(_gpu.Handle);
            if (!states.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var clocks)) return null;
            int core = 0, mem = 0;
            foreach (var c in clocks)
            {
                if (c.DomainId == PublicClockDomain.Graphics) core = c.FrequencyDeltaInkHz.DeltaValue / 1000;
                else if (c.DomainId == PublicClockDomain.Memory) mem = c.FrequencyDeltaInkHz.DeltaValue / 1000;
            }
            return (core, mem);
        }
        catch { return null; }
    }

    public string? SetClockOffsets(int core, int memory)
    {
        if (_gpu is null) return Error;
        core = Math.Clamp(core, MinCoreOffset, MaxCoreOffset);
        memory = Math.Clamp(memory, MinMemoryOffset, MaxMemoryOffset);
        if (GetClockOffsets() is { } cur && Math.Abs(cur.Core - core) < 5 && Math.Abs(cur.Memory - memory) < 5)
            return null;   // já está assim

        try
        {
            var clocks = new[]
            {
                new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(core * 1000)),
                new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memory * 1000)),
            };
            var states = new[]
            {
                new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks,
                    Array.Empty<PerformanceStates20BaseVoltageEntryV1>()),
            };
            GPUApi.SetPerformanceStates20(_gpu.Handle, new PerformanceStates20InfoV1(states, 2, 0));
            return null;
        }
        catch (Exception ex) { return Describe(ex); }
    }

    /// <summary>Trava o clock máximo do núcleo (0 = sem limite). Usa o nvidia-smi que vem com o driver.</summary>
    public string? SetMaxClock(int mhz)
    {
        if (_gpu is null) return Error;
        string args = mhz >= MinClockLimit && mhz < MaxClockLimit ? $"-lgc 0,{mhz}" : "-rgc";
        return RunNvidiaSmi(args);
    }

    private static string? RunNvidiaSmi(string args)
    {
        string exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(exe))
        {
            string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
            exe = File.Exists(alt) ? alt : "nvidia-smi.exe";
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return "Could not start nvidia-smi.";
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return "nvidia-smi timed out."; }
            return p.ExitCode == 0 ? null : $"nvidia-smi: {output.Trim()}";
        }
        catch (Exception ex) { return $"nvidia-smi: {ex.Message}"; }
    }

    private static string Describe(Exception ex) => ex is NVIDIAApiException n
        ? n.Message.Contains("NOT_POWERED", StringComparison.OrdinalIgnoreCase)
            ? "The GPU is asleep (Optimus). Open a game or app that uses it and try again."
            : n.Status switch
        {
            Status.NotSupported => "Not supported by this GPU / driver.",
            Status.InvalidUserPrivilege => "Needs administrator rights.",
            _ => $"NVAPI error: {n.Status}",
        }
        : ex.Message;
}
