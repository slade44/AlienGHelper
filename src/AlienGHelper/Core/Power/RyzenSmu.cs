using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TccG15.Core.Power;

public enum SmuStatus : uint
{
    OK = 0x01,
    Failed = 0xFF,
    UnknownCmd = 0xFE,
    CmdRejectedPrereq = 0xFD,
    CmdRejectedBusy = 0xFC,
}

/// <summary>Famílias de CPU AMD, agrupadas pelo jeito de falar com a SMU.</summary>
public enum CpuFamily { Unknown, Raven, Renoir, Mobile, Raphael, StrixPoint, StrixHalo }

/// <summary>Limites atuais lidos da PM table, em watts.</summary>
public sealed record PowerLimits(float Stapm, float Fast, float Slow);

/// <summary>
/// Limites de potência de CPUs Ryzen (STAPM/SPL, Fast/fPPT, Slow/sPPT) pela mailbox da SMU,
/// usando o módulo RyzenSMU do PawnIO. Comandos e endereços iguais aos do RyzenAdj/G-Helper.
/// Ryzen 7 6800H = Rembrandt → família Mobile.
/// Adaptado do G-Helper (app/Pawn/RyzenSmu.cs, GPL-3.0).
/// </summary>
public sealed class RyzenSmu : IDisposable
{
    private const int MailboxTimeoutMs = 200;

    private readonly PawnIO _io = new();
    private readonly object _mailbox = new();
    private bool _disposed;

    public uint CodeName { get; private set; } = 0xFFFFFFFF;
    public uint SmuVersion { get; private set; }
    public CpuFamily Family { get; private set; }
    public bool IsReady { get; private set; }

    public string CodeNameText => CodeName switch
    {
        6 => "Raven Ridge", 7 => "Raven Ridge 2", 2 => "Picasso", 15 => "Dali",
        1 => "Renoir", 22 => "Lucienne", 13 => "Cezanne",
        12 => "Van Gogh", 10 => "Rembrandt", 25 => "Mendocino",
        23 => "Phoenix", 24 => "Phoenix 2", 30 => "Hawk Point", 19 => "Fire Flight",
        16 => "Raphael", 17 => "Granite Ridge", 28 => "Dragon Range",
        31 => "Strix Point", 32 => "Strix Halo", 33 => "Krackan Point", 34 => "Krackan Point 2",
        _ => $"code {CodeName}",
    };

    public string SmuVersionText => $"{SmuVersion >> 16}.{(SmuVersion >> 8) & 0xFF}.{SmuVersion & 0xFF}";

    /// <summary>Conecta ao driver e carrega o módulo embutido. Retorna o resultado da conexão.</summary>
    internal PawnIO.ConnectResult Initialize(out string? error)
    {
        error = null;
        var r = _io.Connect();
        if (r != PawnIO.ConnectResult.OK) return r;

        var blob = LoadModuleBlob();
        if (blob is null) { error = "RyzenSMU.bin is missing from the build."; return PawnIO.ConnectResult.OtherError; }
        if (!_io.LoadModule(blob))
        {
            error = $"PawnIO refused the RyzenSMU module (Win32 error {Marshal.GetLastWin32Error()}).";
            return PawnIO.ConnectResult.OtherError;
        }

        var o = new ulong[1];
        if (_io.Execute("ioctl_get_code_name", null, o)) CodeName = (uint)o[0];
        if (_io.Execute("ioctl_get_smu_version", null, o)) SmuVersion = (uint)o[0];
        Family = GetFamily(CodeName);
        IsReady = Family != CpuFamily.Unknown;
        if (!IsReady) error = $"CPU not supported by the SMU module ({CodeNameText}).";
        return PawnIO.ConnectResult.OK;
    }

    private static byte[]? LoadModuleBlob()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream("TccG15.RyzenSMU.bin");
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static CpuFamily GetFamily(uint code) => code switch
    {
        6 or 7 or 2 or 15 => CpuFamily.Raven,
        1 or 22 or 13 => CpuFamily.Renoir,
        12 or 10 or 25 or 23 or 24 or 30 or 19 => CpuFamily.Mobile,
        16 or 17 or 28 => CpuFamily.Raphael,
        31 or 33 or 34 => CpuFamily.StrixPoint,
        32 => CpuFamily.StrixHalo,
        _ => CpuFamily.Unknown,
    };

    // ---------- limites ----------

    /// <summary>SPL = STAPM (sustentado), sPPT = Slow (boost longo), fPPT = Fast (boost curto).</summary>
    public (SmuStatus Spl, SmuStatus Sppt, SmuStatus Fppt) SetLimits(int splW, int spptW, int fpptW) =>
        (SetStapm(splW), SetSlow(spptW), SetFast(fpptW));

    private SmuStatus SetStapm(int w)
    {
        uint mw = (uint)w * 1000;
        switch (Family)
        {
            case CpuFamily.Raven: return SendMp1(0x1A, mw);
            case CpuFamily.Renoir: { var s = SendMp1(0x14, mw); SendPsmu(0x31, mw); return s; }
            case CpuFamily.Mobile or CpuFamily.StrixPoint or CpuFamily.StrixHalo: return SendMp1(0x14, mw);
            case CpuFamily.Raphael: return SendMp1(0x4F, mw);
            default: return SmuStatus.Failed;
        }
    }

