# VR Auto-Optimizer

VR Auto-Optimizer is a .NET 8 WPF application for launching selected Windows flight simulators with temporary, recoverable system adjustments.

It scans the local PC, explains the likely MSFS impact of running applications and services, applies selected performance settings, launches the simulator, monitors the exact new simulator process, and restores recorded system state when the flight ends.

> [!IMPORTANT]
> **Run VR Auto-Optimizer as Administrator when using the Aggressive profile.** Aggressive features require elevated Windows permissions, including stopping and restarting services, changing protected registry values, and restoring those settings afterward. Right-click `SimVROptimizer.exe` and select **Run as administrator**, then approve the Windows User Account Control prompt. Without administrator access, service stopping and other Aggressive operations may fail or be skipped.

## Features

- Automatic detection of ten MSFS, DCS World, X-Plane, and IL-2 configurations
- Standard and Aggressive optimization profiles with editable granular controls
- Named user profiles save the simulator, workflow, VR runtime, optimization toggles, application/service checkboxes, and custom app lists
- Manual checkbox control or an Automatic session workflow
- Pre-flight safety checklist for access level, simulator target, VR runtime, recovery state, protected components, and stop selections
- Configurable Virtual Desktop, Pimax Play, SteamVR, or no-runtime launching
- Graceful SteamVR shutdown waits for Bluetooth base-station standby instead of force-closing the runtime
- Optional `-FastLaunch` startup for Microsoft Flight Simulator 2024 on both Steam and Microsoft Store
- Optional OpenXR Turbo frame-pacing layer, bundled with VR Auto-Optimizer and transactionally registered only for the flight session; OpenXR Toolkit is not required
- Live five-stage Prepare, Optimize, VR Runtime, Simulator, and Restore pipeline
- Live performance dashboard with simulator FPS, frame time, average and 1% low FPS, system/simulator CPU, compact processor-load summaries, MainThread timing, memory, spike detection, stutter detection, and optional CSV logging
- Movable MSFS 2024 in-simulator VR toolbar dashboard with the same live metrics, graphs, AMD CCD0/CCD1 load summaries, and spike/stutter counters over a loopback-only read-only connection
- Persistent custom process kill rules; applications stopped for a flight remain closed during restoration
- In-app custom-list instructions and non-saving examples for process names and optional restart paths
- Automatic service and system-setting restoration through a transaction journal; each selected application can be set to **Leave Closed** or **Restart**, while OneDrive is always restored
- After a completed flight, selecting **Close Report** closes the restoration report and VR Auto-Optimizer after final cleanup; manually opened reports remain report-only
- Verified per-item restoration report for applications, services, power plans, NVIDIA state, and registry values
- Desktop “Restore Last Session” shortcut plus automatic recovery launch after the next Windows sign-in when a journal is active
- CPU vendor/model and topology detection with AMD X3D-safe scheduling, Windows Balanced power handling, and Game Bar protection
- Selectable simulator priority and optional Intel hybrid performance-core CPU Sets
- CPU-aware power handling: Windows Balanced for AMD X3D, temporary Ultimate Performance for other supported CPUs, plus NVIDIA persistence control
- Automatic post-flight Xbox application and trigger-start service cleanup for MSFS, without changing service startup configuration
- Standard profile includes DNS flush, High process priority, and vendor-aware CPU Sets; service stopping is reserved for Aggressive mode
- Aggressive profile adds reversible Game Bar/Game DVR, fullscreen-optimization, timer-resolution, process power-throttling, standby-memory clearing, and approved service control
- Optional one-time standby-memory clearing in Aggressive mode
- Background application and service impact guidance
- Four-level application and service guidance: Recommended, Optional, Protected, and Unknown; automatic application selection uses only verified Recommended applications
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

### MSFS 2024 VR toolbar dashboard

The Windows application includes a separate dashboard-only toolbar panel for Microsoft Flight Simulator 2024. It can be moved, resized, opened, or pinned inside the headset while the Windows application continues to control the flight session on the desktop. Logical-processor readings are condensed into an overall average/hotspot summary; supported multi-CCD AMD processors display separate CCD0 and CCD1 average/peak readings.

1. Close MSFS 2024 before installing or updating the panel.
2. Open **Advanced** in VR Auto-Optimizer and select **Install Panel** under **MSFS 2024 / VR Toolbar Dashboard**.
3. Restart MSFS 2024, begin a flight, and open **VR Optimizer** from the simulator toolbar.
4. Keep VR Auto-Optimizer running. Live data begins when the simulator process and dashboard monitor start.

