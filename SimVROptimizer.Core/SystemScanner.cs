using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SimVROptimizer.Core;

public sealed class SystemScanner
{
    private sealed record ImpactProfile(ImpactLevel Level, string DisplayName, string Reason);

    private static readonly Dictionary<string, ImpactProfile> AppProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Discord"] = new(ImpactLevel.High, "Discord", "Overlay and background activity are specifically identified by MSFS Support."),
        ["Overwolf"] = new(ImpactLevel.High, "Overwolf", "Overlay software is specifically identified by MSFS Support."),
        ["RTSS"] = new(ImpactLevel.High, "RivaTuner Statistics Server", "Monitoring/overlay software is specifically identified by MSFS Support."),
        ["MSIAfterburner"] = new(ImpactLevel.High, "MSI Afterburner", "Monitoring and overclocking utilities are specifically identified by MSFS Support."),
        ["NVIDIA Share"] = new(ImpactLevel.High, "NVIDIA Overlay / ShadowPlay", "Capture and overlay software is specifically identified by MSFS Support."),
        ["GameBar"] = new(ImpactLevel.High, "Xbox Game Bar", "Game DVR/overlay capture can add background GPU and CPU work."),
        ["XboxGameBar"] = new(ImpactLevel.High, "Xbox Game Bar", "Game DVR/overlay capture can add background GPU and CPU work."),
        ["obs64"] = new(ImpactLevel.Medium, "OBS Studio", "Live capture/encoding competes for GPU, CPU, and memory bandwidth when active."),
        ["TwitchStudio"] = new(ImpactLevel.High, "Twitch Studio", "Streaming software is specifically identified by MSFS Support."),
        ["Razer Synapse Service Process"] = new(ImpactLevel.Medium, "Razer Synapse", "Razer background statistics/gamecaster components are identified by MSFS Support."),
        ["RazerAppEngine"] = new(ImpactLevel.Medium, "Razer Synapse", "Razer background statistics/gamecaster components are identified by MSFS Support."),
        ["lghub"] = new(ImpactLevel.Medium, "Logitech G Hub", "MSFS crash guidance identifies Logitech G Hub as a possible background conflict."),
        ["lghub_agent"] = new(ImpactLevel.Medium, "Logitech G Hub Agent", "MSFS crash guidance identifies Logitech G Hub as a possible background conflict."),
        ["LenovoVantage"] = new(ImpactLevel.Medium, "Lenovo Vantage", "Identified by MSFS Support as a background application worth closing for diagnosis."),
        ["OneDrive"] = new(ImpactLevel.Medium, "Microsoft OneDrive", "Active file synchronization can consume network and storage bandwidth; pausing sync is preferable."),
        ["Dropbox"] = new(ImpactLevel.Medium, "Dropbox", "Active cloud synchronization can consume network and storage bandwidth."),
        ["GoogleDriveFS"] = new(ImpactLevel.Medium, "Google Drive", "Active cloud synchronization can consume network and storage bandwidth."),
        ["chrome"] = new(ImpactLevel.Low, "Google Chrome", "Many tabs can consume substantial memory, CPU, and GPU resources."),
        ["msedge"] = new(ImpactLevel.Low, "Microsoft Edge", "Many tabs can consume substantial memory, CPU, and GPU resources."),
        ["firefox"] = new(ImpactLevel.Low, "Mozilla Firefox", "Many tabs can consume substantial memory, CPU, and GPU resources."),
        ["Creative Cloud"] = new(ImpactLevel.Medium, "Adobe Creative Cloud", "Background synchronization and update activity can consume storage and network resources."),
        ["CCXProcess"] = new(ImpactLevel.Medium, "Adobe Creative Cloud Experience", "Background synchronization and update activity can consume storage and network resources."),
        ["CCleaner"] = new(ImpactLevel.Medium, "CCleaner", "Monitoring, update, and Performance Optimizer activity can add background CPU and storage work."),
        ["CCleaner64"] = new(ImpactLevel.Medium, "CCleaner", "Monitoring, update, and Performance Optimizer activity can add background CPU and storage work."),
        ["CCleanerBrowser"] = new(ImpactLevel.Low, "CCleaner Browser", "Browser processes can consume memory, CPU, and GPU resources."),
        ["PhoneExperienceHost"] = new(ImpactLevel.Low, "Microsoft Phone Link", "Phone synchronization, notifications, and cross-device features consume some memory and background resources."),
        ["CrossDeviceService"] = new(ImpactLevel.Low, "Microsoft Cross Device Service", "Cross-device synchronization and notifications consume some memory and background resources."),
        ["GoogleUpdater"] = new(ImpactLevel.Medium, "Google Updater", "Update checks and downloads can use network and storage bandwidth."),
        ["GoogleCrashHandler"] = new(ImpactLevel.Low, "Google Crash Handler", "Background crash-reporting component; usually low impact."),
        ["GoogleCrashHandler64"] = new(ImpactLevel.Low, "Google Crash Handler", "Background crash-reporting component; usually low impact."),
        ["iCloudDrive"] = new(ImpactLevel.Medium, "iCloud Drive", "Active cloud synchronization can consume network and storage bandwidth."),
        ["iCloudServices"] = new(ImpactLevel.Medium, "iCloud Services", "Active cloud synchronization can consume network and storage bandwidth."),
        ["ApplePhotoStreams"] = new(ImpactLevel.Medium, "iCloud Photos", "Photo synchronization can consume network, storage, and CPU resources."),
        ["APSDaemon"] = new(ImpactLevel.Low, "Apple Push Service", "Apple notification and synchronization activity is not required while MSFS is running."),
        ["mDNSResponder"] = new(ImpactLevel.Low, "Bonjour", "Apple network-service discovery is not required for MSFS."),
        ["AppleMobileDeviceService"] = new(ImpactLevel.Low, "Apple Mobile Device Service", "Apple device discovery and synchronization are not required for MSFS."),
        ["iPodService"] = new(ImpactLevel.Low, "Apple iPod Service", "Apple device access is not required for MSFS."),
        ["iTunesHelper"] = new(ImpactLevel.Low, "iTunes Helper", "Apple device detection is not required for MSFS."),
        ["Spotify"] = new(ImpactLevel.Low, "Spotify", "Media playback and its embedded browser processes consume memory and some CPU."),
        ["Teams"] = new(ImpactLevel.Medium, "Microsoft Teams", "Calls, effects, notifications, and embedded browser processes can consume CPU, GPU, and memory."),
        ["ms-teams"] = new(ImpactLevel.Medium, "Microsoft Teams", "Calls, effects, notifications, and embedded browser processes can consume CPU, GPU, and memory."),
        ["Zoom"] = new(ImpactLevel.Medium, "Zoom", "Calls and video effects can consume CPU, GPU, network, and memory resources."),
        ["steam"] = new(ImpactLevel.Low, "Steam", "Required to launch and maintain Steam simulator sessions; protected from stopping."),
        ["VirtualDesktop.Streamer"] = new(ImpactLevel.Low, "Virtual Desktop Streamer", "VR runtime component; protected from stopping."),
        ["vrserver"] = new(ImpactLevel.Low, "SteamVR Server", "VR runtime component; protected from stopping."),
        ["vrmonitor"] = new(ImpactLevel.Low, "SteamVR Monitor", "VR runtime component; protected from stopping.")
    };

    private static readonly Dictionary<string, ImpactProfile> ServiceProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NahimicService"] = new(ImpactLevel.High, "Nahimic Service", "MSFS crash guidance identifies Nahimic/audio enhancement software as a possible conflict."),
        ["SysMain"] = new(ImpactLevel.Low, "SysMain", "No general MSFS benefit is established; consider stopping only if Task Manager shows sustained activity."),
        ["Spooler"] = new(ImpactLevel.Low, "Print Spooler", "Normally negligible unless print work is active. Do not stop when printing."),
        ["WSearch"] = new(ImpactLevel.Low, "Windows Search", "Indexing can create storage activity, but stopping it is usually unnecessary unless activity is observed."),
        ["CorsairService"] = new(ImpactLevel.Medium, "Corsair Service", "RGB/device suites can add background CPU activity; flight controls may depend on this service."),
        ["LightingService"] = new(ImpactLevel.Medium, "ASUS Lighting Service", "RGB lighting activity can add background CPU work."),
        ["Razer Central Service"] = new(ImpactLevel.Medium, "Razer Central Service", "Razer background components are identified by MSFS Support as possible conflicts."),
        ["LGHUBUpdaterService"] = new(ImpactLevel.Medium, "Logitech G Hub Updater", "G Hub is identified as a possible conflict, but flight controls may depend on it."),
        ["Apple Mobile Device Service"] = new(ImpactLevel.Low, "Apple Mobile Device Service", "Used for iPhone/iPad discovery and synchronization; it can be paused during an MSFS session."),
        ["Bonjour Service"] = new(ImpactLevel.Low, "Apple Bonjour Service", "Used for Apple network-service discovery; it can be paused when Apple sharing and device discovery are not needed."),
        ["iPod Service"] = new(ImpactLevel.Low, "Apple iPod Service", "Used for Apple device access; it can be paused during an MSFS session."),
        ["iCloudDrive"] = new(ImpactLevel.Medium, "iCloud Drive Service", "Cloud file synchronization can use network, storage, CPU, and memory resources."),
        ["iCloudPhotos"] = new(ImpactLevel.Medium, "iCloud Photos Service", "Photo synchronization can use network, storage, CPU, and memory resources."),
        ["iCloudServices"] = new(ImpactLevel.Medium, "iCloud Services", "iCloud synchronization is not required while MSFS is running."),
        ["DiagTrack"] = new(ImpactLevel.Low, "Connected User Experiences and Telemetry", "Windows diagnostic telemetry is normally low impact but can be paused temporarily during the simulator session."),
        ["ClickToRunSvc"] = new(ImpactLevel.Low, "Microsoft Office Click-to-Run", "Office update and streaming activity is not required while MSFS is running."),
        ["EABackgroundService"] = new(ImpactLevel.Low, "EA Background Service", "The EA launcher background service is not required for MSFS."),
        ["EpicOnlineServices"] = new(ImpactLevel.Low, "Epic Online Services", "Epic background services are not normally required for MSFS."),
        ["GalaxyClientService"] = new(ImpactLevel.Low, "GOG Galaxy Client Service", "The GOG launcher service is not required for MSFS."),
        ["GalaxyCommunication"] = new(ImpactLevel.Low, "GOG Galaxy Communication Service", "The GOG launcher service is not required for MSFS."),
        ["DoSvc"] = new(ImpactLevel.Medium, "Delivery Optimization", "Windows downloads can use network/storage bandwidth. This system service is shown for information only."),
        ["BITS"] = new(ImpactLevel.Medium, "Background Intelligent Transfer Service", "Background downloads can use network bandwidth. This system service is shown for information only.")
    };

    private static readonly HashSet<string> NeverStopApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "services", "svchost", "lsass", "System", "Idle",
        "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "SecurityHealthSystray", "MsMpEng",
        "SimVROptimizer", "taskhostw", "sihost", "fontdrvhost"
    };

    private static readonly HashSet<string> ProtectedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "VirtualDesktop.Streamer", "vrserver", "vrmonitor", "vrcompositor",
        "OculusClient", "OVRServer_x64", "PimaxClient", "PimaxPlay", "PiTool"
    };

    private readonly ICommandRunner _commands;
    public SystemScanner(ICommandRunner commands) => _commands = commands;

    public async Task<SystemScanResult> ScanAsync(
        IReadOnlyList<CustomApplicationRule>? customApplications = null,
        CancellationToken cancellationToken = default)
    {
        var simulatorsTask = DetectSimulatorsAsync(cancellationToken);
        var servicesTask = ScanServicesAsync(cancellationToken);
        var applications = ScanApplications(customApplications ?? []);
        return new SystemScanResult
        {
            Simulators = await simulatorsTask.ConfigureAwait(false),
            Applications = applications,
            Services = await servicesTask.ConfigureAwait(false)
        };
    }

    private async Task<IReadOnlyList<DetectedSimulator>> DetectSimulatorsAsync(CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, DetectedSimulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in FindSteamLibraries())
        {
            foreach (var pair in SimulatorCatalog.SteamByAppId)
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{pair.Key}.acf");
                if (File.Exists(manifest)) found[pair.Value.Id] = new DetectedSimulator { Definition = pair.Value, Detection = manifest };
            }
        }

        await DetectStoreAppAsync(found, SimulatorCatalog.All[4], "Microsoft.Limitless", cancellationToken).ConfigureAwait(false);
        await DetectStoreAppAsync(found, SimulatorCatalog.All[5], "Microsoft.FlightSimulator", cancellationToken).ConfigureAwait(false);
        DetectDcsStandalone(found);
        DetectXPlaneStandalone(found);
        return found.Values.OrderBy(item => item.Name).ToArray();
    }

    private IEnumerable<string> FindSteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var registryPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (!string.IsNullOrWhiteSpace(registryPath)) roots.Add(registryPath.Replace('/', '\\'));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

        foreach (var root in roots.ToArray())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\\\"path\\\"\\s+\\\"(?<path>[^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                roots.Add(match.Groups["path"].Value.Replace("\\\\", "\\"));
        }
        return roots.Where(Directory.Exists);
    }

    private async Task DetectStoreAppAsync(Dictionary<string, DetectedSimulator> found, SimulatorDefinition definition, string packageName, CancellationToken cancellationToken)
    {
        var command = $"if (Get-AppxPackage -Name '{packageName}' -ErrorAction SilentlyContinue) {{ 'installed' }}";
        var result = await _commands.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && result.StandardOutput.Contains("installed", StringComparison.OrdinalIgnoreCase))
            found[definition.Id] = new DetectedSimulator { Definition = definition, Detection = $"Store package: {packageName}" };
    }

    private static void DetectDcsStandalone(Dictionary<string, DetectedSimulator> found)
    {
        var keys = new[]
        {
            @"HKEY_CURRENT_USER\Software\Eagle Dynamics\DCS World",
            @"HKEY_CURRENT_USER\Software\Eagle Dynamics\DCS World OpenBeta",
            @"HKEY_LOCAL_MACHINE\Software\Eagle Dynamics\DCS World",
            @"HKEY_LOCAL_MACHINE\Software\Eagle Dynamics\DCS World OpenBeta"
        };
        foreach (var key in keys)
        {
            if (Registry.GetValue(key, "Path", null) is not string root) continue;
            foreach (var relative in new[] { @"bin-mt\DCS.exe", @"bin\DCS.exe" })
            {
                var executable = Path.Combine(root, relative);
                if (!File.Exists(executable)) continue;
                var definition = new SimulatorDefinition("dcs-standalone", "DCS World (Standalone)", ["DCS", "DCS_mt"], LaunchKind.Executable, executable);
                found[definition.Id] = new DetectedSimulator { Definition = definition, Detection = executable };
                return;
            }
        }
    }

    private static void DetectXPlaneStandalone(Dictionary<string, DetectedSimulator> found)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            var executable = Path.Combine(drive.RootDirectory.FullName, "X-Plane 12", "X-Plane.exe");
            if (!File.Exists(executable)) continue;
            var definition = new SimulatorDefinition("xplane12-standalone", "X-Plane 12 (Standalone)", ["X-Plane"], LaunchKind.Executable, executable);
            found[definition.Id] = new DetectedSimulator { Definition = definition, Detection = executable };
        }
    }

    private static IReadOnlyList<RunningAppCandidate> ScanApplications(IReadOnlyList<CustomApplicationRule> customApplications)
    {
        var applications = Process.GetProcesses()
            .Where(process => !NeverStopApps.Contains(process.ProcessName))
            .GroupBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(CreateAppCandidate)
            .Where(candidate => candidate is not null)
            .Cast<RunningAppCandidate>()
            .ToDictionary(candidate => candidate.ProcessName, StringComparer.OrdinalIgnoreCase);

        ApplyCustomApplications(applications, customApplications);
        return applications.Values
            .OrderBy(candidate => candidate.Impact)
            .ThenByDescending(candidate => candidate.MemoryMb)
            .ToArray();
    }

    private static void ApplyCustomApplications(
        IDictionary<string, RunningAppCandidate> applications,
        IEnumerable<CustomApplicationRule> rules)
    {
        foreach (var rule in rules.Where(rule => !string.IsNullOrWhiteSpace(rule.ProcessName)))
        {
            var processName = NormalizeProcessName(rule.ProcessName);
            if (NeverStopApps.Contains(processName) || ProtectedApps.Contains(processName)) continue;

            var processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Length == 0) continue;
                applications.TryGetValue(processName, out var existing);
                var discoveredPath = processes
                    .Select(process => TryGet(() => process.MainModule?.FileName, null))
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
                var restartPath = string.IsNullOrWhiteSpace(rule.RestartExecutablePath)
                    ? discoveredPath
                    : Environment.ExpandEnvironmentVariables(rule.RestartExecutablePath.Trim().Trim('"'));
                applications[processName] = new RunningAppCandidate
                {
                    ProcessName = processName,
                    DisplayName = existing?.DisplayName ?? processName,
                    Impact = existing?.Impact ?? ImpactLevel.Unknown,
                    Reason = "Persistent custom application rule. The process is stopped for the session and restarted from the configured executable when available.",
                    InstanceCount = processes.Length,
                    MemoryMb = processes.Sum(process => TryGet(() => process.WorkingSet64, 0L)) / 1024 / 1024,
                    ExecutablePath = discoveredPath,
                    RestartCommand = string.IsNullOrWhiteSpace(restartPath) ? "none:" : "exe:" + restartPath,
                    CanStop = true,
                    IsCustom = true
                };
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
    }

    private static RunningAppCandidate? CreateAppCandidate(IGrouping<string, Process> group)
    {
        var processes = group.ToArray();
        try
        {
            AppProfiles.TryGetValue(group.Key, out var profile);
            var path = processes.Select(process => TryGet(() => process.MainModule?.FileName, null)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var hasWindow = processes.Any(process => TryGet(() => !string.IsNullOrWhiteSpace(process.MainWindowTitle), false));
            var thirdParty = !string.IsNullOrWhiteSpace(path) && !IsWindowsComponent(path);
            if (profile is null && !hasWindow && !thirdParty) return null;

            var memory = processes.Sum(process => TryGet(() => process.WorkingSet64, 0L)) / 1024 / 1024;
            var fileDescription = !string.IsNullOrWhiteSpace(path)
                ? TryGet(() => FileVersionInfo.GetVersionInfo(path).FileDescription, null)
                : null;
            var windowTitle = processes
                .Select(process => TryGet(() => process.MainWindowTitle, ""))
                .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
            var restartCommand = ResolveRestartCommand(group.Key, path);
            return new RunningAppCandidate
            {
                ProcessName = group.Key,
                DisplayName = FirstNonBlank(profile?.DisplayName, fileDescription, windowTitle, group.Key),
                Impact = profile?.Level ?? ImpactLevel.Unknown,
                Reason = profile?.Reason ?? "Third-party or visible application with no known MSFS-specific conflict. Stop only if you recognize it and do not need it.",
                InstanceCount = processes.Length,
                MemoryMb = memory,
                ExecutablePath = path,
                RestartCommand = restartCommand,
                CanStop = !ProtectedApps.Contains(group.Key)
            };
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private async Task<IReadOnlyList<ServiceCandidate>> ScanServicesAsync(CancellationToken cancellationToken)
    {
        // Omitting state= uses SC's compatible default of active services. Some current
        // Windows builds reject the otherwise documented "state= active" spelling.
        var result = await _commands.RunAsync("sc.exe", ["query", "type=", "service"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) return [];
        var runningNames = OutputParsers.ParseRunningServices(result.StandardOutput);
        return runningNames
            .Select(name =>
            {
                var profile = GetServiceProfile(name);
                var imagePath = GetServiceImagePath(name);
                var thirdParty = !string.IsNullOrWhiteSpace(imagePath) && !IsWindowsComponent(imagePath);
                if (profile is null && !thirdParty) return null;
                return new ServiceCandidate
                {
                    ServiceName = name,
                    DisplayName = profile?.DisplayName ?? name,
                    Impact = profile?.Level ?? ImpactLevel.Unknown,
                    Reason = profile?.Reason ?? "Running third-party service with no known MSFS-specific impact. Stop only if you understand what depends on it.",
                    CanStop = !IsProtectedService(name)
                };
            })
            .Where(candidate => candidate is not null)
            .Cast<ServiceCandidate>()
            .OrderBy(candidate => candidate.Impact)
            .ThenBy(candidate => candidate.DisplayName)
            .ToArray();
    }

    private static ImpactProfile? GetServiceProfile(string name)
    {
        if (ServiceProfiles.TryGetValue(name, out var profile)) return profile;
        if (name.StartsWith("GoogleUpdater", StringComparison.OrdinalIgnoreCase) || name is "gupdate" or "gupdatem" or "GoogleChromeElevationService")
            return new ImpactProfile(ImpactLevel.Medium, $"Google Update ({name})", "Google Update checks for and downloads application updates. Temporary session stopping is restored afterward; keep updates enabled outside gaming sessions.");
        if (name.Contains("CCleaner", StringComparison.OrdinalIgnoreCase))
            return new ImpactProfile(ImpactLevel.Medium, $"CCleaner service ({name})", "CCleaner monitoring, update, or Performance Optimizer activity can add background CPU and storage work.");
        if (name is "edgeupdate" or "edgeupdatem" or "MozillaMaintenance")
            return new ImpactProfile(ImpactLevel.Low, name, "Browser updater service; normally low impact but may use network and storage during an update.");
        if (name.StartsWith("DropboxUpdate", StringComparison.OrdinalIgnoreCase) || name is "dbupdate" or "dbupdatem")
            return new ImpactProfile(ImpactLevel.Medium, name, "Dropbox update activity can use network and storage bandwidth.");
        if (name.StartsWith("Adobe", StringComparison.OrdinalIgnoreCase) || name is "AGMService" or "AGSService")
            return new ImpactProfile(ImpactLevel.Low, name, "Adobe background licensing or update service; usually low impact.");
        return null;
    }

    private static bool IsProtectedService(string name)
    {
        if (name is "DoSvc" or "BITS" or "Steam Client Service") return true;
        return name.Contains("Oculus", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("OVR", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pimax", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SteamVR", StringComparison.OrdinalIgnoreCase)
            || name.Contains("OpenXR", StringComparison.OrdinalIgnoreCase)
            || name.Contains("VirtualDesktop", StringComparison.OrdinalIgnoreCase)
            || name.Contains("GamingServices", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Xbox", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SimConnect", StringComparison.OrdinalIgnoreCase)
            || name.Contains("FSUIPC", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tobii", StringComparison.OrdinalIgnoreCase)
            || name.Contains("TrackIR", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Honeycomb", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Thrustmaster", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Logitech", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetServiceImagePath(string serviceName)
    {
        try
        {
            return Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{serviceName}", "ImagePath", null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWindowsComponent(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path).Trim().Trim('"');
        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return true;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return expanded.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    private static T TryGet<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    private static string NormalizeProcessName(string value)
    {
        var name = value.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string ResolveRestartCommand(string processName, string? executablePath)
    {
        if (!string.IsNullOrWhiteSpace(executablePath)) return "exe:" + executablePath;

        if (processName is "CCleaner" or "CCleaner64")
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CCleaner", "CCleaner64.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "CCleaner", "CCleaner.exe")
            };
            var candidate = candidates.FirstOrDefault(File.Exists);
            if (candidate is not null) return "exe:" + candidate;
        }

        if (processName is "PhoneExperienceHost" or "CrossDeviceService" or "YourPhone")
            return @"shell:shell:AppsFolder\Microsoft.YourPhone_8wekyb3d8bbwe!App";

        return "none:";
    }
}
