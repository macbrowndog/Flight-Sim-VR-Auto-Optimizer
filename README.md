# VR Auto-Optimizer

VR Auto-Optimizer is a .NET 8 WPF application for launching selected Windows flight simulators with temporary, recoverable system adjustments.

It scans the local PC, explains the likely MSFS impact of running applications and services, applies selected performance settings, launches the simulator, monitors the exact new simulator process, and restores recorded system state when the flight ends.

> [!IMPORTANT]
> **Run VR Auto-Optimizer as Administrator when using the Aggressive profile.** Aggressive features require elevated Windows permissions, including stopping and restarting services, changing protected registry values, applying system-level network and memory tuning, and restoring those settings afterward. Right-click `SimVROptimizer.exe` and select **Run as administrator**, then approve the Windows User Account Control prompt. Without administrator access, service stopping and other Aggressive operations may fail or be skipped.

## Features

- Automatic detection of ten MSFS, DCS World, X-Plane, and IL-2 configurations
- Standard and Aggressive optimization profiles with editable granular controls
- Manual checkbox control or an Automatic session workflow
- Configurable Virtual Desktop, Pimax Play, SteamVR, or no-runtime launching
- Optional `-FastLaunch` startup for Microsoft Flight Simulator 2024 on both Steam and Microsoft Store
- Live five-stage Prepare, Optimize, VR Runtime, Simulator, and Restore pipeline
- Persistent custom process kill and executable restart rules
- Automatic application and service restoration through a transaction journal
- CPU vendor/model and topology detection with AMD X3D-safe scheduling
- Selectable simulator priority and optional Intel hybrid performance-core CPU Sets
- Temporary Ultimate Performance power plan and NVIDIA persistence control
- Standard profile includes DNS flush, High process priority, and vendor-aware CPU Sets; service stopping is reserved for Aggressive mode
- Aggressive profile adds reversible Game Bar/Game DVR, fullscreen-optimization, timer-resolution, process power-throttling, and multimedia/network tuning
- Optional one-time standby-memory clearing in Aggressive mode
- Background application and service impact guidance
- Protected Steam, Xbox/MSFS, VR/OpenXR, networking, security, and flight-control components
- Content Creator Mode for OBS, Streamlabs, Stream Deck, NVIDIA Broadcast, Voicemeeter, Elgato, and other capture tools
- Aviation instrument-themed dark interface with live session status and rotating logs
- Interrupted-session recovery on the next application start

## Install

1. Download the latest Windows x64 package from [Releases](https://github.com/macbrowndog/Flight-Sim-VR-Auto-Optimizer/releases/latest).
2. Extract the entire ZIP to a writable folder.
3. Install the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) if it is not already installed.
4. For Standard use, run `SimVROptimizer.exe` normally. For Aggressive use, right-click it and select **Run as administrator**.
5. Approve the Windows administrator prompt, select a detected simulator and session workflow, then choose **Start Flight Session**. Close the simulator normally to trigger restoration.

## Safety model

- Starting a session performs the selected real optimization after confirmation and administrator approval.
- Real sessions write `%LOCALAPPDATA%\SimVROptimizer\active-session.json` before changing state.
- Every session uses a `finally` restoration path.
- An interrupted session is detected on the next start and must be restored before another session can begin.
- Services are restarted only when they were running before the optimizer stopped them.
- NVIDIA persistence is restored per GPU to its original value.
- A temporary Ultimate Performance plan is deleted after the original plan is reactivated.
- The optimizer tracks a newly created simulator PID. It does not attach to a matching process that was already running.
- CPU topology is detected through Windows CPU Set APIs. The scheduler remains the default; an advanced opt-in can constrain Intel hybrid sessions to the highest performance efficiency class.
- Selected applications can be stopped for the session. Restart support is shown before selection and simulator launchers/VR runtimes are protected.
- Standard Automatic mode selects safely restartable high- and medium-impact candidates. Aggressive Automatic mode includes all safely restartable candidates. Both restore the recorded state after the simulator exits.
- A VR runtime is closed after the session only when the optimizer started it; a runtime that was already running is left running.