The installer detects the `InstalledPackagesPath` recorded by MSFS and installs the compiled package structure into its `Community` folder. **Remove** uninstalls only the recognized FlightDeckTools package. Restart MSFS after installing, updating, or removing it.

The panel connects only to `ws://127.0.0.1:48624/dashboard`. The bridge is bound to the local computer, sends telemetry in one direction, and exposes no controls for stopping applications, changing services, or applying optimizer settings. If the toolbar panel shows **Offline**, start VR Auto-Optimizer and confirm no other program is using port `48624`.

## Safety model

- Before optimization, one combined confirmation lists the safety findings and planned simulator, application, service, runtime, and tuning actions. It blocks missing simulator or VR launchers, unfinished recovery, protected-component selection, and profile/selection conflicts.
- Starting a session performs the selected real optimization after that confirmation and administrator approval.
- Real sessions write `%LOCALAPPDATA%\SimVROptimizer\active-session.json` before changing state.
- Every session uses a `finally` restoration path.
- An interrupted session is detected on the next start and must be restored before another session can begin.
- While a real session is active, a temporary Startup shortcut launches recovery after the next Windows sign-in. It is removed after verified restoration; the desktop recovery shortcut remains available for manual use.
- Restoration commands are followed by state verification. Failures are recorded in `%LOCALAPPDATA%\SimVROptimizer\last-restoration-report.json`, and the active journal is retained whenever verification fails.
- Services are restarted only when they were running before the optimizer stopped them.
- NVIDIA persistence is restored per GPU to its original value.
- AMD X3D systems retain or temporarily use Windows Balanced for cache-aware CCD scheduling. Other supported CPUs can use a temporary Ultimate Performance plan. Any changed plan is restored afterward.
- The optimizer tracks a newly created simulator PID. It does not attach to a matching process that was already running.
- CPU topology is detected through Windows CPU Set APIs, including processor groups on systems with more than 64 logical processors. Intel hybrid tuning uses only unparked, unreserved performance-class CPU Sets, preserves any existing simulator CPU policy, and verifies the assignment. AMD X3D, AMD, uniform-core Intel, and uncertain topologies remain safely managed by the Windows scheduler.
- Selected applications can be stopped for the session and remain closed after the simulator exits. Services and system settings are restored, while simulator launchers and VR runtimes remain protected.
- Standard Automatic mode selects safely restartable high- and medium-impact candidates. Aggressive Automatic mode includes all safely restartable candidates. Both restore the recorded state after the simulator exits.
- A VR runtime is closed after the session only when the optimizer started it; a runtime that was already running is left running. SteamVR receives its normal graceful shutdown command and up to 30 seconds to complete Bluetooth base-station standby. If it does not finish, it is left running rather than force-closed.

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

- CPU-aware power plan: Windows Balanced for AMD X3D; temporary Ultimate Performance for other supported CPUs
- Selectable `Normal`, `AboveNormal`, or `High` simulator priority
- Optional Intel hybrid performance-core CPU Sets; AMD and AMD X3D remain scheduler-managed
- Stop SysMain when it was previously running
- Stop Print Spooler when it was previously running
- Enable NVIDIA persistence mode only on GPUs where it was previously disabled
- Stop selected running applications for the session; choose **Leave Closed** or **Restart** for each restartable application, while OneDrive is always restored
- Stop selected relevant services only when they were running, then restore them afterward

The scan presents an impact level and explanation for each candidate. Manual mode retains saved choices, while Automatic mode selects verified Recommended applications and approved Aggressive services. Items without reliable classification remain available for manual selection; Windows download, simulator-launcher, VR-runtime, Xbox Gaming Services, and common flight-control services are protected. On AMD X3D systems, detected Process Lasso power controllers are locked selected so they cannot override Windows Balanced.

Standard profile limits Automatic application selection to high- and medium-impact items and uses CPU-aware power handling, High simulator priority, vendor-aware CPU Sets, NVIDIA persistence, and a DNS flush. AMD X3D systems use Windows Balanced; other supported processors use temporary Ultimate Performance. It does not stop services. Aggressive profile includes low- and unknown-impact applications, enables service selection (including approved SysMain, Print Spooler, iCloud and updater candidates), and adds the advanced session tuning shown in the UI. Every profile toggle remains editable before launch.

