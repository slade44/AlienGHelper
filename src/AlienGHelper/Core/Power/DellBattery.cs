using System.Management;

namespace AlienGHelper.Core.Power;

/// <summary>Modos de carga da bateria da BIOS Dell (mesmos valores do Dell Power Manager).</summary>
public enum ChargeMode : byte
{
    Standard = 1,
    Express = 2,
    PrimarilyAc = 3,
    Adaptive = 4,
    Custom = 5,
}

/// <summary>Estado de carga lido da BIOS. <see cref="Error"/> != null = não deu para ler.</summary>
public sealed record BatteryChargeInfo(
    ChargeMode? Mode, int Start, int Stop, uint Capability, string? Error)
{
    public bool Supported => Error is null;

    public bool Supports(ChargeMode m) => Capability == 0 || (Capability & Bit(m)) != 0;

    private static uint Bit(ChargeMode m) => m switch
    {
        ChargeMode.Standard => 0x01,
        ChargeMode.Express => 0x02,
        ChargeMode.PrimarilyAc => 0x04,
        ChargeMode.Adaptive => 0x08,
        ChargeMode.Custom => 0x10,
        _ => 0,
    };
}

/// <summary>
/// Limite de carga da bateria pela BIOS Dell, do mesmo jeito que o Dell Power Manager 3.9 (smblib.dll /
/// systeminterop.dll): chamada da "Dell calling interface" pela ACPI-WMI, classe <c>BFn</c> (método
/// <c>DoBFn</c>, buffer <c>BDat.Bytes</c>) em <c>root\wmi</c>, instância <c>ACPI\PNP0C14\0_0</c>.
/// O tamanho do buffer vem do descritor <c>WMI_Query.QDATA</c> ("DELL", " WMI", versão, tamanho).
///
/// Buffer (36 bytes da chamada + 8 de extensão, empacotado):
///   0 classe u16 · 2 seletor u16 · 4..19 entrada (4 × u32) · 20..35 saída (4 × u32) · 36 ArgAttrib · 40 BLength
/// Classe 8 (bateria):
///   seletor 18, comando 3 = ler propriedades: saída[0]=resultado, byte 24 = modo, 25 = início, 26 = fim,
///                                             saída[2] = modos suportados (bits)
///   seletor 19, comando 0 = gravar: byte 8 = modo, 9 = início (%), 10 = fim (%); saída[0] = resultado
/// Resultado 0 = sucesso. Antes da chamada vale −3 (se continuar −3, a BIOS não respondeu).
/// </summary>
public static class DellBattery
{
    public const int MinStart = 50, MaxStart = 95, MinStop = 55, MaxStop = 100, MinGap = 5;

    private const string Ns = @"root\wmi";
    private const string Instance = @"ACPI\PNP0C14\0_0";
    private const int HeaderSize = 44;            // DA_CALL_STANDARD_INTERFACE
    private const int AdvConfigSize = 36;         // DA_CALL_ADVANCED_CHARGE_CONFIGURATION
    private const uint NotRun = 0xFFFFFFFD;       // −3
    private const byte Battery = 1;               // bateria principal (BiosId 1)

    private static readonly object Gate = new();
    private static int? _bufferLength;

    /// <summary>Lê o modo e os limites atuais. Lento (WMI): chamar em background.</summary>
    public static BatteryChargeInfo Read()
    {
        try
        {
            var buf = NewBuffer(out int len);
            Put16(buf, 0, 8);
            Put16(buf, 2, 18);
            buf[4] = 3;                                // comando: ler propriedades
            buf[5] = Battery;
            Put32(buf, 8, HeaderSize);                 // onde começa o buffer estendido
            Put32(buf, 20, NotRun);
            Put32(buf, 36, 0x100);                     // ArgAttrib
            Put32(buf, 40, AdvConfigSize);             // BLength
            // buffer estendido como o do Dell: tamanho + "DSCI" repetido
            if (len >= HeaderSize + AdvConfigSize)
            {
                Put32(buf, HeaderSize, AdvConfigSize);
                var tag = "DSCI"u8;
                for (int i = 4; i + 4 <= AdvConfigSize; i += 4)
                    tag.CopyTo(buf.AsSpan(HeaderSize + i));
            }

            var r = Call(buf);
            uint res = Get32(r, 20);
            if (res != 0)
                return new BatteryChargeInfo(null, 0, 0, 0, res == NotRun
                    ? "The BIOS did not answer the battery request"
                    : $"The BIOS refused the battery request (code {(int)res})");

            byte mode = r[24];
            return new BatteryChargeInfo(
                Enum.IsDefined(typeof(ChargeMode), mode) ? (ChargeMode)mode : null,
                r[25], r[26], Get32(r, 28), null);
        }
        catch (Exception ex)
        {
            return new BatteryChargeInfo(null, 0, 0, 0, Describe(ex));
        }
    }

