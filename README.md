# GPU VRAM Monitor

A Windows desktop app that shows per-process GPU dedicated VRAM usage in real time. Fills a gap — no good per-process VRAM tools exist for AMD GPUs on Windows.

## What it does

- Lists every process using GPU dedicated VRAM, sorted by usage
- Shows total VRAM usage with GPU name detection
- Per-process progress bars showing relative usage
- Exclude `dwm.exe` toggle (its values are wildly inflated by a Windows bug)
- Pause/resume, configurable refresh interval (1–10s)
- System tray support with minimize-to-tray and start-on-boot
- Right-click a process to open file location or end task

## How it works

Reads `\GPU Process Memory(*)\Dedicated Usage` and `\GPU Adapter Memory(*)\Dedicated Usage` performance counters. Parses instance names like `luid_..._phys_0_pid_1234` to extract PIDs, then resolves process names. Total VRAM capacity detected via registry (`HardwareInformation.qwMemorySize`) with WMI and GPU-name fallbacks.

**Known limitation:** Per-process VRAM values are overestimated on Windows (Microsoft bug — cross-process shared memory is double-counted). The relative ordering is still useful. The adapter-level total is accurate.

## Tech stack

| Layer | Tech |
|-------|------|
| UI | C# WPF (.NET 8) |
| Data | `System.Diagnostics.PerformanceCounter` |
| System info | `System.Management` (WMI), Windows Registry |
| Tray icon | Hardcodet.NotifyIcon.Wpf |

## Project structure

```
GpuVramMonitor/
├── App.xaml / App.xaml.cs          # Application entry
├── MainWindow.xaml / .xaml.cs      # Main UI + refresh logic
├── Models/
│   ├── GpuProcessInfo.cs           # Per-process VRAM data model
│   └── AppSettings.cs              # Settings model (start on boot, tray behavior)
├── Services/
│   ├── VramMonitorService.cs       # Core VRAM reading (perf counters, WMI, registry)
│   └── SettingsService.cs          # JSON settings persistence + auto-start registry
└── AssemblyInfo.cs
```

## Build and run

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows

```bash
# Build
dotnet build

# Run
dotnet run

# Or run the compiled executable
dotnet build -c Release
GpuVramMonitor\bin\Release\net8.0-windows\GpuVramMonitor.exe
```
