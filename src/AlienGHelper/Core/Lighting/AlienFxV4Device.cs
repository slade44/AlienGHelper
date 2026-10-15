// Protocolo AlienFX APIv4 (relatórios HID de 34 bytes, report ID 0, HidD_SetOutputReport).
// Tabelas de comandos e sequências portadas de AlienFX_SDK (alienfx-controls.h / AlienFX_SDK.cpp),
// T-Troll / Gurjot95, licença MIT – veja THIRD-PARTY-NOTICES.md.
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace TccG15.Core.Lighting;

public enum LightActionType : byte
{
    Color = 0,
    Pulse = 1,
    Morph = 2,
    Breathing = 3,
    Spectrum = 4,
    Rainbow = 5,
    Power = 6,
}

/// <summary>Fase de um efeito (equivale a Afx_action do SDK).</summary>
public readonly record struct LightAction(LightActionType Type, byte Time, byte Tempo, byte R, byte G, byte B);

/// <summary>
/// Fase como ela vai no pacote ADD_ACTION (0x24): modo (0 color, 1 pulse, 2 morph), duração em ms e tempo,
/// ambos em 16 bits big-endian. Layout confirmado no AlienwareController do OpenRGB.
/// </summary>
public readonly record struct V4Phase(byte Mode, ushort Duration, ushort Tempo, byte R, byte G, byte B)
{
    public const byte ModeColor = 0, ModePulse = 1, ModeMorph = 2;
}

public sealed class AlienFxV4Device : IDisposable
{
    private const int Length = 34;           // OutputReportByteLength do APIv4
    private const byte StatusBusy = 34;      // ALIENFX_V4_BUSY

    // COMMV4_*: bytes do comando a partir do offset 1 (o offset 0 é o report ID = 0)
    private static readonly byte[] CmdControl   = { 0x03, 0x21, 0x00, 0x03, 0xff, 0xff }; // [4]=tipo: 1 novo, 3 aplicar, 4 remover
    private static readonly byte[] CmdColorSel  = { 0x03, 0x23, 0x01, 0x00, 0x01 };       // [6]=ID da luz
    private static readonly byte[] CmdColorSet  = { 0x03, 0x24, 0x00, 0x07, 0xd0, 0x00, 0xfa };
    private static readonly byte[] CmdTurnOn    = { 0x03, 0x26 };                          // brilho
    private static readonly byte[] CmdOneColor  = { 0x03, 0x27 };                          // cor fixa em N luzes
    // v4OpCodes: Color, Pulse, Morph, Breathing, Spectrum, Rainbow, Power
    private static readonly byte[] OpCodes = { 0xd0, 0xdc, 0xcf, 0xdc, 0x82, 0xac, 0xe8 };

    private readonly SafeFileHandle _handle;
    private bool _inSet;

    private AlienFxV4Device(SafeFileHandle handle) => _handle = handle;

    public static AlienFxV4Device? Open(string path)
    {
        var h = NativeHid.OpenDevice(path);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new AlienFxV4Device(h);
    }

    public void Dispose() => _handle.Dispose();

    // ---------- operações ----------

    /// <summary>Remove a lista atual e inicia uma nova (como Functions::Reset do SDK para APIv4).</summary>
    public bool Reset()
    {
        WaitForReady();
        Send(CmdControl, (4, new byte[] { 4 }));
        _inSet = Send(CmdControl, (4, new byte[] { 1 }));
        return _inSet;
    }

    /// <summary>Fecha a lista e aplica ("finish and play").</summary>
    public bool UpdateColors()
    {
        if (_inSet) _inSet = !Send(CmdControl);
        return !_inSet;
    }

    /// <summary>Cor fixa em várias luzes de uma vez (SetMultiColor).</summary>
    public bool SetMultiColor(IReadOnlyList<byte> lights, byte r, byte g, byte b)
    {
        if (lights.Count == 0) return true;
        if (!_inSet) Reset();

        bool ok = false;
        for (int i = 0; i < lights.Count; i += 26)
        {
            if (i > 0) { UpdateColors(); Reset(); }   // o SDK faz isto entre blocos de 26 luzes
            var chunk = lights.Skip(i).Take(26).ToArray();
            ok = Send(CmdOneColor, (3, new byte[] { r, g, b, 0, (byte)chunk.Length }), (8, chunk));
        }
        return ok;
    }

    /// <summary>
    /// Efeito no formato do SDK (SetV4Action). O SDK monta os bytes como {tipo, time, opcode, 0, tempo, R, G, B};
    /// isso equivale a duração = (time &lt;&lt; 8) | opcode e tempo = 0x00tempo. Os bytes enviados são os mesmos de antes.
    /// </summary>
    public bool SetAction(byte light, IReadOnlyList<LightAction> actions) =>
        SetPhases(light, actions.Select(a => new V4Phase(
            a.Type < LightActionType.Breathing ? (byte)a.Type : V4Phase.ModeMorph,
            (ushort)((a.Time << 8) | OpCodes[(int)a.Type]),
            a.Type == LightActionType.Color ? (ushort)0xfa : a.Tempo,
            a.R, a.G, a.B)).ToList());

