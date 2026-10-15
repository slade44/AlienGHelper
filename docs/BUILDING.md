# Building Alien G-Helper

> 🇧🇷 Português: [BUILDING.pt-BR.md](BUILDING.pt-BR.md)

Step-by-step guide to produce `AlienGHelper.exe` on Windows 10/11 (64-bit).

## 1. Install the .NET 8 SDK (once)

```powershell
winget install Microsoft.DotNet.SDK.8
```

No winget? Download "SDK 8.0 – Windows x64" from https://dotnet.microsoft.com/download/dotnet/8.0.

Close and reopen the terminal, then check:

```powershell
dotnet --version   # 8.0.xxx or newer
```

The first build downloads the NuGet packages (Avalonia, NvAPIWrapper, TraceEvent…), so it needs internet.

## 2. Get the source

```powershell
git clone https://github.com/<your-user>/AlienGHelper.git
cd AlienGHelper\src\AlienGHelper
```

Without Git: **Code › Download ZIP** on GitHub, extract it and open a terminal in `src\AlienGHelper` (the folder with `AlienGHelper.csproj`).

## 3. Run it for a quick test

The app requires **administrator** rights (BIOS/EC via WMI, Ryzen SMU via PawnIO, ETW for the FPS counter). Open the terminal with **Run as administrator**, then:

```powershell
dotnet run -c Release
```

In a non-elevated terminal you get *"The requested operation requires elevation"* (error 740).

## 4. Build the executable

### Option A — single .exe (needs the .NET 8 Desktop Runtime)

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

### Option B — self-contained single .exe (runs anywhere, ~80–100 MB)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Output: `src\AlienGHelper\publish\AlienGHelper.exe`. Copy it to a permanent folder (e.g. `C:\Program Files\Alien G-Helper\`).

## 5. First run

1. **Close Alienware Command Center and Dell Power Manager.**
2. Double-click `AlienGHelper.exe` and accept the administrator prompt.
3. Optional: **Settings › Other › Run on Startup**. Re-tick it whenever you move the .exe (the scheduled task stores its path).

| Optional feature | Needs |
|---|---|
| CPU power (Eco / Balanced / Performance / Custom) | **PawnIO** driver: `winget install namazso.PawnIO`, then restart the app |
| GPU clocks, usage, VRAM | NVIDIA driver |
| FPS monitor | administrator (default) |
| Battery charge limit | Dell BIOS with the battery interface (G15 has it) |

## 6. Updating

1. Quit the app (tray › Exit).
2. `git pull`
3. Run the publish command again and replace the old `AlienGHelper.exe`.

Settings live in `%APPDATA%\TccG15\settings.json` and survive updates.

## 7. Troubleshooting

| Symptom | Fix |
|---|---|
| `dotnet` is not recognized | Reopen the terminal after installing the SDK, or reboot. |
| NuGet restore errors (`NU1101`, `NU1301`) | Check the connection, run `dotnet nuget locals all --clear`, try again. |
| `error CS…` / `AVLN…` | Open an issue with the full message (file, line and text). |
| "Another instance of this app is already running" | Exit it from the tray or end **AlienGHelper** in Task Manager. |
| Process runs but no window | Check `%APPDATA%\TccG15\startup.log` (last step) and `crash.log`. |
| Fans or lighting go back to defaults | Close AWCC and Dell Power Manager. |
| "PawnIO driver is not installed" | `winget install namazso.PawnIO` and restart the app. |

## CI

`.github/workflows/build.yml` builds the app on every push and pull request and uploads `AlienGHelper.exe` as an artifact. Pushing a tag like `v0.1.0` also creates a GitHub Release with both builds attached.
