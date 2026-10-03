# VR Auto-Optimizer

VR Auto-Optimizer is a .NET 8 WPF application for launching selected Windows flight simulators with temporary, recoverable system adjustments.

It scans the local PC, explains the likely MSFS impact of running applications and services, applies selected performance settings, launches the simulator, monitors the exact new simulator process—including DCS launcher process handoff—and restores recorded system state when the flight ends.

> [!IMPORTANT]
> **Run VR Auto-Optimizer as Administrator when using the Aggressive profile.** Aggressive features require elevated Windows permissions, including stopping and restarting services, changing protected registry values, and restoring those settings afterward. Right-click `SimVROptimizer.exe` and select **Run as administrator**, then approve the Windows User Account Control prompt. Without administrator access, service stopping and other Aggressive operations may fail or be skipped.

## Features

- Automatic detection of ten MSFS, DCS World, X-Plane, and IL-2 configurations
- Standard and Aggressive optimization profiles with editable granular controls
- Named user profiles save the simulator, workflow, VR runtime, optimization toggles, application/service checkboxes, and custom app lists, with import/export, duplicate/rename, optional simulator/aircraft/headset/monitor associations, and exact saved-versus-current differences
- Current application and service choices remain available across restarts; saved profiles change only through the explicit **Save Changes**, **Load** and **Revert** controls
- Protected-aware **Invert Selection** control plus short measured per-application CPU and memory readings alongside curated impact guidance
- Manual checkbox control or an Automatic session workflow
- Pre-flight safety checklist for access level, simulator target, VR runtime, recovery state, protected components, and stop selections
- Configurable Virtual Desktop, Pimax Play, SteamVR, or no-runtime launching
- Graceful SteamVR shutdown waits for Bluetooth base-station standby instead of force-closing the runtime
- Optional `-FastLaunch` startup for Microsoft Flight Simulator 2024 on both Steam and Microsoft Store
- Optional OpenXR Turbo frame-pacing layer, bundled with VR Auto-Optimizer and transactionally registered only for the flight session; OpenXR Toolkit is not required
- Live five-stage Prepare, Optimize, VR Runtime, Simulator, and Restore pipeline
- Live performance dashboard with simulator FPS and average FPS, GPU-Z GPU Load/VRAM usage percentage, system/simulator CPU, compact processor-load summaries, MainThread timing, simulator memory, spike detection, stutter detection, and optional CSV logging
- One-click pause/resume control for frame-time stutter and CPU-spike recording, so simulator loading does not inflate flight counters or session history while all other live metrics continue updating
- One-click privacy-scrubbed support package containing configuration, detected workloads, CPU/GPU/OpenXR details, driver versions, restoration results, performance history, logs, and recent telemetry
- Safer workload controls with service dependency/dependent visibility, selection and pre-flight dependency warnings, application restart-command warnings, and an explicit Test Restart action
- Automatic flight-session history with sortable columns, multi-select JSON export, confirmed record deletion, latest-versus-previous comparisons and average-FPS trend graphs, including MainThread time, stutters, CPU spikes, simulator version, GPU driver and profile-change markers
- Movable MSFS 2024 in-simulator VR toolbar dashboard with live FPS, GPU-Z GPU Load/VRAM usage percentage, simulator CPU/memory, MainThread timing, graphs, AMD CCD0/CCD1 load summaries, and spike/stutter counters over a loopback-only read-only connection
- Persistent custom process kill rules; applications stopped for a flight remain closed during restoration
- Profile-aware companion-app preloading for tools such as Active Sky, SayIntentions, REX Core Atmos and GPU-Z, with before-start, after-process-start and SimConnect-confirmed Ready-to-Fly timing, per-app delays, optional administrator launch, minimize-after-launch, duplicate prevention, and tracked post-flight cleanup
- In-app custom-list instructions and non-saving examples for process names and optional restart paths
- Automatic service and system-setting restoration through a transaction journal; each selected application can be set to **Leave Closed** or **Restart**, while OneDrive is always restored
- After a completed flight, selecting **Close Report** closes the restoration report and VR Auto-Optimizer after final cleanup; manually opened reports remain report-only
- Verified per-item restoration report for applications, services, power plans, NVIDIA state, and registry values
- Desktop “Restore Last Session” shortcut plus automatic recovery launch after the next Windows sign-in when a journal is active
- CPU vendor/model and topology detection with AMD X3D-safe scheduling, Windows Balanced power handling, and Game Bar protection
- Selectable simulator priority and optional Intel hybrid performance-core CPU Sets
- CPU-aware power handling: Windows Balanced for AMD X3D, temporary Ultimate Performance for other supported CPUs, plus NVIDIA persistence control
- Post-flight Xbox online-stack preservation for MSFS: Xbox, Game Bar, Gaming Services, authentication, game-save and networking components are not terminated
- Standard profile includes DNS flush, High process priority, and vendor-aware CPU Sets; service stopping is reserved for Aggressive mode
- Aggressive profile adds reversible Game Bar/Game DVR, fullscreen-optimization, timer-resolution, process power-throttling, standby-memory clearing, and approved service control
- Optional one-time standby-memory clearing in Aggressive mode
- Clear **Recommend**, **Keep Running**, and **Protected** application and service guidance; automatic selection uses only verified recommended items
- Optional online guidance catalogue with local executable identity checks using file metadata, publisher, product, Authenticode signature, and SHA-256
- Verified in-app updates that download only official GitHub installer assets accompanied by a matching SHA-256 checksum; releases without both files fall back to the release page
- Simple freeware release pipeline that builds the Windows installer and publishes a matching SHA-256 checksum without requiring a paid signing certificate
- Windows installer with Start Menu/optional desktop shortcuts and a clean uninstaller that removes recovery shortcuts and the recognized MSFS toolbar package, with an optional prompt to remove saved data
- Dedicated read-only Display / DLSS tab showing saved MSFS desktop/VR TAA or DLSS mode, the active DLSS runtime version, NVIDIA App model presets, and installed NVIDIA or AMD GPU driver details
- Read-only display recommendations based on the latest matching monitored flight, including likely CPU/graphics balance, DLSS mode, render-scaling and stable frame-rate target guidance, with explicit timestamped `UserCfg.opt` backup
- Administrator-controlled NVIDIA DLSS information overlay switch on the Display / DLSS tab, using NVIDIA's documented global indicator value so the active DLSS version and preset information can be verified inside MSFS
- VR runtime diagnostics showing selected-runtime launcher availability, selected-versus-active alignment, the registered OpenXR runtime, detected headset and available launchers. Pimax identification uses passive manifest and device-log inspection so the active runtime is never loaded or disturbed; deeper OpenXR registration details remain available in support packages.
- MSFS online-services health checks for the official MSFS 2024 Live Weather, Live Traffic, Multiplayer and Online Services states, local network availability, Microsoft/Xbox DNS resolution and Xbox support-service readiness, plus a conservative DNS/service-start repair
- Protected Steam, Xbox/MSFS, VR/OpenXR, networking, security, and flight-control components
- Content Creator Mode for OBS, Streamlabs, Stream Deck, NVIDIA Broadcast, Voicemeeter, Elgato, and other capture tools
- Aviation instrument-themed dark interface with live session status and rotating logs
- Interrupted-session recovery on the next application start

