namespace SimVROptimizer.Core;

public enum PreflightStatus
{
    Ready,
    Warning,
    Blocked
}

public sealed record PreflightItem(string Name, PreflightStatus Status, string Detail);

public sealed record PreflightReport(IReadOnlyList<PreflightItem> Items)
{
    public bool CanProceed => Items.All(item => item.Status != PreflightStatus.Blocked);
    public int WarningCount => Items.Count(item => item.Status == PreflightStatus.Warning);
    public int BlockedCount => Items.Count(item => item.Status == PreflightStatus.Blocked);
}

public sealed record VrRuntimeAvailability(
    VrRuntimePreference Runtime,
    bool Available,
    bool AlreadyRunning,
    string Detail);

public sealed record SessionPreflightContext(
    bool IsAdministrator,
    bool HasRecoveryJournal,
    SimulatorDefinition? Simulator,
    VrRuntimeAvailability Runtime,
    IReadOnlyList<RunningAppCandidate> Applications,
    IReadOnlyList<ServiceCandidate> Services,
    OptimizationProfile Profile);

public static class SessionPreflight
{
    public static PreflightReport Evaluate(SessionPreflightContext context)
    {
        var items = new List<PreflightItem>
        {
            context.IsAdministrator
                ? new("Administrator access", PreflightStatus.Ready, "Administrator access is active.")
                : new("Administrator access", PreflightStatus.Warning, "Windows will request administrator access before optimization begins."),
            EvaluateSimulator(context.Simulator),
            context.Runtime.Available
                ? new("VR runtime", PreflightStatus.Ready, context.Runtime.Detail)
                : new("VR runtime", PreflightStatus.Blocked, context.Runtime.Detail),
            context.HasRecoveryJournal
                ? new("Recovery journal", PreflightStatus.Blocked, "An unfinished session must be restored before a new flight can start.")
                : new("Recovery journal", PreflightStatus.Ready, "No unfinished session is waiting for recovery."),
            EvaluateProtection(context.Applications, context.Services),
            EvaluateSelections(context.Applications, context.Services, context.Profile)
        };

        return new PreflightReport(items);
    }

    private static PreflightItem EvaluateSimulator(SimulatorDefinition? simulator)
    {
        if (simulator is null)
            return new("Simulator target", PreflightStatus.Blocked, "No detected simulator is selected.");

        if (simulator.LaunchKind == LaunchKind.Executable && !File.Exists(simulator.LaunchTarget))
            return new("Simulator target", PreflightStatus.Blocked, $"The simulator executable could not be found: {simulator.LaunchTarget}");

        return new("Simulator target", PreflightStatus.Ready, $"{simulator.Name} is detected and has a valid launch target.");
    }

    private static PreflightItem EvaluateProtection(
        IReadOnlyList<RunningAppCandidate> applications,
        IReadOnlyList<ServiceCandidate> services)
    {
        var invalidApps = applications.Count(item => item.Selected && !item.CanStop);
        var invalidServices = services.Count(item => item.Selected && !item.CanStop);
        if (invalidApps + invalidServices > 0)
            return new("Protected components", PreflightStatus.Blocked,
                $"{invalidApps} protected application(s) and {invalidServices} protected service(s) are incorrectly selected. Rescan or clear those selections.");

        var protectedApps = applications.Count(item => !item.CanStop);
        var protectedServices = services.Count(item => !item.CanStop);
        return new("Protected components", PreflightStatus.Ready,
            $"{protectedApps} application(s) and {protectedServices} service(s) are protected and will remain running.");
    }

    private static PreflightItem EvaluateSelections(
        IReadOnlyList<RunningAppCandidate> applications,
        IReadOnlyList<ServiceCandidate> services,
        OptimizationProfile profile)
    {
        var selectedApps = applications.Where(item => item.Selected && item.CanStop).ToArray();
        var selectedServices = services.Count(item => item.Selected && item.CanStop);
        var unknownApps = selectedApps.Count(item => item.Classification == WorkloadClassification.Unknown);

        if (profile != OptimizationProfile.Aggressive && selectedServices > 0)
            return new("Session selections", PreflightStatus.Blocked,
                "Services are selected while Standard mode is active. Switch to Aggressive mode or clear the service selections.");

        var detail = $"{selectedApps.Length} application(s) and {selectedServices} service(s) are selected for this session.";
        if (unknownApps > 0)
            return new("Session selections", PreflightStatus.Warning,
                $"{detail} Review the {unknownApps} selected application(s) with unknown impact before continuing.");

        return new("Session selections", PreflightStatus.Ready, detail);
    }
}
