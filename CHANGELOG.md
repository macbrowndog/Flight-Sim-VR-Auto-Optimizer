# Changelog

## Unreleased

- Reduced in-flight monitoring overhead by removing the unused live 1% low calculation, keeping only a short frame-time window for stutter detection, batching optional CSV flushes, and skipping desktop graph rebuilds while the dashboard is hidden or the app is minimized.
- Preserved current application and service checkbox choices across restarts without silently reapplying an older active-profile snapshot; **Load**, **Save Changes** and **Revert** retain their explicit profile behavior.
- Added an **Invert Selection** action for applications that reverses selectable choices while leaving protected and required workloads unchanged.
- Added a measured **CPU Now** column using a short local per-process sample; retained the curated **Impact** guidance and existing memory reading because GPU-Z reports total GPU load rather than reliable per-application GPU usage.
- Blocked session startup when the selected simulator is already running, preventing partial optimization and duplicate launches.
- Added bounded DCS launcher-to-simulator PID handoff for `DCS.exe` and `DCS_mt.exe`, including CPU-tuning and dashboard-monitor transfer before restoration.
- Added a dedicated top-right **×** button to the MSFS toolbar dashboard. It closes only the in-simulator panel and leaves the optimizer session running on the Windows desktop.
- Expanded automated coverage to 65 passing tests.

## 2.4.2 — 2026-10-03

- Normalized DLSS file-version formatting to full-stop separators and preserved all four components, for example `DLSS v310.9.1.0` instead of `DLSS v310,9,1,0`.
- Expanded Pimax diagnostics with passive manifest and device-log inspection that identifies the headset without loading or disturbing the active OpenXR runtime.
- Simplified the visible VR runtime panel to show launcher status, runtime alignment, active runtime, headset and available launchers; removed the OpenXR API, manifest, running-process, display-target, motion-reprojection, implicit-layer and environment-override rows.
- Added sortable performance-history columns with typed date and numeric ordering.
- Added multi-select comparison export to portable JSON files and confirmed deletion of unwanted saved history records.
- Aligned session history with the current dashboard by removing the obsolete 1% Low column, comparison label and green trend line.
- Fixed Microsoft Store simulator versions appearing as **Unknown** in session history by reading installed package metadata, with executable metadata fallback and an end-of-session retry.
- Replaced the MSFS toolbar's **1% Low** and **Frame MS** cards with live **GPU Load** and **GPU Memory** readings from GPU-Z shared memory. GPU Memory is shown as a percentage of total VRAM, with used/total MB retained in the hover detail.
- Matched the desktop Dashboard to the toolbar with the same GPU Load, VRAM usage percentage and Sim Memory cards, fed from the exact same GPU-Z sample.
- Added safe GPU-Z unavailable, updating and stale-data handling; simulator process memory remains visible as **Sim Memory**.
- Added a profile-owned **Minimize After Launch** option for companion apps. The optimizer waits for the newly launched app's normal window and minimizes it without affecting matching apps that were already running.
- Expanded automated coverage to 62 passing tests.

## 2.4.1 — 2026-09-26

- Added profile-owned companion-app preloading for Active Sky, SayIntentions, REX Core Atmos and other external tools.
- Added **Before Simulator**, **After Simulator Starts** and SimConnect-confirmed **Ready to Fly** launch stages with configurable launch delays and a bounded readiness fallback.
- Added direct executable selection, optional administrator launch, duplicate-process prevention and per-app **Leave Running** or **Close on Session End** cleanup.
- Companion apps are now stored with named flight profiles and restored when the active profile is reapplied at startup.
- Added a one-click switch to pause or resume CPU-spike and frame-stutter recording during simulator loading while keeping other dashboard telemetry active.
- Clarified the Custom Apps workflow, removed the unused command-line Arguments field and fixed its launch/cleanup dropdown controls.
- Expanded automated coverage to 59 passing tests.

## 2.4.0 — 2026-09-17

- Added flight-session history, performance trend graphs, session comparisons, and automatic simulator, GPU-driver and profile-change markers.
- Added read-only CPU/GPU balance recommendations for DLSS mode, render scaling and stable frame-rate targets, with safe `UserCfg.opt` backup support.
- Added VR runtime diagnostics and MSFS online-services health checks, including official MSFS 2024 service status, local networking, Microsoft/Xbox DNS and supporting services.
- Expanded saved profiles with import, export, duplicate, rename, simulator/aircraft/headset/monitor associations, and exact saved-versus-current differences.
- Added a one-click privacy-scrubbed support package for faster troubleshooting.
- Added service dependency and dependent visibility, pre-flight warnings, restart-command safety checks and a **Test Restart** action.
- Added a proper Windows installer and clean uninstaller.
- Added verified in-app installer downloads from official GitHub releases with SHA-256 validation.
- Kept the freeware release process simple and unsigned, without paid certificate requirements.
- Expanded automated coverage to 58 passing tests.

## 2.3.0

- Added the dedicated **Display / DLSS** tab, saved 2D/VR rendering modes, NVIDIA App model presets, active DLSS runtime version, driver details and an in-simulator DLSS information overlay switch.
- Preserved the Xbox, Gaming Services, authentication, game-save and networking stack after MSFS exits to protect live weather on subsequent launches.
- Improved protected-process handling and expanded automated validation to 50 tests.

## 2.2.1

- Added manual update checking, initial read-only display/DLSS inspection and improved dashboard FPS-source handling.
- Removed forced post-flight Xbox/Game Bar cleanup and clarified degraded telemetry states.

## 2.2.0

- Added opt-in online application/service guidance, local software identity inspection and safer unmatched-item guidance.
- Added selected-item sorting, clearer explanations, full hover text and profile persistence for online guidance.

## 2.1.0

- Added per-application **Restart** or **Left Closed** behavior, service restoration visibility and improved saved-profile state handling.
- Combined launch confirmations, improved post-flight shutdown and fixed application restart/restoration edge cases.
