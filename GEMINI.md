# Project Context: Stutter Guard (C# WPF Application)

## Application Spec & Architecture [MEM-13]
- Goal: Package every proven DPC & interrupt latency optimization from SHABAKA-B4 (733 µs -> 11.6 µs) into a universal, guided Windows 10/11 desktop app.
- Stack: C# WPF on .NET 10 (LTS), Fluent theme (`ThemeMode="System"`), self-contained single-file `.exe`, proprietary commercial license.
- Core Principles:
  1. Reversible first: Per-tweak JSON backup and individual Revert button before any change touches the system.
  2. Hardware-aware IRQ assignment: Automatically detects topology (AMD dual-CCD X3D vs single-CCD vs Intel heterogeneous; reserves CPU 0/1 and 3D V-Cache CCD; assigns GPU, NIC RSS queues, audio path, and USB controllers to optimal secondary CCD/first-thread cores) + native core wake-up/latency benchmark (wake-up median 30%, workload speed 30%, Windows preferred-core rank 40%).
  3. Includes "Author's Desktop (SHABAKA-B4)" reference preset and experimental `ThreadDpcEnable=0` toggle.
- Always edit code and documentation files directly in place.
