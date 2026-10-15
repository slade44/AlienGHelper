# Changelog

All notable changes to this project are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/), versions follow [SemVer](https://semver.org/).

## [0.1.0] — 2026-10-15

First public release of **Alien G-Helper** (compact edition of TCC G15).

### Added
- Compact G-Helper-style main window: Fan Control, CPU power, System, Laptop Keyboard, Battery Charge Limit.
- Fan Control: Silent, Balanced, Turbo (G-Mode) and Manual curves for CPU and GPU, clamp to grid, fail-safe.
- **Fan calibration**: finds each fan's real top speed; 100 % on the curves maps to it.
- CPU power presets **Eco 15 W / Balanced 45 W / Performance 90 W** and Custom SPL / sPPT / fPPT (PawnIO + Ryzen SMU).
- Per-app profiles with thermal mode, fan curves and CPU power preset.
- GPU core/memory offsets, core clock limit and power target (NVIDIA).
- **Battery charge limit** through the Dell BIOS (Standard, ExpressCharge, Adaptive, Primarily AC, Custom).
- Keyboard lighting (AlienFX APIv4): per-zone colors, effects, brightness, saved to the keyboard memory.
- Monitor window: temperatures, usage, clocks, power, RAM/VRAM, RPM, activity chart, FPS and frametime graph (ms).
- Tray icon with tooltip stats, quick mode menu, first-close "still running in the tray" notice, G key toggles Turbo.
- Mica or Flat window style, light/dark/system theme, accent color.

### Changed
- Detail windows are created on first use and the FPS session only runs while Monitor is open (lower RAM use).

### Removed
- GPU undervolt, per-thermal-mode CPU limits, Acrylic/Tabbed/Studio styles (kept the app simpler).
