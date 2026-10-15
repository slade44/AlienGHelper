# How it works

Technical notes on how Alien G-Helper talks to the hardware. Useful if you want to port it to another model or debug a feature.

## Architecture

```
Program / App  ──►  AppController (1 s loop: sensors, fail-safe, fan curves, settings)
                     │
AppHost ────────────┼── PowerService        CPU limits (PawnIO + Ryzen SMU) · GPU (NVAPI)
  (services +       ├── KeyboardLightingService  AlienFX APIv4 over USB HID
   windows)         ├── AppProfileWatcher   per-app profiles (process list every 3 s)
                    ├── SystemSensors       usage, clocks, power, RAM/VRAM (PDH, NVML)
                    ├── FpsMonitor          ETW Present events (only while Monitor is open)
                    └── DellBattery         charge mode via Dell BIOS calling interface
UI (Avalonia): CompactWindow (main) · FansWindow · KeyboardWindow · MonitorWindow · ExtraWindow
Tray (WinForms NotifyIcon) · HotKeyService (G key → Turbo)
```

The services live in `AppHost` and keep running when every window is closed. Only the main window and the keyboard window are created at startup; Fans, Monitor and Extra are created the first time they are opened. When everything is hidden in the tray, the app trims its working set.

## Thermal modes and fans — AWCC WMI

`Core/AwccWmi.cs`, `Core/ThermalController.cs` (port of tcc-g15).

- WMI class `root\WMI : AWCCWmiMethodFunction`, method `Thermal_Control` / `Thermal_Information`.
- Modes: Balanced `0x97`, Quiet `0x96` (USTT laptops use `0xA3`, detected at runtime), G-Mode `0xAB`, Custom/Manual `0`.
- In **Manual**, the app sets each fan with `Thermal_Control` operation 2 (`(speed << 16) | (fanId << 8) | 2`), raw value 0–255.
- In Silent / Balanced / Turbo the BIOS owns the fans; the app only selects the mode.

**Fan calibration** (`AppController.CalibrateFansAsync`): in Manual mode, sends raw 100, 110, … 250, 255, waits ~6 s per step and averages the RPM. When the RPM stops rising (< 2 % and < 40 RPM), the previous step is that fan's real maximum. Afterwards, 100 % on the curve maps to the calibrated raw value (`Settings.FanMaxBoost`).

## CPU power — PawnIO + Ryzen SMU

`Core/Power/PawnIO.cs`, `Core/Power/RyzenSmu.cs`, `Core/Power/PowerService.cs` (adapted from G-Helper).

- PawnIO kernel driver, opened directly with `DeviceIoControl`; the embedded module `Resources/RyzenSMU.bin` exposes the SMU mailbox.
- Rembrandt (Ryzen 6000) uses the "mobile" mailbox at MP1 `0x03B10528 / 0x03B10578 / 0x03B10998`.
- SPL (STAPM), sPPT (slow) and fPPT (fast) are written with the RyzenAdj command IDs.
- On Ryzen 6000 Dell uses AMD PMF, which writes its own limits back. The app re-applies every 10 s and ~3 s after any thermal mode or Windows power mode change, and after resume.

## GPU — NVAPI

`Core/Power/NvidiaControl.cs`: NvAPIWrapper (`GPUApi` power policies and P-state clock offsets) and `nvidia-smi -lgc` for the core clock limit.

## Battery charge limit — Dell BIOS calling interface

`Core/Power/DellBattery.cs`. Reverse engineered from Dell Power Manager 3.9 (`smblib.dll`, `systeminterop.dll`).

1. Buffer size from the descriptor: `root\wmi : WMI_Query` (instance `ACPI\PNP0C14\0_0`), property `QDATA.Bytes` = `"DELL" " WMI" version length…`.
2. Call: `root\wmi : BFn.DoBFn(Data = BDat { Bytes = buffer })` on instance `ACPI\PNP0C14\0_0`.
3. Buffer layout (packed): `class u16 · select u16 · input[4] u32 · output[4] u32 · ArgAttrib u32 · BLength u32`.

| Operation | Class | Select | Input | Output |
|---|---|---|---|---|
| Read | 8 | 18 | byte 4 = 3 (command), byte 5 = battery (1) | byte 24 = mode, 25 = start %, 26 = stop %, dword 28 = supported modes |
| Write | 8 | 19 | byte 4 = 0, byte 5 = battery, byte 8 = mode, 9 = start %, 10 = stop % | dword 20 = result (0 = OK) |

Modes: 1 Standard · 2 ExpressCharge · 3 Primarily AC · 4 Adaptive · 5 Custom. Custom: start 50–95 %, stop 55–100 %, stop − start ≥ 5.

## Keyboard lighting — AlienFX APIv4

`Core/Lighting/` (ported from T-Troll's AlienFX-SDK).

- USB HID, vendor `0x187C`, APIv4 reports.
- Live colors use the temporary animation slot (`control 4` remove → `1` new → phases → play).
- **Save to keyboard memory** writes the same phases to the default slot `0x61` (`{4,0,0x61}`, `{1,0,0x61}`, phases, `{2,0,0x61}`, `{6,0,0x61}`), so the colors stay after boot, sleep and with the app closed.

## FPS / frametime — ETW

`Core/Sensors/FpsMonitor.cs`: real-time ETW session (`Microsoft-Windows-DXGI` Present_Start, event 42, and D3D9 Present, event 1), 4 MB buffers, foreground process only. Runs only while the Monitor window is visible. Vulkan / OpenGL games are not counted.

## Settings

`%APPDATA%\TccG15\settings.json` (shared with the full TCC G15 edition). Saved by the controller loop when something changes.
