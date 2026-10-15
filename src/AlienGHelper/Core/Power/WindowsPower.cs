using System.Runtime.InteropServices;

namespace TccG15.Core.Power;

/// <summary>
/// Configurações de energia do Windows: modo de energia (overlay de Configurações &gt; Sistema &gt; Energia)
/// e "Processor performance boost mode" do plano ativo. Mesmas chamadas do G-Helper.
/// </summary>
public static class WindowsPower
{
    public static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Balanced = Guid.Empty;
    public static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static readonly (Guid Id, string Name)[] Modes =
    {
        (Efficiency, "Best Power Efficiency"),
        (Balanced, "Balanced"),
        (Performance, "Best Performance"),
    };

    /// <summary>Valores de PERFBOOSTMODE na ordem do Windows.</summary>
    public static readonly string[] BoostModes =
        { "Disabled (base clock only)", "Enabled", "Aggressive", "Efficient Enabled", "Efficient Aggressive" };

    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    /// <summary>"Maximum processor state" (PROCTHROTTLEMAX), em %.</summary>
    private static readonly Guid MaxProcState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    /// <summary>"Processor performance boost policy" (PERFBOOSTPOL), 0–100 %.</summary>
    private static readonly Guid BoostPolicy = new("45bcc044-d885-43e2-8605-ee0ec6e96b59");
    /// <summary>"Maximum processor frequency" (PROCFREQMAX), MHz; 0 = sem limite.</summary>
    private static readonly Guid MaxFreq = new("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
    /// <summary>"Processor energy performance preference" (PERFEPP), 0 = desempenho … 100 = economia.</summary>
    private static readonly Guid Epp = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    /// <summary>Índice em <see cref="Modes"/>, ou -1 se não der para ler.</summary>
    public static int GetMode()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var g) != 0) return -1;
            return Array.FindIndex(Modes, m => m.Id == g);
        }
        catch { return -1; }
    }

    public static bool SetMode(int index)
    {
        if (index < 0 || index >= Modes.Length) return false;
        try { return PowerSetActiveOverlayScheme(Modes[index].Id) == 0; }
        catch { return false; }
    }

    private static Guid? ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    public static int GetCpuBoost() => ReadProcessor(PerfBoostMode);

    /// <summary>Grava na tomada e na bateria do plano ativo e reativa o plano para valer na hora.</summary>
    public static bool SetCpuBoost(int mode) =>
        mode >= 0 && mode < BoostModes.Length && WriteProcessor(PerfBoostMode, (uint)mode, dc: true);

    public static int GetMaxProcessorState() => ReadProcessor(MaxProcState);

    public static bool SetMaxProcessorState(int percent) => WriteProcessor(MaxProcState, (uint)Math.Clamp(percent, 5, 100), dc: true);

    /// <summary>Opções do plano ativo (na tomada) que limitam o clock da CPU. -1 = não existe/não lido.</summary>
    public sealed record PlanCpuInfo(int Boost, int BoostPolicy, int MaxState, int MaxFreqMHz, int Epp)
    {
        public bool Locked =>
            Boost == 0 || Boost >= 3 ||                       // desligado ou "Efficient …"
            BoostPolicy is >= 0 and < 100 ||
            MaxState is >= 0 and < 100 ||
            MaxFreqMHz > 0 ||
            Epp > 50;

        public override string ToString()
        {
            string boost = Boost >= 0 && Boost < BoostModes.Length ? BoostModes[Boost].Split(" (")[0] : "?";
            return $"Boost {boost} · boost policy {Pct(BoostPolicy)} · max state {Pct(MaxState)} · " +
                   $"max freq {(MaxFreqMHz > 0 ? MaxFreqMHz + " MHz" : MaxFreqMHz == 0 ? "unlimited" : "?")} · EPP {Pct(Epp)}";
        }

        private static string Pct(int v) => v >= 0 ? v + " %" : "?";
    }

    /// <summary>
    /// O modo "Balanced" do Windows não sobrepõe nada: valem as opções do plano de energia. Boost
    /// desligado/"Efficient", boost policy &lt; 100 %, estado máximo &lt; 100 %, frequência máxima
    /// definida ou EPP alto (preferência por economia) seguram a CPU perto do clock base.
    /// O "Best Performance" esconde isso porque sobrepõe essas opções.
    /// </summary>
    public static PlanCpuInfo ReadPlanCpu() => new(
        ReadProcessor(PerfBoostMode), ReadProcessor(BoostPolicy), ReadProcessor(MaxProcState),
        ReadProcessor(MaxFreq), ReadProcessor(Epp));

    public static bool IsClockLocked() => ReadPlanCpu().Locked;

    /// <summary>
    /// Libera o clock na tomada: boost Aggressive, boost policy 100 %, estado máximo 100 %,
    /// frequência sem limite e EPP 25 (o mesmo lado "desempenho" do Balanced padrão).
    /// Na bateria só o boost e o estado máximo mudam, para não gastar mais bateria.
    /// </summary>
    public static bool UnlockClock()
    {
        bool ok = WriteProcessor(PerfBoostMode, 2, dc: true);
        ok &= WriteProcessor(MaxProcState, 100, dc: true);
        WriteProcessor(BoostPolicy, 100, dc: false);
        WriteProcessor(MaxFreq, 0, dc: false);
        if (ReadProcessor(Epp) > 25) WriteProcessor(Epp, 25, dc: false);
        return ok;
    }

    private static int ReadProcessor(Guid setting)
    {
        try
        {
            if (ActiveScheme() is not Guid scheme) return -1;
            var sub = SubProcessor;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint v) == 0 ? (int)v : -1;
        }
        catch { return -1; }
    }

    private static bool WriteProcessor(Guid setting, uint value, bool dc)
    {
        try
        {
            if (ActiveScheme() is not Guid scheme) return false;
            var sub = SubProcessor;
            uint ac = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
            if (dc) PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
            PowerSetActiveScheme(IntPtr.Zero, ref scheme);
            return ac == 0;
        }
        catch { return false; }
    }
}