No program can guarantee recovery after disk corruption or an operating-system failure. If restoration is incomplete, the journal is retained and the application reports the failed operation instead of claiming success.

## Supported launch targets

- Microsoft Flight Simulator 2024 — Steam and Microsoft Store
- Microsoft Flight Simulator 2020 — Steam and Microsoft Store
- DCS World — Steam and standalone
- X-Plane 12 — Steam and standalone
- IL-2 Sturmovik: Battle of Stalingrad — Steam
- Korea. IL-2 Series — standalone launcher

The application scans Steam library manifests, Microsoft Store packages, DCS registry installation paths, ready drive roots for a standalone X-Plane 12 installation, and Windows uninstall records plus common installation roots for the standalone Korea. IL-2 Series launcher. Only detected simulators are offered in the launch selector.

Additional simulator configurations may be added in future releases.

## Optional session adjustments

- Temporary Ultimate Performance power plan
- Selectable `Normal`, `AboveNormal`, or `High` simulator priority
- Optional Intel hybrid performance-core CPU Sets; AMD and AMD X3D remain scheduler-managed
- Stop SysMain when it was previously running
- Stop Print Spooler when it was previously running
- Enable NVIDIA persistence mode only on GPUs where it was previously disabled
- Stop selected running applications for the session and restart them when an executable path can be captured
- Stop selected relevant services only when they were running, then restore them afterward

The scan presents an impact level and explanation for each candidate. Manual mode leaves all selections under user control. Automatic mode selects all applications with a reliable restart command and all stoppable services. Items without a reliable restart command remain available for manual selection; Windows download, simulator-launcher, VR-runtime, Xbox Gaming Services, and common flight-control services are protected.

Standard profile limits Automatic application selection to high- and medium-impact items and uses Ultimate Performance, High simulator priority, vendor-aware CPU Sets, NVIDIA persistence, and a DNS flush. It does not stop services. Aggressive profile includes low- and unknown-impact applications, enables service selection (including approved SysMain, Print Spooler, iCloud and updater candidates), and adds the advanced session tuning shown in the UI. Every profile toggle remains editable before launch.

**Administrator mode is required for Aggressive operation.** This includes stopping services such as SysMain, Print Spooler, iCloud, CCleaner and updater services. It also includes protected registry changes, timer and memory operations, network tuning, and reliable restoration. If the title bar does not show administrator access, close the app and restart it using **Run as administrator** before beginning an Aggressive session.

Persistent Aggressive changes are written to the recovery journal before they are applied and restored in reverse order after the simulator exits. The 0.5 ms timer request is released and simulator power-throttling state is restored. DNS flushing and standby-list clearing are one-time operations rather than persistent settings; their caches naturally repopulate. NVIDIA persistence is supported and restored, but the driver-profile “Prefer maximum performance” setting is not forced because reliable per-profile restoration requires a dedicated NVIDIA NVAPI integration.

The Custom Apps tab stores process names in `config.json`. Optional restart entries use `process=full executable path`; matching running processes appear as custom candidates on every scan.

Content Creator Mode can be enabled in Session options. In Automatic mode it keeps OBS, Streamlabs, Twitch Studio, Discord, Stream Deck, NVIDIA Broadcast, Voicemeeter, Elgato, XSplit, vMix, TikTok LIVE Studio, Meld Studio, NDI, Blackmagic, AJA, and matching helper services running. Manual mode remains fully user-controlled.

Service stopping is available only in Aggressive mode. Administrator access is requested only when starting a real session or recovering one.

