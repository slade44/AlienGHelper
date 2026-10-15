<p align="center">
  <img src="docs/logo.png" width="110" alt="Alien G-Helper logo">
</p>

<h1 align="center">Alien G-Helper</h1>

<p align="center">
  A lightweight, G-Helper-style control center for <b>Dell G15</b> laptops (and other Dell G / Alienware models with the AWCC WMI interface).<br>
  Fans, CPU/GPU power, battery charge limit, keyboard lighting and a hardware monitor — without Alienware Command Center.
</p>

<p align="center">
  <a href="docs/BUILDING.md">Build</a> ·
  <a href="docs/HOW-IT-WORKS.md">How it works</a> ·
  <a href="CHANGELOG.md">Changelog</a>
</p>

---

<!-- Screenshots: save your captures as `docs/screenshots/main.png` and `fans.png`, then remove the comment markers around the images below.
<p align="center">
  <img src="docs/screenshots/main.png" width="300" alt="Main window">
  <img src="docs/screenshots/fans.png" width="520" alt="Fans and Power">
</p>
-->

## Features

| Area | What you get |
|---|---|
| **Fan Control** | Silent, Balanced, Turbo (G-Mode) and Manual (your own CPU/GPU fan curves). Clamp-to-grid editor, live point on the curve, **fan calibration** (finds the real top speed of each fan), fail-safe that switches to Turbo when it gets too hot. |
| **CPU power** | One-click presets **Eco (15 W)**, **Balanced (45 W)**, **Performance (90 W)** or Custom SPL / sPPT / fPPT sliders, applied to the Ryzen SMU through the PawnIO driver and re-applied when the firmware (AMD PMF) resets them. Windows power mode, CPU boost and "unlock clocks" in the Advanced tab. |
| **GPU (NVIDIA)** | Core and memory clock offsets, core clock limit and power target (NVAPI / `nvidia-smi`). |
| **Battery** | Dell BIOS charge modes: Standard, ExpressCharge, Adaptive, Primarily AC and **Custom charge limit** (e.g. stop at 80 %). Stored in the BIOS, works with the app closed. |
| **Keyboard lighting** | AlienFX APIv4 over USB HID: per-zone colors, Static / Pulse / Breath / Spectrum / Rainbow, brightness, saved to the keyboard memory (keeps the colors at boot and with the app closed), restored after sleep. |


## Requirements

- Windows 10 (19041+) or Windows 11, 64-bit
- **.NET 8 Desktop Runtime** (not needed for the self-contained build)
- Runs **as administrator** (needed for WMI, the SMU and ETW — the app asks automatically)
- Optional:
  - **[PawnIO](https://github.com/namazso/PawnIO)** driver for CPU power limits: `winget install namazso.PawnIO`
  - NVIDIA driver for the GPU features

### Tested hardware

| Laptop | CPU / GPU | Status |
|---|---|---|
| Dell G15 5525 | Ryzen 7 6800H / RTX 3060 Laptop | ✅ main machine |

## Download

Get `AlienGHelper.exe` from the [Releases](../../releases) page (or the artifact of the latest **Build** workflow run), put it in a folder of your choice and run it.

To start with Windows, open **Settings › Other › Run on Startup** (re-tick it if you move the .exe).

## Build from source

```powershell
winget install Microsoft.DotNet.SDK.8
git clone https://github.com/slade44/AlienGHelper.git
cd AlienGHelper
dotnet publish src/AlienGHelper/AlienGHelper.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
.\publish\AlienGHelper.exe
```

Step-by-step guide, self-contained build and troubleshooting: **[docs/BUILDING.md](docs/BUILDING.md)**.

## Where things are stored

| What | Where |
|---|---|
| Settings (curves, colors, profiles, limits) | `%APPDATA%\AlienGHelper\settings.json` |
| Startup log / crash log | `%APPDATA%\AlienGHelper\startup.log`, `crash.log` |
| Battery charge mode | Dell BIOS |
| Keyboard colors (when "Save to keyboard memory" is on) | keyboard controller |
| Run on Startup | Task Scheduler task `AlienGHelper` |

## Safety notes

- Everything here talks directly to the BIOS, the embedded controller, the CPU's SMU and the GPU driver. It uses the same interfaces as Dell's own tools and G-Helper, and the firmware keeps its own thermal protections, but **use it at your own risk**.
- High CPU limits (Performance) only help as far as the cooling allows; the CPU still throttles at its temperature limit.
- Fan calibration runs the fans at full speed for 1–2 minutes.

## Credits

Alien G-Helper stands on the shoulders of:

- **[tcc-g15](https://github.com/AlexIII/tcc-g15)** by AlexIII — original thermal control (GPL-3.0)
- **[G-Helper](https://github.com/seerge/g-helper)** by seerge — the UI idea, PawnIO / Ryzen SMU code (GPL-3.0)
- **[alienfx-tools / AlienFX-SDK](https://github.com/T-Troll/alienfx-tools)** by T-Troll — keyboard lighting protocol (MIT)
- **[PawnIO](https://github.com/namazso/PawnIO)** by namazso, **[RyzenAdj](https://github.com/FlyGoat/RyzenAdj)** — SMU access and commands

Full notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

[GPL-3.0](LICENSE), as required by tcc-g15 and G-Helper.

## Disclaimer

This is an independent community project. It is **not affiliated with, endorsed or supported by Dell, Alienware, ASUS or the G-Helper project**. Dell, Alienware, G-Series and related names are trademarks of their respective owners and are used here only to describe compatibility.
