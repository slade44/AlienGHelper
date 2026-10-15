# Third-party notices

Alien G-Helper is licensed under the GPL-3.0 (see `LICENSE`). It includes or is derived from the following works.

## AlienFX-SDK / alienfx-tools (MIT)

The USB HID lighting code in `src/AlienGHelper/Core/Lighting/` (device detection by HID report length, the APIv4
command tables and the Reset / color / action / brightness / update command sequences) was ported
to C# from **AlienFX-SDK** (`AlienFX_SDK.cpp`, `AlienFX_SDK.h`, `alienfx-controls.h`) by
**T-Troll**, itself based on Gurjot95's AlienFX_SDK, from the project
<https://github.com/T-Troll/alienfx-tools>, licensed under the MIT License.

If you redistribute this program, include the full MIT license text from
<https://github.com/T-Troll/alienfx-tools/blob/master/LICENSE> together with this notice.

## tcc-g15 (GPL-3.0)

The thermal / fan control part is derived from <https://github.com/AlexIII/tcc-g15> (GPL-3.0).
The combined program therefore remains under GPL-3.0.

## G-Helper (GPL-3.0)

`src/AlienGHelper/Core/Power/PawnIO.cs` and `RyzenSmu.cs` are adapted from G-Helper
(`app/Pawn/PawnIOWrapper.cs`, `app/Pawn/RyzenSmu.cs`) by Serge (seerge),
<https://github.com/seerge/g-helper>, GPL-3.0. The SMU commands and mailbox addresses come from
RyzenAdj (<https://github.com/FlyGoat/RyzenAdj>, LGPL-3.0). The GPU clock offset / `nvidia-smi -lgc`
approach follows G-Helper's `NvidiaGpuControl`.

## PawnIO and the RyzenSMU module

The kernel driver PawnIO by namazso (<https://github.com/namazso/PawnIO>) is **not** bundled: the user
installs it. `src/AlienGHelper/Resources/RyzenSMU.bin` is the compiled PawnIO module shipped with G-Helper (based on
<https://github.com/namazso/PawnIO.Modules>; see that repository for its license). Keep this notice
and the module's license when redistributing.

## NvAPIWrapper.Net (LGPL-3.0)

GPU power target and clock offsets use NvAPIWrapper by Soroush Falahati
(<https://github.com/falahati/NvAPIWrapper>), referenced as the NuGet package `NvAPIWrapper.Net`
and loaded as a separate DLL, under the LGPL-3.0.

## Microsoft.Diagnostics.Tracing.TraceEvent (MIT)

The FPS monitor uses TraceEvent (<https://github.com/microsoft/perfview>), MIT License, as a NuGet package.

## Avalonia UI (MIT)

The user interface uses Avalonia UI (<https://github.com/AvaloniaUI/Avalonia>), MIT License,
referenced as NuGet packages (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent).

## Dell Power Manager (interoperability)

`src/AlienGHelper/Core/Power/DellBattery.cs` reimplements, for interoperability, the BIOS calls Dell Power
Manager uses to read and set the battery charge mode. No Dell code or binaries are included.

## App icon

The alien mask icon (`src/AlienGHelper/Resources/app.ico`, `alien_icon.png`, `docs/logo.png`) was supplied by the repository owner.
If you fork the project and do not hold the rights to it, replace it with your own.