**Administrator mode is required for Aggressive operation.** This includes stopping services such as SysMain, Print Spooler, iCloud, CCleaner and updater services. It also includes protected registry changes, timer and memory operations, network tuning, and reliable restoration. If the title bar does not show administrator access, close the app and restart it using **Run as administrator** before beginning an Aggressive session.

The Dashboard's CPU summary, simulator thread, and memory readings work for standard users. AMD logical-processor data is grouped by Windows CPU topology into CCD summaries; other processors show an overall average and hottest logical processor. MSFS 2024 FPS and frame-time readings use its installed SimConnect runtime and visual-frame data; other supported simulators can fall back to the included Intel PresentMon component. When frame access is unavailable, the dashboard reports this explicitly and continues collecting the remaining metrics. Simulator FPS is an application presentation rate and may differ from headset-delivered FPS when a VR runtime uses reprojection.

Persistent Aggressive changes are written to the recovery journal before they are applied and restored in reverse order after the simulator exits. The 0.5 ms timer request is released and simulator power-throttling state is restored. DNS flushing and standby-list clearing are one-time operations rather than persistent settings; their caches naturally repopulate. NVIDIA persistence is supported and restored, but the driver-profile “Prefer maximum performance” setting is not forced because reliable per-profile restoration requires a dedicated NVIDIA NVAPI integration.

The Custom Apps tab stores process names in `config.json`. Optional restart entries use `process=full executable path`; matching running processes appear as custom candidates on every scan.

### Saved user profiles

The Flight Profile tab can store multiple named setups. Configure the simulator, workflow, optimization options, application/service checkboxes, application **After Flight** actions, VR runtime, and custom app lists; type a name in **Saved User Profile**, then select **Save Changes**. Choose that name and select **Load** on a later run to restore the complete setup and rescan the PC. The profile status clearly reports **Saved** or **Modified**, and **Revert** discards unsaved changes. Saving with an existing name updates that profile. Deleting a profile does not alter the current on-screen settings. The most recently loaded or saved profile name and its active settings are retained when the application is restarted.

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
Telemetry\telemetry-*.csv            # optional per-session performance data
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

## Release notes — 2.1.0

- Added a per-application **After Flight** choice so selected applications can either **Restart** or remain **Left Closed**; OneDrive continues to be restored automatically.
- Added reliable restart handling for conventional desktop applications and packaged Windows applications such as Phone Link and Cross Device Experience.
- Added fixed **Restore** status to the Services tab, making it clear that stopped services are always restored after the flight for system safety.
- Improved named profile editing with **Saved**, **Modified**, and **New** states plus explicit **Save Changes** and **Revert** controls; application and service choices remain persistent through rescans and administrator handoff.
- Combined the separate launch warnings into one concise pre-flight confirmation showing the applications, services, runtime actions, and system changes planned for the session.
- Changed **Close Report** after a successfully completed flight to close VR Auto-Optimizer after final cleanup; manually opened reports and incomplete recovery reports leave the optimizer running.
- Fixed the After Flight dropdown display and conversion error in the Applications grid.
- Fixed cleanly self-stopping updater services, including Microsoft Edge and Google/Mozilla updater services, being incorrectly reported as failed and retaining the recovery journal.
- Strengthened restart verification, recovery-journal recording, and regression coverage for per-application choices, profiles, packaged-app launching, transient services, and post-flight shutdown.
- Expanded automated validation to 45 passing tests.

## Release notes — 2.0.0