    /// <summary>Seleciona a luz e envia as fases, até 3 por pacote (como SDK e OpenRGB).</summary>
    public bool SetPhases(byte light, IReadOnlyList<V4Phase> phases)
    {
        if (phases.Count == 0) return false;
        if (!_inSet) Reset();
        return WritePhases(light, phases);
    }

    /// <summary>Slot de animação gravado na memória do controlador: é o que ele mostra ao ligar/acordar.</summary>
    private const byte DefaultSlot = 0x61;

    /// <summary>
    /// Grava os efeitos na memória do teclado como animação padrão (Functions::SaveLightsState do SDK, APIv4):
    /// control 4 (remover) e 1 (nova) no slot 0x61, as fases de cada luz, control 2 (fechar) e 6 (definir como padrão).
    /// Sem isso o teclado só mostra as cores enquanto o app as reaplica; ao acordar ou após o timeout ele volta
    /// para a animação que estava gravada (a do AWCC ou a de fábrica).
    /// </summary>
    public bool SaveAsDefault(IReadOnlyList<(byte Light, IReadOnlyList<V4Phase> Phases)> lights)
    {
        if (_inSet) UpdateColors();
        WaitForReady();
        Send(CmdControl, (4, new byte[] { 4, 0, DefaultSlot }));
        bool ok = Send(CmdControl, (4, new byte[] { 1, 0, DefaultSlot }));
        foreach (var (light, phases) in lights)
            if (phases.Count > 0) ok &= WritePhases(light, phases);
        ok &= Send(CmdControl, (4, new byte[] { 2, 0, DefaultSlot }));
        ok &= Send(CmdControl, (4, new byte[] { 6, 0, DefaultSlot }));
        WaitForReady(3000);
        return ok;
    }

    private bool WritePhases(byte light, IReadOnlyList<V4Phase> phases)
    {
        Send(CmdColorSel, (6, new[] { light }));

        bool ok = false;
        for (int i = 0; i < phases.Count; i += 3)
        {
            var mods = new List<(int, byte[])>();
            for (int k = 0; k < 3 && i + k < phases.Count; k++)
            {
                var p = phases[i + k];
                mods.Add((3 + 8 * k, new byte[]
                {
                    p.Mode,
                    (byte)(p.Duration >> 8), (byte)p.Duration,
                    (byte)(p.Tempo >> 8), (byte)p.Tempo,
                    p.R, p.G, p.B,
                }));
            }
            ok = Send(CmdColorSet, mods.ToArray());
        }
        return ok;
    }

    /// <summary>Brilho de hardware 0..255 nas luzes indicadas (Functions::SetBrightness do SDK, APIv4).</summary>
    public bool SetBrightness(byte brightness, IReadOnlyList<byte> lights)
    {
        if (_inSet) UpdateColors();
        if (lights.Count == 0) return true;

        int bright = brightness * 100 / 255;          // ((b * gbr/255) * 0x64) / 0xff com gbr = 255
        byte level = (byte)(0x64 - bright);           // 0 = brilho máximo, 100 = apagado

        bool ok = false;
        for (int i = 0; i < lights.Count; i += 28)
        {
            var chunk = lights.Skip(i).Take(28).ToArray();
            ok = Send(CmdTurnOn, (3, new byte[] { level, 0, (byte)chunk.Length }), (6, chunk));
        }
        return ok;
    }

    // ---------- baixo nível ----------

    private bool Send(byte[] command, params (int Pos, byte[] Val)[] mods)
    {
        var buf = new byte[Length];                       // zerado; buf[0] = report ID 0
        Array.Copy(command, 0, buf, 1, command.Length);
        foreach (var (pos, val) in mods)
        {
            if (pos >= Length) continue;
            Array.Copy(val, 0, buf, pos, Math.Min(val.Length, Length - pos));
        }
        return NativeHid.HidD_SetOutputReport(_handle, buf, (uint)Length);
    }

    private byte GetStatus()
    {
        var buf = new byte[Length];
        return NativeHid.HidD_GetInputReport(_handle, buf, (uint)Length) ? buf[2] : (byte)0;
    }

    /// <summary>Espera o dispositivo sair do estado "ocupado" (no SDK não há limite; aqui há, por segurança).</summary>
    private void WaitForReady(int timeoutMs = 1500)
    {
        var sw = Stopwatch.StartNew();
        while (GetStatus() == StatusBusy && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(20);
    }
}