    /// <summary>Grava um modo. Para <see cref="ChargeMode.Custom"/>, passe o início e o fim (%).</summary>
    public static (bool Ok, string Message) Set(ChargeMode mode, int start = 0, int stop = 0)
    {
        try
        {
            if (mode == ChargeMode.Custom)
            {
                stop = Math.Clamp(stop, MinStop, MaxStop);
                start = Math.Clamp(start, MinStart, Math.Min(MaxStart, stop - MinGap));
            }

            var buf = NewBuffer(out _);
            Put16(buf, 0, 8);
            Put16(buf, 2, 19);
            buf[4] = 0;                                // comando: gravar
            buf[5] = Battery;
            buf[8] = (byte)mode;
            if (mode == ChargeMode.Custom)
            {
                buf[9] = (byte)start;
                buf[10] = (byte)stop;
            }
            Put32(buf, 20, NotRun);

            var r = Call(buf);
            uint res = Get32(r, 20);
            if (res == 0)
                return (true, mode == ChargeMode.Custom ? $"Charging between {r[28]}% and {r[29]}%" : "Saved");
            return (false, res == NotRun
                ? "The BIOS did not answer"
                : $"The BIOS refused this setting (code {(int)res})");
        }
        catch (Exception ex)
        {
            return (false, Describe(ex));
        }
    }

    // ---------- WMI ----------

    private static byte[] NewBuffer(out int len)
    {
        len = BufferLength();
        return new byte[len];
    }

    private static int BufferLength()
    {
        lock (Gate)
        {
            if (_bufferLength is int cached) return cached;
            using var searcher = new ManagementObjectSearcher(Ns, "SELECT * FROM WMI_Query");
            foreach (ManagementObject o in searcher.Get())
            {
                using (o)
                {
                    if (!string.Equals(o["InstanceName"]?.ToString(), Instance, StringComparison.OrdinalIgnoreCase)) continue;
                    if (o["QDATA"] is not ManagementBaseObject q || q["Bytes"] is not byte[] b || b.Length < 16)
                        throw new InvalidOperationException("Invalid Dell WMI descriptor");
                    string vendor = System.Text.Encoding.ASCII.GetString(b, 0, 4);
                    string sig = System.Text.Encoding.ASCII.GetString(b, 4, 4);
                    int size = (int)Get32(b, 12);
                    if (vendor != "DELL" || sig != " WMI" || size < HeaderSize)
                        throw new InvalidOperationException($"Unexpected Dell WMI descriptor ({vendor}{sig}, {size} bytes)");
                    _bufferLength = size;
                    return size;
                }
            }
            throw new InvalidOperationException("This laptop has no Dell BIOS WMI interface (WMI_Query)");
        }
    }

    private static byte[] Call(byte[] buffer)
    {
        lock (Gate)
        {
            using var bdat = new ManagementClass(Ns, "BDat", null);
            using var data = bdat.CreateInstance();
            data["Bytes"] = buffer;

            using var searcher = new ManagementObjectSearcher(Ns, "SELECT * FROM BFn");
            foreach (ManagementObject fn in searcher.Get())
            {
                using (fn)
                {
                    if (!string.Equals(fn["InstanceName"]?.ToString(), Instance, StringComparison.OrdinalIgnoreCase)) continue;
                    using var inParams = fn.GetMethodParameters("DoBFn");
                    inParams["Data"] = data;
                    using var outParams = fn.InvokeMethod("DoBFn", inParams, null);
                    if (outParams?["Data"] is ManagementBaseObject outData && outData["Bytes"] is byte[] result && result.Length >= HeaderSize)
                        return result;
                    throw new InvalidOperationException("The BIOS returned an empty buffer");
                }
            }
            throw new InvalidOperationException("This laptop has no Dell BIOS call interface (BFn)");
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        ManagementException { ErrorCode: ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace or ManagementStatus.NotFound }
            => "This laptop has no Dell BIOS battery interface",
        ManagementException { ErrorCode: ManagementStatus.AccessDenied } => "Access denied (run as administrator)",
        _ => ex.Message,
    };

    private static void Put16(byte[] b, int o, ushort v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);

    private static void Put32(byte[] b, int o, uint v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);

    private static void Put32(byte[] b, int o, int v) => BitConverter.TryWriteBytes(b.AsSpan(o), v);

    private static uint Get32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
}