- Added the live Windows performance dashboard and movable MSFS 2024 **VR Optimizer** toolbar panel with FPS, frame-time, MainThread, CPU, memory, processor-group/AMD CCD summaries, graphs, independent spike and stutter counters, and reset controls.
- Added MSFS SimConnect visual-frame telemetry, Intel PresentMon fallback, optional CSV capture, and a loopback-only telemetry bridge for the in-simulator panel.
- Added the bundled OpenXR Turbo frame-pacing layer with temporary session registration, automatic removal on exit, profile persistence, explanatory tooltips, and toolbar ON/OFF status.
- Added named user profiles that persist simulator, workflow, VR runtime, optimization options, application/service selections, and custom process lists across rescans, restarts, and administrator handoff.
- Added Recommended, Optional, Protected, and Unknown classification with colour guidance for applications and services; improved virtualization-safe checkbox persistence.
- Strengthened recovery with a durable transaction journal, verified per-item restoration report, automatic interrupted-session recovery, log rotation, and recovery shortcuts.
- Changed post-flight application handling so selected applications remain closed, while OneDrive is explicitly and safely restored to the normal desktop session.
- Added AMD X3D-aware Windows Balanced handling, post-launch power-plan verification, Process Lasso controller shutdown before plan selection, scheduler-safe CPU behavior, and Xbox Game Bar protection.
- Added automatic post-flight Xbox process/service cleanup without changing startup configuration, reducing stale Xbox state before the next MSFS launch.
- Improved Intel hybrid CPU Set selection and verification, processor-group support, process priority, power-throttling restoration, and AMD CCD-aware monitoring.
- Added graceful SteamVR shutdown so Bluetooth base stations can enter standby, plus safer Pimax, Virtual Desktop, OpenXR, flight-control, security, and simulator-companion protection.
- Added MSFS 2024 `-FastLaunch`, ten simulator configurations including standalone Korea. IL-2 Series, two optimization profiles, granular controls, five-stage progress, Content Creator Mode, and configurable VR runtime launching.
- Added a pre-flight safety review, administrator guidance, post-restoration restart requirement, aviation instrument UI refinements, application header copyright, and automatic Version 2.0.0 display.
- Expanded automated regression validation to 42 passing tests.

## Release notes — 1.10.1

- Fixed saved user profiles so the selected profile loads reliably, its application and service choices are not overwritten by UI events, and the complete profile catalogue survives administrator/UAC continuation.
- Added clear profile-load confirmation with simulator, workflow, optimization mode, and restored selection counts; serialized configuration writes to prevent competing saves.
- Replaced the simulator thread-count tile with **MAIN THREAD** frame time in milliseconds on the Windows dashboard, MSFS toolbar panel, telemetry bridge, and CSV output.
- Split frame-time stutters and CPU spike samples into independent indicators with a separate reset control for each on both dashboards.
- Refined the MSFS VR toolbar layout and reduced processor-summary and counter typography for clearer viewing in VR.
- Updated the bundled MSFS toolbar package to 1.10.1 and expanded automated validation to 37 passing tests.

## Release notes — 1.10.0

- Added a live desktop performance dashboard for simulator FPS, frame time, average FPS, 1% low FPS, system and simulator CPU, thread count, memory, stutter detection, CPU-spike detection, and optional CSV logging.
- Added MSFS 2024 visual-frame telemetry through SimConnect with an included Intel PresentMon fallback for other supported simulators and unavailable frame sources.
- Added the movable and resizable **VR Optimizer** MSFS 2024 toolbar panel, including live graphs, CPU model, compact AMD CCD0/CCD1 summaries, synchronized resettable event counters, and automatic VR panel height fitting.
- Added one-click installation, updating, and removal of the compiled toolbar package in the detected MSFS 2024 Community folder using a read-only loopback telemetry bridge.
- Added named user profiles that preserve simulator, workflow, VR runtime, optimization settings, application/service selections, and custom app rules.
- Added clearer custom-app examples and made selected applications remain closed after the flight instead of reopening visible desktop windows during restoration.
- Added Recommended, Optional, Protected, and Unknown workload classifications with matching colour guidance for applications and services.
- Added a pre-flight safety review that blocks missing simulators/runtimes, protected selections, unfinished recovery, and incompatible profile choices before optimization starts.
- Strengthened interrupted-session recovery with verified restoration outcomes, a detailed restoration report, retained journals after failures, and recovery shortcuts for desktop use and the next Windows sign-in.
- Improved CPU handling with processor-group awareness, safer Intel performance CPU Set selection and verification, existing-policy preservation, and scheduler-managed AMD/X3D cache safety.
- Added compact processor-load summaries: AMD systems show CCD averages and hotspots; other CPUs show an overall average and hottest logical processor.
- Added graceful SteamVR shutdown with up to 30 seconds for Bluetooth base stations to enter standby; SteamVR is left running instead of force-closed if shutdown does not complete.
- Added VR-runtime availability checks before launch and retained the rule that runtimes already running before a session are not closed afterward.
- Changed the Windows application to open in a normal centred window instead of full-screen mode.
- Expanded automated validation from 22 to 36 passing tests, covering classification, profiles, safety checks, restoration verification, CPU topology, telemetry, toolbar installation/update, performance sampling, and VR-runtime shutdown policy.

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
- Added reversible Game Bar/Game DVR, fullscreen, timer-resolution, and power-throttling tuning.
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