    private SmuStatus SetFast(int w)
    {
        uint mw = (uint)w * 1000;
        switch (Family)
        {
            case CpuFamily.Raven: return SendMp1(0x1B, mw);
            case CpuFamily.Renoir: { var s = SendMp1(0x15, mw); SendPsmu(0x32, mw); return s; }
            case CpuFamily.Mobile or CpuFamily.StrixPoint or CpuFamily.StrixHalo: return SendMp1(0x15, mw);
            case CpuFamily.Raphael: return SendMp1(0x3E, mw);
            default: return SmuStatus.Failed;
        }
    }

    private SmuStatus SetSlow(int w)
    {
        uint mw = (uint)w * 1000;
        switch (Family)
        {
            case CpuFamily.Raven: return SendMp1(0x1C, mw);
            case CpuFamily.Renoir: { var s = SendMp1(0x16, mw); SendPsmu(0x33, mw); SendPsmu(0x34, mw); return s; }
            case CpuFamily.Mobile or CpuFamily.StrixPoint or CpuFamily.StrixHalo: return SendMp1(0x16, mw);
            case CpuFamily.Raphael: return SendMp1(0x5F, mw);
            default: return SmuStatus.Failed;
        }
    }

    /// <summary>
    /// Lê os limites atuais da PM table (offsets 0x00, 0x08 e 0x10 são estáveis em todas as versões).
    /// A primeira atualização depois de acordar pode falhar com "busy": por isso o update duplo.
    /// </summary>
    public PowerLimits? ReadLimits()
    {
        if (!IsReady) return null;
        lock (_mailbox)
        {
            var resolve = new ulong[2];
            if (!_io.Execute("ioctl_resolve_pm_table", null, resolve)) return null;
            _io.Execute("ioctl_update_pm_table", null, null);
            Thread.Sleep(100);
            if (!_io.Execute("ioctl_update_pm_table", null, null)) return null;
            Thread.Sleep(150);

            var words = new ulong[64];
            if (!_io.Execute("ioctl_read_pm_table", null, words)) return null;
            var f = MemoryMarshal.Cast<ulong, float>(words);
            if (f[0] <= 0 || f[0] > 500) return null;
            return new PowerLimits(f[0], f[2], f[4]);
        }
    }

    // ---------- mailbox ----------

    private SmuStatus SendMp1(uint cmd, uint arg)
    {
        var (c, r, a) = Family switch
        {
            CpuFamily.Raven or CpuFamily.Renoir => (0x03B10528u, 0x03B10564u, 0x03B10998u),
            CpuFamily.Mobile => (0x03B10528u, 0x03B10578u, 0x03B10998u),
            CpuFamily.StrixPoint or CpuFamily.StrixHalo => (0x03B10928u, 0x03B10978u, 0x03B10998u),
            CpuFamily.Raphael => (0x03B10530u, 0x03B1057Cu, 0x03B109C4u),
            _ => (0u, 0u, 0u),
        };
        return c == 0 ? SmuStatus.Failed : Mailbox(c, r, a, cmd, arg);
    }

    private SmuStatus SendPsmu(uint cmd, uint arg)
    {
        var (c, r, a) = Family switch
        {
            CpuFamily.Raphael => (0x03B10524u, 0x03B10570u, 0x03B10A40u),
            CpuFamily.Raven or CpuFamily.Renoir or CpuFamily.Mobile
                or CpuFamily.StrixPoint or CpuFamily.StrixHalo => (0x03B10A20u, 0x03B10A80u, 0x03B10A88u),
            _ => (0u, 0u, 0u),
        };
        return c == 0 ? SmuStatus.Failed : Mailbox(c, r, a, cmd, arg);
    }

    private bool ReadReg(uint addr, out uint value)
    {
        value = 0;
        var o = new ulong[1];
        if (!_io.Execute("ioctl_read_smu_register", new ulong[] { addr }, o)) return false;
        value = (uint)o[0];
        return true;
    }

    private bool WriteReg(uint addr, uint value) =>
        _io.Execute("ioctl_write_smu_register", new ulong[] { addr, value }, null);

    private bool WaitNonZero(uint rsp, out uint status)
    {
        status = 0;
        var sw = Stopwatch.StartNew();
        int spins = 0;
        while (sw.ElapsedMilliseconds < MailboxTimeoutMs)
        {
            if (!ReadReg(rsp, out status)) return false;
            if (status != 0) return true;
            if (++spins > 256) Thread.Sleep(1);
            else if (spins > 32) Thread.Yield();
        }
        return false;
    }

    private SmuStatus Mailbox(uint cmdAddr, uint rspAddr, uint argAddr, uint cmd, uint arg)
    {
        if (_disposed || !IsReady) return SmuStatus.Failed;
        lock (_mailbox)
        {
            if (!WaitNonZero(rspAddr, out _)) return SmuStatus.CmdRejectedBusy;
            if (!WriteReg(rspAddr, 0)) return SmuStatus.Failed;
            for (uint i = 0; i < 6; i++)
                if (!WriteReg(argAddr + i * 4, i == 0 ? arg : 0)) return SmuStatus.Failed;
            if (!WriteReg(cmdAddr, cmd)) return SmuStatus.Failed;
            if (!WaitNonZero(rspAddr, out uint status)) return SmuStatus.Failed;
            return (SmuStatus)status;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsReady = false;
        lock (_mailbox) _io.Dispose();
    }
}