## Install

1. Download `VR-Auto-Optimizer-<version>-Setup.exe` and its `.sha256` file from [Releases](https://github.com/macbrowndog/Flight-Sim-VR-Auto-Optimizer/releases/latest).
2. Run the installer. It installs the self-contained Windows x64 application, Start Menu shortcut and optional desktop shortcut; a separate .NET runtime is not required.
3. For Standard use, run VR Auto-Optimizer normally. For Aggressive use, approve the Windows administrator prompt.
4. Select a detected simulator and session workflow, then choose **Start Flight Session**. Close the simulator normally to trigger restoration.

The installer is unsigned freeware, so Windows may display an **Unknown publisher** or SmartScreen warning. Confirm that the installer came from the official GitHub release and that its SHA-256 matches the published `.sha256` file. The in-app updater performs this checksum validation automatically.

The uninstaller removes installed program files, shortcuts, recovery shortcuts and any recognized VR Dashboard package. It asks separately before deleting profiles, logs, performance history and recovery data.

### Release build

`scripts/Build-Release.ps1` creates the self-contained executable, builds the Inno Setup installer and writes the matching `.sha256` file. No certificate or signing secrets are required.

### MSFS 2024 VR toolbar dashboard

The Windows application includes a separate dashboard-only toolbar panel for Microsoft Flight Simulator 2024. It can be moved, resized, opened, or pinned inside the headset while the Windows application continues to control the flight session on the desktop. Logical-processor readings are condensed into an overall average/hotspot summary; supported multi-CCD AMD processors display separate CCD0 and CCD1 average/peak readings.

1. Close MSFS 2024 before installing or updating the panel.
2. Open **Advanced** in VR Auto-Optimizer and select **Install Panel** under **MSFS 2024 / VR Toolbar Dashboard**.
3. Restart MSFS 2024, begin a flight, and open **VR Optimizer** from the simulator toolbar.
4. Keep VR Auto-Optimizer running. Live data begins when the simulator process and dashboard monitor start.

For the toolbar's **GPU Load** and **GPU Memory** cards, keep GPU-Z running during the flight. GPU Memory is displayed as a percentage of the card's total VRAM; hover over it to see the live used/total MB values. VR Auto-Optimizer reads GPU-Z's local read-only `GPUZShMem` sensor mapping; it does not launch, configure or control GPU-Z. If GPU-Z is closed, updating its sensor table, stale, or missing a required value, the affected toolbar card displays a dash and its hover text explains the source state. The separate **Sim Memory** card continues to show the simulator process working set.

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
- Starting a session is blocked when the selected simulator is already running. DCS sessions allow a bounded handoff from the initially detected `DCS.exe` or `DCS_mt.exe` launcher process to a newly created replacement simulator process before restoration.
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

The scan presents an impact level and explanation for each candidate. **CPU Now** is measured locally over a short interval and **Mem MB** shows the current working set; these readings complement rather than replace the curated Impact guidance. Manual mode retains saved choices, while Automatic mode selects verified **Recommend** applications and approved Aggressive services. Items without reliable classification are labelled **Keep Running** and remain available for deliberate manual selection; Windows download, simulator-launcher, VR-runtime, Xbox Gaming Services, and common flight-control services are protected. On AMD X3D systems, detected Process Lasso power controllers are locked selected so they cannot override Windows Balanced.

The optional **Online Guidance** switch downloads the complete curated application and service catalogue; it does not upload process names, service names, executable paths, hashes, or PC details. When enabled, local executable metadata, signatures, and SHA-256 hashes are inspected on the PC and matched against catalogue names, publishers, products, and hashes. The Identity column reports **Verified**, **Identified**, **Likely**, or **Unidentified** with full details on hover. Local protection rules always take priority, and unmatched items remain **Keep Running**.

Standard profile limits Automatic application selection to high- and medium-impact items and uses CPU-aware power handling, High simulator priority, vendor-aware CPU Sets, NVIDIA persistence, and a DNS flush. AMD X3D systems use Windows Balanced; other supported processors use temporary Ultimate Performance. It does not stop services. Aggressive profile includes low- and unknown-impact applications, enables service selection (including approved SysMain, Print Spooler, iCloud and updater candidates), and adds the advanced session tuning shown in the UI. Every profile toggle remains editable before launch.

**Administrator mode is required for Aggressive operation.** This includes stopping services such as SysMain, Print Spooler, iCloud, CCleaner and updater services. It also includes protected registry changes, timer and memory operations, network tuning, and reliable restoration. If the title bar does not show administrator access, close the app and restart it using **Run as administrator** before beginning an Aggressive session.

The Dashboard's CPU summary, simulator thread, and memory readings work for standard users. AMD logical-processor data is grouped by Windows CPU topology into CCD summaries; other processors show an overall average and hottest logical processor. MSFS 2024 FPS and frame-time readings use its installed SimConnect runtime and visual-frame data; other supported simulators can fall back to the included Intel PresentMon component. The Dashboard Monitoring option enables the telemetry session, but FPS remains dependent on one of those frame sources. When frame access is unavailable, the dashboard now identifies monitoring as active, explains why FPS is unavailable, and continues collecting CPU, MainThread, and memory metrics. Simulator FPS is an application presentation rate and may differ from headset-delivered FPS when a VR runtime uses reprojection.

Persistent Aggressive changes are written to the recovery journal before they are applied and restored in reverse order after the simulator exits. The 0.5 ms timer request is released and simulator power-throttling state is restored. DNS flushing and standby-list clearing are one-time operations rather than persistent settings; their caches naturally repopulate. NVIDIA persistence is supported and restored, but the driver-profile “Prefer maximum performance” setting is not forced because reliable per-profile restoration requires a dedicated NVIDIA NVAPI integration.

The **Custom Apps** tab has two separate roles. **Companion App Preload** stores external executable paths, launch timing and a delay applied before each app starts. Companion changes are stored when **Save Changes** is selected on the **Flight Profile** tab, then restored whenever that named profile is loaded or reapplied at startup. An app can start **Before Simulator**, **After Simulator Starts**, or **Ready to Fly**. For MSFS, Ready to Fly waits for the SimConnect `FlightLoaded` event and then applies the configured launch delay; if readiness cannot be confirmed, the profile's bounded launch timeout is used as a fallback. Select **Admin** when that companion must be started using Windows' administrator `runas` request. Select **Minimize** to wait up to 20 seconds for the newly launched app's normal window and minimize it; an app without a normal window is left untouched and reported in the Flight Log. Apps that are already running are never duplicated or minimized. For cleanup, choose **Leave Running** or **Close On Session End**; automatic cleanup applies only to the process instance started by VR Auto-Optimizer and first requests a normal window close before terminating that tracked process if necessary. These rules are included in named profiles and survive the administrator handoff.

The lower custom-app list stores process names in `config.json`; matching running processes appear as custom candidates on every scan so they can be closed during the flight. This close list is independent of companion preloading.

### Saved user profiles

The Flight Profile tab can store multiple named setups. Configure the simulator, workflow, optimization options, application/service checkboxes, application **After Flight** actions, VR runtime, online-guidance preference, and custom app lists; type a name in **Saved User Profile**, then select **Save Changes**. Profiles can carry optional aircraft, VR-headset and monitor-configuration associations, and the simulator association follows the selected simulator. The difference table lists every current value that differs from the selected saved profile. Profiles can be duplicated, renamed, exported as portable versioned JSON files, and imported on another PC. Choose a profile and select **Load** to restore the complete setup and rescan the PC. **Revert** discards unsaved changes, and deleting a profile does not alter the current on-screen settings.

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
performance-history.json             # latest 100 monitored flight summaries with version-change metadata
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

## Credits & Acknowledgments

This project is based on the original VR Optimizer application developed by shark. Huge thanks to shark for open-sourcing the initial codebase and making this work possible.

## Release notes — 2.4.2

- Reworked the desktop and MSFS toolbar dashboards with live GPU-Z GPU Load and VRAM-percentage readings, while retaining simulator memory and CPU/MainThread telemetry.
- Added sortable session history, multi-record JSON export, confirmed deletion, simulator-version detection fixes, and updated performance comparisons without the retired 1% Low metric.
- Added profile-owned **Minimize After Launch** handling for companion apps.
- Improved Pimax diagnostics using passive manifest/device-log inspection and simplified the visible runtime panel so diagnostics never load or disturb the active OpenXR runtime.
- Corrected DLSS version formatting to four dotted components, such as `DLSS v310.9.1.0`; the version is displayed only while MSFS has actually loaded the library.
- Expanded automated validation to 62 passing tests.

## Release notes — 2.4.1

- Added profile-owned companion-app preloading for Active Sky, SayIntentions, REX Core Atmos and other external applications.
- Added **Before Simulator**, **After Simulator Starts** and SimConnect-confirmed **Ready to Fly** launch stages, with per-app launch delays and a bounded fallback.
- Added direct executable selection, optional administrator launch, duplicate prevention, and **Leave Running** or **Close on Session End** cleanup for processes started by the optimizer.
- Companion apps are saved through **Flight Profile → Save Changes** and restored with the active named profile at startup.
- Added a dashboard switch to pause/resume frame-stutter and CPU-spike recording during loading without stopping other telemetry.
- Simplified the Custom Apps interface by removing command-line Arguments and fixed its launch and cleanup dropdown controls.
- Expanded automated validation to 59 passing tests.

## Release notes — 2.4.0

- Added flight-session history with performance trend graphs, latest-versus-previous comparisons, and automatic simulator, GPU-driver and profile-change markers.
- Added read-only CPU/GPU balance recommendations for DLSS mode, render scaling and stable frame-rate targets, with timestamped `UserCfg.opt` backup before any user-approved graphics change.
- Added VR runtime diagnostics and an MSFS online-services health panel covering official service state, local connectivity, Microsoft/Xbox DNS and supporting services.
- Expanded profile management with import, export, duplicate, rename, simulator/aircraft/headset/monitor associations, and exact saved-versus-current differences.
- Added a one-click privacy-scrubbed support package containing logs, configuration, detected hardware, driver versions, restoration results, history and telemetry.
- Added safer application and service controls with dependency visibility, stop warnings, restart-command checks and an application **Test Restart** action.
- Added a Windows installer and clean uninstaller plus checksum-verified in-app downloads from official GitHub releases. Releases remain unsigned freeware and require no paid certificate.
- Expanded automated validation to 58 passing tests.

## Release notes — 2.3.0

- Replaced the separate display-settings dialog with a dedicated **Display / DLSS** tab and one-click refresh.
- Added saved 2D and VR TAA/DLSS modes, NVIDIA App Frame Generation, Super Resolution and Ray Reconstruction model presets, and accurate active NGX/DLSS runtime-version reporting.
- Added an administrator-controlled NVIDIA in-simulator DLSS information overlay switch for checking the live version, rendering API and preset inside MSFS.
- Added installed NVIDIA and AMD GPU identification, driver version and driver date to the Display / DLSS panel.
- Preserved the complete Xbox, Game Bar, Gaming Services, authentication, game-save and networking stack after MSFS exits to prevent the optimizer from disrupting live weather on the next launch.
- Improved protected-process handling and expanded automated coverage for DLSS runtime metadata, driver-version formatting, overlay values and Xbox online-stack preservation; all 50 tests pass.

## Release notes — 2.2.1

- Added a manual **Check Update** control that compares the installed version with the latest public GitHub release and only opens GitHub after user approval.
- Added a read-only **MSFS Display & NVIDIA DLSS Settings** window for saved desktop/VR rendering modes, NVIDIA App model-preset overrides, and the loaded DLSS library version.
- Improved dashboard source handling by checking loaded SimConnect libraries before using the safe PresentMon fallback.
- Clarified degraded telemetry states so the dashboard explicitly shows when CPU, MainThread, and memory monitoring remain active while FPS is unavailable.
- Removed forced post-flight Xbox/Game Bar process termination so the complete authentication and online-services stack remains available for the next MSFS launch.
- Expanded automated coverage for update checking, display-settings parsing, DLSS preset mapping, FPS status reporting, and Xbox cleanup safety.

## Release notes — 2.2.0

- Added opt-in online guidance for both running applications and services using a remotely maintained catalogue; the complete catalogue is downloaded and no local process names, service names, paths, hashes, or PC details are uploaded.
- Added local executable identity inspection using file metadata, publisher, product, version, Authenticode trust, and SHA-256, with **Verified**, **Identified**, **Likely**, and **Unidentified** confidence levels.
- Added an **Identity** column to the Applications and Services tabs with full identification details available on hover.
- Added confidence-based catalogue matching by exact hash, trusted publisher/product, product/name, and executable or service name; local protection rules always take priority over downloaded guidance.
- Replaced ambiguous **Optional** and **Unknown** display labels with the safer **Keep Running**, and changed unknown impact text to **No Known**. Automatic selection continues to use only recommended items.
- Sorted selected applications and services together at the top of their grids and retained the ordering as selections change.
- Improved unrecognized application and service explanations so they describe the safe user action instead of referring to an internal verified catalogue.
- Truncated long guidance and impact notes cleanly in the grids and added wrapped hover text showing the complete explanation.
- Saved the online-guidance preference in both the main configuration and named user profiles.
- Added a downloadable application and service guidance catalogue with publisher and product metadata, while retaining safe **Keep Running** behavior when the catalogue is unavailable or an item is unmatched.
- Expanded automated validation to 46 passing tests, including online matching, local-protection precedence, executable-path parsing, hashing, and trusted-signature verification.

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
- Preserved the complete Xbox/Game Bar and Gaming Services stack after MSFS exits so online features remain available for the next launch.
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
