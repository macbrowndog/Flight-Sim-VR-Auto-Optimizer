using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SimVROptimizer.Core;

public sealed record SimulatorDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> ProcessNames,
    LaunchKind LaunchKind,
    string LaunchTarget,
    string Arguments = "");

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LaunchKind
{
    Uri,
    Executable
}

public sealed class OptimizerOptions
{
    public bool DryRun { get; set; }
    public OptimizationProfile Profile { get; set; } = OptimizationProfile.Standard;
    public bool UseUltimatePowerPlan { get; set; } = true;
    public bool EnableNvidiaPersistence { get; set; }
    public bool UseMsfs2024FastLaunch { get; set; } = true;
    public bool FlushDnsCache { get; set; } = true;
    public bool DisableGameDvr { get; set; }
    public bool ClearStandbyMemory { get; set; }
    public bool UseHighResolutionTimer { get; set; }
    public bool DisableFullscreenOptimizations { get; set; }
    public bool DisablePowerThrottling { get; set; }
    public bool ApplyNetworkMemoryOptimizations { get; set; }
    public ProcessPriorityPreference ProcessPriority { get; set; } = ProcessPriorityPreference.AboveNormal;
    public bool UseVendorAwareCpuSets { get; set; }
    public bool ContentCreatorMode { get; set; }
    public VrRuntimePreference VrRuntime { get; set; } = VrRuntimePreference.None;
    public int LaunchTimeoutSeconds { get; set; } = 180;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OptimizationProfile
{
    Standard,
    Aggressive
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VrRuntimePreference
{
    None,
    VirtualDesktop,
    PimaxPlay,
    SteamVR
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProcessPriorityPreference
{
    Normal,
    AboveNormal,
    High
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionMode
{
    Manual,
    Automatic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionStage
{
    Prepare = 1,
    Optimize = 2,
    VrRuntime = 3,
    Simulator = 4,
    Restore = 5
}

public sealed record SessionProgress(SessionStage Stage, string Title, string Detail);

public sealed record CpuSetDescriptor(
    uint Id,
    ushort Group,
    byte LogicalProcessorIndex,
    byte CoreIndex,
    byte EfficiencyClass,
    bool Parked);

public sealed record CpuProfile(
    string Vendor,
    string Model,
    bool IsIntel,
    bool IsAmd,
    bool IsX3D,
    bool IsHybrid,
    int PhysicalCoreCount,
    int LogicalProcessorCount,
    IReadOnlyList<CpuSetDescriptor> CpuSets);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImpactLevel
{
    High,
    Medium,
    Low,
    Unknown
}

public sealed class DetectedSimulator
{
    public required SimulatorDefinition Definition { get; init; }
    public required string Detection { get; init; }
    public string Name => Definition.Name;
}

public sealed class RunningAppCandidate : INotifyPropertyChanged
{
    private bool _selected;

    public required string ProcessName { get; init; }
    public required string DisplayName { get; init; }
    public required ImpactLevel Impact { get; init; }
    public required string Reason { get; init; }
    public required int InstanceCount { get; init; }
    public required long MemoryMb { get; init; }
    public string? ExecutablePath { get; init; }
    public required string RestartCommand { get; init; }
    public required bool CanStop { get; init; }
    public bool IsCustom { get; init; }
    public string RestartSupport => RestartCommand.StartsWith("none:", StringComparison.OrdinalIgnoreCase) ? "Manual" : "Automatic";
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public static class SessionSelectionPolicy
{
    private static readonly string[] CreatorApplicationMarkers =
    [
        "obs", "streamlabs", "twitchstudio", "twitch studio", "streamdeck", "stream deck",
        "nvidia broadcast", "voicemeeter", "voice meeter", "elgato", "xsplit", "vmix",
        "tiktok live studio", "meld studio", "discord", "ndi", "blackmagic", "aja"
    ];

    private static readonly string[] CreatorServiceMarkers =
    [
        "streamdeck", "stream deck", "elgato", "vb-audio", "voicemeeter", "voice meeter",
        "nvidia broadcast", "xsplit", "ndi", "blackmagic", "aja"
    ];

    public static void SelectAutomatic(
        IEnumerable<RunningAppCandidate> applications,
        IEnumerable<ServiceCandidate> services,
        bool contentCreatorMode = false,
        OptimizationProfile profile = OptimizationProfile.Standard)
    {
        foreach (var application in applications)
            application.Selected = application.CanStop
                && application.RestartSupport == "Automatic"
                && (profile == OptimizationProfile.Aggressive || application.IsCustom || application.Impact is ImpactLevel.High or ImpactLevel.Medium)
                && !(contentCreatorMode && IsContentCreatorApplication(application));
        foreach (var service in services)
            service.Selected = profile == OptimizationProfile.Aggressive
                && service.CanStop
                && !(contentCreatorMode && IsContentCreatorService(service));
    }

    public static bool IsContentCreatorApplication(RunningAppCandidate application) =>
        ContainsMarker(application.ProcessName, CreatorApplicationMarkers)
        || ContainsMarker(application.DisplayName, CreatorApplicationMarkers);

    public static bool IsContentCreatorService(ServiceCandidate service) =>
        ContainsMarker(service.ServiceName, CreatorServiceMarkers)
        || ContainsMarker(service.DisplayName, CreatorServiceMarkers);

    public static void Clear(
        IEnumerable<RunningAppCandidate> applications,
        IEnumerable<ServiceCandidate> services)
    {
        foreach (var application in applications) application.Selected = false;
        foreach (var service in services) service.Selected = false;
    }

    public static void ApplySaved(
        IEnumerable<RunningAppCandidate> applications,
        IEnumerable<ServiceCandidate> services,
        IReadOnlyDictionary<string, bool> applicationSelections,
        IReadOnlyDictionary<string, bool> serviceSelections,
        OptimizationProfile profile,
        bool contentCreatorMode)
    {
        foreach (var application in applications)
        {
            if (TryGetSelection(applicationSelections, application.ProcessName, out var selected))
                application.Selected = selected
                    && application.CanStop
                    && !(contentCreatorMode && IsContentCreatorApplication(application));
        }

        foreach (var service in services)
        {
            if (TryGetSelection(serviceSelections, service.ServiceName, out var selected))
                service.Selected = selected
                    && profile == OptimizationProfile.Aggressive
                    && service.CanStop
                    && !(contentCreatorMode && IsContentCreatorService(service));
        }
    }

    private static bool TryGetSelection(
        IReadOnlyDictionary<string, bool> selections,
        string key,
        out bool selected)
    {
        if (selections.TryGetValue(key, out selected)) return true;
        foreach (var pair in selections)
        {
            if (!pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            selected = pair.Value;
            return true;
        }

        selected = false;
        return false;
    }

    private static bool ContainsMarker(string value, IEnumerable<string> markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}

public sealed class ServiceCandidate : INotifyPropertyChanged
{
    private bool _selected;

    public required string ServiceName { get; init; }
    public required string DisplayName { get; init; }
    public required ImpactLevel Impact { get; init; }
    public required string Reason { get; init; }
    public required bool CanStop { get; init; }
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SystemScanResult
{
    public IReadOnlyList<DetectedSimulator> Simulators { get; init; } = [];
    public IReadOnlyList<RunningAppCandidate> Applications { get; init; } = [];
    public IReadOnlyList<ServiceCandidate> Services { get; init; } = [];
}

public sealed class AppConfig
{
    public string? SelectedSimulatorId { get; set; }
    public SessionMode SessionMode { get; set; } = SessionMode.Manual;
    public OptimizerOptions Options { get; set; } = new();
    public List<CustomApplicationRule> CustomApplications { get; set; } = [];
    public Dictionary<string, bool> ApplicationSelections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> ServiceSelections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CustomApplicationRule
{
    public string ProcessName { get; set; } = "";
    public string RestartExecutablePath { get; set; } = "";
}

public sealed class PendingLaunch
{
    public required string SimulatorId { get; init; }
    public required SessionMode SessionMode { get; init; }
    public required OptimizerOptions Options { get; init; }
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    public IReadOnlyList<string> ServiceNames { get; init; } = [];
    public IReadOnlyList<CustomApplicationRule> CustomApplications { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MutationKind
{
    PowerPlan,
    CreatedPowerPlan,
    Service,
    NvidiaPersistence,
    Process,
    RegistryValue
}

public sealed record StateMutation(
    MutationKind Kind,
    string Target,
    string OriginalValue,
    string AppliedValue,
    DateTimeOffset RecordedAtUtc);

public sealed class SessionJournal
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SimulatorName { get; init; } = "";
    public bool DryRun { get; init; }
    public List<StateMutation> Mutations { get; init; } = [];
}

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
