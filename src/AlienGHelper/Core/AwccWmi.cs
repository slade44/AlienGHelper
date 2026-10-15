using System.Management;
using System.Runtime.InteropServices;

namespace TccG15.Core;

/// <summary>Modos térmicos. Valores conforme WMI-AWCC-doc.md do projeto original.</summary>
public enum ThermalMode : uint
{
    Custom = 0,
    /// <summary>Perfil silencioso do BIOS (0x96; em notebooks USTT o valor real é 0xA3, detectado na hora).</summary>
    Quiet = 0x96,
    Balanced = 0x97,
    GMode = 0xAB,
}

public static class ThermalModeExtensions
{
    /// <summary>
    /// Nomes exibidos na UI, no estilo do G-Helper. Os valores internos continuam iguais ao WMI e ao
    /// settings.json: Quiet = "Silent", Balanced = "Balanced", G Mode = "Turbo", Custom (curva própria) = "Manual".
    /// </summary>
    public static string DisplayName(this ThermalMode m) => m switch
    {
        ThermalMode.Quiet => "Silent",
        ThermalMode.Balanced => "Balanced",
        ThermalMode.GMode => "Turbo",
        ThermalMode.Custom => "Manual",
        _ => m.ToString(),
    };
}

public sealed class AwccException : Exception
{
    public string Detail { get; }
    public AwccException(string message, string detail) : base(message) => Detail = detail;
}

/// <summary>
/// Port de AWCCWmiWrapper.py. Fala com root\WMI : AWCCWmiMethodFunction.
/// Todos os métodos recebem um uint32 (arg2) e devolvem um uint32 (argr).
/// </summary>
public sealed class AwccWmi
{
    public const uint SensorIdFirst = 0x01, SensorIdLast = 0x30;
    public const uint FanIdFirst = 0x31, FanIdLast = 0x63;
    private const uint UsttBalanced = 0xA0;
    private const uint UsttQuiet = 0xA3, LegacyQuiet = 0x96;

    private readonly ManagementObject _obj;
    private readonly object _lock = new();
    private uint? _balancedPatch;      // null = ainda não testado
    private bool _balancedPatchProbed;
    private uint? _quietValue;      // valor de Silent aceito pelo BIOS (0xA3 ou 0x96)

    private AwccWmi(ManagementObject obj) => _obj = obj;

    public static AwccWmi Open()
    {
        try
        {
            var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM AWCCWmiMethodFunction");
            foreach (var o in searcher.Get())
                return new AwccWmi((ManagementObject)o);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            throw new AwccException("Couldn't access the AWCC WMI class.", "Make sure you're running as Administrator.");
        }
        catch (ManagementException)
        {
            throw new AwccException("AWCC WMI class not found in the system.",
                "You don't have some drivers installed or your system is not supported.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new AwccException("Couldn't access the AWCC WMI class.", "Make sure you're running as Administrator.");
        }

        throw new AwccException("Couldn't instantiate AWCC WMI class.", "Make sure you're running as Administrator.");
    }

    // ---------- leitura ----------

    public int? GetSensorTemperature(uint sensorId) =>
        InRange(sensorId, SensorIdFirst, SensorIdLast)
            ? ToInt(Call("Thermal_Information", ((sensorId & 0xFF) << 8) | 4)) : null;

    public int? GetFanRpm(uint fanId) =>
        InRange(fanId, FanIdFirst, FanIdLast)
            ? ToInt(Call("Thermal_Information", ((fanId & 0xFF) << 8) | 5)) : null;

    public int? GetFanRpmPercent(uint fanId) =>
        InRange(fanId, FanIdFirst, FanIdLast)
            ? ToInt(Call("Thermal_Information", ((fanId & 0xFF) << 8) | 6)) : null;

    /// <summary>Lista (fanId, sensores relacionados) – equivalente a GetFanIdsAndRelatedSensorsIds().</summary>
    public List<(uint FanId, uint[] SensorIds)> DiscoverFans()
    {
        var result = new List<(uint, uint[])>();
        for (uint fan = FanIdFirst; fan <= FanIdLast; fan++)
        {
            var count = Call("GetFanSensors", ((fan & 0xFF) << 8) | 1);
            if (!count.HasValue || count.Value == 0) continue;

            var ids = new List<uint>();
            for (uint i = 0; i < count.Value; i++)
            {
                var id = Call("GetFanSensors", ((i & 0xFF) << 16) | ((fan & 0xFF) << 8) | 2);
                if (id.HasValue) ids.Add(id.Value);
            }
            result.Add((fan, ids.ToArray()));
        }
        return result;
    }

    // ---------- escrita ----------

    public bool ApplyThermalMode(ThermalMode mode)
    {
        uint value = (uint)mode;

        // Silent: notebooks com USTT usam USTT_Quiet (0xA3); os outros, Quiet (0x96). Tenta e guarda o que funcionou.
        if (mode == ThermalMode.Quiet)
        {
            if (_quietValue is uint known) return ThermalControl(known);
            uint[] order = _balancedPatchProbed && !_balancedPatch.HasValue
                ? new[] { LegacyQuiet, UsttQuiet }
                : new[] { UsttQuiet, LegacyQuiet };
            foreach (var v in order)
            {
                if (!ThermalControl(v)) continue;
                _quietValue = v;
                return true;
            }
            return false;
        }

        // Em notebooks com USTT o "Balanced" usa outro valor (0xA0). Testa uma vez e guarda.
        if (mode == ThermalMode.Balanced)
        {
            if (!_balancedPatchProbed)
            {
                _balancedPatch = ThermalControl(UsttBalanced) ? UsttBalanced : null;
                _balancedPatchProbed = true;
            }
            if (_balancedPatch.HasValue) value = _balancedPatch.Value;
        }
        return ThermalControl(value);
    }

    public bool SetFanSpeedPercent(uint fanId, int speed)
    {
        if (!InRange(fanId, FanIdFirst, FanIdLast)) return false;
        uint s = (uint)Math.Clamp(speed, 0, 0xFF);
        return Call("Thermal_Control", (s << 16) | ((fanId & 0xFF) << 8) | 2) == 0;
    }

    private bool ThermalControl(uint arg) => Call("Thermal_Control", ((arg & 0xFF) << 8) | 1) == 0;

    // ---------- baixo nível ----------

    private static bool InRange(uint v, uint lo, uint hi) => v >= lo && v <= hi;
    private static int? ToInt(uint? v) => v.HasValue ? (int)v.Value : null;

    private uint? Call(string method, uint arg)
    {
        lock (_lock)
        {
            try
            {
                using var inParams = _obj.GetMethodParameters(method);
                inParams["arg2"] = arg;
                using var outParams = _obj.InvokeMethod(method, inParams, null);
                var raw = outParams?["argr"];
                if (raw is null) return null;
                uint v = Convert.ToUInt32(raw);
                return v == 0xFFFFFFFF ? null : v;
            }
            catch (Exception ex) when (ex is ManagementException or COMException or InvalidCastException
                                           or FormatException or OverflowException)
            {
                return null;
            }
        }
    }
}