Impact guidance is based primarily on the current [Microsoft Flight Simulator performance guidance](https://flightsimulator.zendesk.com/hc/en-us/articles/360016142680-How-to-improve-the-performance), [MSFS crash troubleshooting guidance](https://flightsimulator.zendesk.com/hc/en-us/articles/4406280399250-Basic-Troubleshooting-How-to-troubleshoot-crashing-CTDs-issues), and Microsoft's recommendation to [pause OneDrive synchronization](https://support.microsoft.com/en-us/onedrive/how-to-pause-and-resume-onedrive-sync) when it is consuming resources. A listed item is a candidate, not proof that it is harming a particular PC; confirm with measurements and change one item at a time.

## Data files

All runtime data is kept under `%LOCALAPPDATA%\SimVROptimizer`:

```text
config.json
active-session.json   # present only during an unfinished real session
pending-launch.json   # temporary UAC handoff; removed when the elevated session continues
optimizer.log
optimizer.log.1 ... optimizer.log.5   # rotated history
```

## Requirements

- Windows 10 or Windows 11, x64
- .NET 8 Desktop Runtime
- Administrator access for Aggressive optimization, service stopping, system-level tuning, and recovery

## Important notice

This tool changes system state during a simulator session. Save work before starting. Applications without a reliable restart command are not selected automatically. No software can guarantee recovery after disk corruption or an operating-system failure; if restoration is incomplete, the recovery journal is retained and the application reports the failure.

## Build and test

```powershell
dotnet build .\SimVROptimizer.App\SimVROptimizer.App.csproj --configuration Release
dotnet run --project .\SimVROptimizer.Tests\SimVROptimizer.Tests.csproj --configuration Release
```

The test runner has no third-party test framework dependency. It covers output parsing, automatic selection, pending-session handoff, internal no-change isolation, transactional restoration, and corrupt-journal retention.

## License

Licensed under the [MIT License](LICENSE).

## Release notes — 1.9.0

- Added Korea. IL-2 Series as a separate standalone simulator configuration.
- Added flexible Korea launcher detection through Windows uninstall records and bounded common installation roots, including the current `Il2Series` layout.
- Fixed application and service checkbox selections being visually lost when virtualized table rows were scrolled out of view and reused.
- Persisted explicit stop/do-not-stop preferences in `config.json` across scrolling, rescans, workflow or profile changes, application restarts, and administrator handoff.
- Preserved user-adjusted application selections when starting an Automatic workflow.
- Protected MSFS AutoFPS process variants from manual and automatic stopping so simulator autostart remains available.
- Added launcher-path, simulator-configuration, selection-state, and saved-preference validation, bringing the automated test suite to 22 tests.

## Release notes — 1.8.1

- Fixed Pimax Play launch detection for the current `PimaxClient\pimaxui\PimaxClient.exe` installation layout, with legacy locations retained as fallbacks.
- Fixed bright Windows selection colours in application and service tables so selected rows remain readable in the aviation dark theme.
- Protected Microsoft Defender and Windows security services from session stopping.
- Made an individual service access-denied response non-fatal so other optimizations and the simulator launch can continue safely.
- Enabled the Windows token privilege required for aggressive standby-memory clearing.
- Protected Xbox/Store launch, GameInput, Pimax, NVIDIA, Tobii, Navigraph, MOZA, motion-rig, and VPN runtime components from Aggressive automatic stopping.
- Prevented direct executable relaunches of packaged WindowsApps/SystemApps components, avoiding missing packaged-runtime DLL errors during restoration.

## Release notes — 1.8.0

- Added Standard and Aggressive optimization profiles with granular controls.
- Reserved all service stopping for Aggressive mode and emphasized its administrator requirement.
- Added reversible Game Bar/Game DVR, fullscreen, timer-resolution, power-throttling, network, and multimedia tuning.
- Added optional standby-memory clearing and DNS cache flushing.
- Fixed running-service detection on Windows and expanded iCloud, Google updater, CCleaner, SysMain, and Print Spooler coverage.
- Added `-FastLaunch` for Microsoft Flight Simulator 2024 on Steam and Microsoft Store.
- Expanded simulator, VR-runtime, five-stage pipeline, log-rotation, and custom process-list support.
- Expanded automated validation to 16 passing tests.

## Release notes — 1.7.0

- Added a multi-resolution aviation application icon to the Windows executable and window title bar.
- Added Standard and Aggressive profiles while retaining granular power, priority, CPU Set, NVIDIA, application, and service controls.
- Added optional Virtual Desktop, Pimax Play, and SteamVR startup with session-aware shutdown only when launched by the optimizer.
- Added a live five-stage progress pipeline from preparation through restoration.
- Added 2 MB log rotation with five retained history files.
- Added persistent custom process kill and executable restart rules.
- Added IL-2 Sturmovik: Battle of Stalingrad on Steam as the ninth detected simulator configuration.

## Release notes — 1.6.1

- Removed Dry Run from the production interface so Start Flight Session always runs the real optimization workflow.
- Real sessions continue to require confirmation and administrator approval and always create a recovery journal before changing system state.

## Release notes — 1.6.0

- Rebuilt the interface as a dark aviation-instrument flight deck with matte avionics panels, amber controls, cyan readouts, annunciator lamps, technical labels, and squared instrument bezels.
- Added custom dark checkbox and selector templates to preserve text contrast and eliminate bright default control surfaces.
- Added live green, amber, and cyan status-lamp states for ready, scanning/restoring, preview, and active-session conditions.

## Release notes â€” 1.4.0

- Added Automatic and Manual session workflows.
- Automatic mode safely preselects all restartable applications and stoppable services, applies configured CPU settings, launches the detected simulator, and restores state after the exact simulator process exits.
- Added confirmation before automatic closure and an elevated pending-session handoff that continues automatically after UAC approval.
- Added protections for Steam, VR/OpenXR runtimes, Xbox Gaming Services, SimConnect/FSUIPC, and common flight-control/tracking services.
- MSFS is preferred by default when more than one supported simulator is detected.

## Release notes â€” 1.5.0

- Added persistent Content Creator Mode to protect streaming, recording, audio-routing, communication, and capture-device tools during Automatic sessions.
- Added live creator-protection status and confirmation counts before applications or services are stopped.
- Content Creator Mode is preserved through administrator/UAC session continuation.

## Release notes â€” 1.4.1

- Added explicit iCloud Drive, iCloud Photos, Apple Mobile Device, Bonjour, iPod, and Apple synchronization-process detection.
- Added Windows telemetry, Office Click-to-Run, Dropbox updater, EA, Epic, and GOG background-service profiles.
- Apple storage drivers, Windows Update, networking, audio, security, MSFS/Xbox, VR, and flight-control services remain untouched.

## Release notes — 1.3.0

- Added native CPU vendor/model and heterogeneous topology detection.
- Added selectable process priority and advanced Intel performance-core CPU Sets using Windows CPU Set IDs.
- Added explicit AMD X3D detection with scheduler-managed, cache/CCD-safe behavior.
- Added session logging and cancellation restoration for original priority and CPU Set assignments.

## Release notes — 1.2.2

- Made elevated or packaged applications selectable even when Windows withholds their executable path from the initial scan.
- Added CCleaner install-path fallback and Phone Link packaged-app restart handling.

## Release notes — 1.2.1

- Fixed blank application labels by ignoring empty executable descriptions and falling back to the visible window title or process name.

## Release notes — 1.2.0

- Fixed the detected-simulator selector to render simulator names in the closed field and dropdown.
- Expanded application discovery to known utilities plus other visible and third-party processes.
- Added Google updater/crash components, CCleaner, iCloud, Teams, Zoom, Spotify, browser updaters, Adobe services, and dynamically named Google/CCleaner services.
- Protected simulator launchers and VR runtime processes from session stopping.

## Release notes — 1.1.1

- Replaced default light WPF control surfaces with explicit high-contrast dark styles for selectors, tabs, tables, text fields, and disabled buttons.

## Release notes — 1.1.0

- Added automatic installed-simulator detection and rescanning.
- Added live background-application and relevant-service inventories.
- Added impact levels, explanations, opt-in session stop controls, and protected-service handling.
- Added transactional process restart and arbitrary selected-service restoration.

## Release notes — 1.0.0

- Replaced administrator-level scripts with a compiled WPF application and testable core library.
- Added atomic configuration and recovery-journal persistence.
- Added exact new-process detection and configurable launch timeout.
- Removed automatic app termination and speculative CPU-affinity behavior.
- Made documentation match the implemented feature set.
