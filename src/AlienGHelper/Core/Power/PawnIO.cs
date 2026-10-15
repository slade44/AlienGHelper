using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TccG15.Core.Power;

/// <summary>
/// Acesso ao driver PawnIO (https://github.com/namazso/PawnIO) direto por DeviceIoControl,
/// sem a PawnIOLib.dll. O driver precisa estar instalado (instalador oficial ou
/// <c>winget install namazso.PawnIO</c>). Adaptado do G-Helper (GPL-3.0).
/// </summary>
public sealed class PawnIO : IDisposable
{
    private const int FnLen = 32;
    private const uint DevType = 41394u << 16;            // 0xA1B20000
    private const uint IoctlLoad = DevType | (0x821 << 2);    // 0xA1B22084
    private const uint IoctlExecute = DevType | (0x841 << 2); // 0xA1B22104

    public enum ConnectResult { OK, NotInstalled, AccessDenied, OtherError }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle dev, uint code,
        [In] byte[]? inBuf, uint inSize, [Out] byte[]? outBuf, uint outSize, out uint returned, IntPtr overlapped);

    private SafeFileHandle? _h;
    private bool _loaded;

    public bool IsReady => _loaded && _h is { IsInvalid: false, IsClosed: false };

    public ConnectResult Connect()
    {
        if (_h is { IsInvalid: false }) return ConnectResult.OK;
        var h = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000u, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            return err switch
            {
                2 or 3 => ConnectResult.NotInstalled,
                5 => ConnectResult.AccessDenied,
                _ => ConnectResult.OtherError,
            };
        }
        _h = h;
        return ConnectResult.OK;
    }

    public bool LoadModule(byte[] blob)
    {
        if (_h is null || _h.IsInvalid || blob.Length == 0) return false;
        _loaded = DeviceIoControl(_h, IoctlLoad, blob, (uint)blob.Length, null, 0, out _, IntPtr.Zero);
        return _loaded;
    }

    public bool Execute(string function, ulong[]? input, ulong[]? output)
    {
        if (!IsReady) return false;

        int inCount = input?.Length ?? 0;
        var inBuf = new byte[FnLen + inCount * 8];
        var name = Encoding.ASCII.GetBytes(function);
        Buffer.BlockCopy(name, 0, inBuf, 0, Math.Min(name.Length, FnLen - 1));
        if (inCount > 0) Buffer.BlockCopy(input!, 0, inBuf, FnLen, inCount * 8);

        int outCount = output?.Length ?? 0;
        var outBuf = outCount > 0 ? new byte[outCount * 8] : null;

        bool ok = DeviceIoControl(_h!, IoctlExecute, inBuf, (uint)inBuf.Length,
            outBuf, (uint)(outBuf?.Length ?? 0), out uint got, IntPtr.Zero);
        if (ok && output is not null && outBuf is not null && got > 0)
            Buffer.BlockCopy(outBuf, 0, output, 0, (int)Math.Min(got, (uint)outBuf.Length));
        return ok;
    }

    public void Dispose()
    {
        _loaded = false;
        _h?.Dispose();
        _h = null;
    }
}
