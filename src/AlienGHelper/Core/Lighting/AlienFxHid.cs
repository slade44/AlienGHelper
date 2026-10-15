// Detecção de dispositivos AlienFX. Lógica de versão por tamanho do relatório HID
// portada de AlienFX_SDK::Functions::AlienFXProbeDevice (AlienFX-SDK, MIT).
using System.Runtime.InteropServices;
using System.Text;

namespace TccG15.Core.Lighting;

public sealed class AlienFxDeviceInfo
{
    public const ushort AlienwareVid = 0x187C;

    public string Path { get; init; } = "";
    public ushort Vid { get; init; }
    public ushort Pid { get; init; }
    public int OutputLength { get; init; }
    /// <summary>Versão da API do SDK (2, 3, 4, 6) ou -1 se o tamanho de relatório for desconhecido.</summary>
    public int Api { get; init; }
    public string Description { get; init; } = "";

    /// <summary>Só o APIv4 (Dell G-series / Alienware m-series, relatórios de 34 bytes) está implementado.</summary>
    public bool IsSupported => Api == 4;

    public override string ToString()
    {
        string api = Api > 0 ? $"API v{Api}" : $"unknown API ({OutputLength}-byte reports)";
        string note = IsSupported ? "supported" : "not supported by this app";
        return $"{Vid:X4}:{Pid:X4}  {api}  {note}  {Description}".TrimEnd();
    }
}

public static class AlienFxHid
{
    /// <summary>Lista os dispositivos HID com VID da Alienware (0x187C) que podem ser abertos.</summary>
    public static List<AlienFxDeviceInfo> Enumerate()
    {
        var result = new List<AlienFxDeviceInfo>();
        var guid = NativeHid.HidGuid;
        IntPtr set = NativeHid.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
            NativeHid.DIGCF_PRESENT | NativeHid.DIGCF_DEVICEINTERFACE);
        if (set == NativeHid.InvalidHandle || set == IntPtr.Zero) return result;

        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new NativeHid.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<NativeHid.SP_DEVICE_INTERFACE_DATA>()
                };
                if (!NativeHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                if ((data.Flags & NativeHid.SPINT_ACTIVE) == 0) continue;

                string? path = GetDevicePath(set, ref data);
                // Só toca em dispositivos Alienware: o VID aparece no caminho ("...#vid_187c&pid_xxxx#...")
                if (path is null || path.IndexOf("vid_187c", StringComparison.OrdinalIgnoreCase) < 0) continue;

                var info = Probe(path);
                if (info is not null) result.Add(info);
            }
        }
        finally
        {
            NativeHid.SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    private static string? GetDevicePath(IntPtr set, ref NativeHid.SP_DEVICE_INTERFACE_DATA data)
    {
        NativeHid.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        if (required == 0) return null;

        IntPtr buf = Marshal.AllocHGlobal((int)required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize = 8 em 64 bits e 6 em 32 bits; o caminho (wchar) começa no offset 4
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!NativeHid.SetupDiGetDeviceInterfaceDetail(set, ref data, buf, required, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(IntPtr.Add(buf, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static AlienFxDeviceInfo? Probe(string path)
    {
        using var handle = NativeHid.OpenDevice(path);
        if (handle.IsInvalid) return null;

        var attr = new NativeHid.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<NativeHid.HIDD_ATTRIBUTES>() };
        if (!NativeHid.HidD_GetAttributes(handle, ref attr)) return null;
        if (attr.VendorID != AlienFxDeviceInfo.AlienwareVid) return null;
        if (!NativeHid.HidD_GetPreparsedData(handle, out IntPtr prep)) return null;

        var caps = new NativeHid.HIDP_CAPS { Reserved = new ushort[17] };
        try { NativeHid.HidP_GetCaps(prep, ref caps); }
        finally { NativeHid.HidD_FreePreparsedData(prep); }

        int api = caps.OutputReportByteLength switch { 9 => 2, 12 => 3, 34 => 4, 65 => 6, _ => -1 };

        var sb = new StringBuilder(256);
        string desc = "";
        if (NativeHid.HidD_GetManufacturerString(handle, sb, 512)) desc = sb.ToString().Trim();
        sb.Clear();
        if (NativeHid.HidD_GetProductString(handle, sb, 512)) desc = (desc + " " + sb.ToString().Trim()).Trim();

        return new AlienFxDeviceInfo
        {
            Path = path,
            Vid = attr.VendorID,
            Pid = attr.ProductID,
            OutputLength = caps.OutputReportByteLength,
            Api = api,
            Description = desc,
        };
    }
}
