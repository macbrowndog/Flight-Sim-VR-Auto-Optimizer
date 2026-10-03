using System.Diagnostics;

namespace SimVROptimizer.Core;

public sealed class SessionCoordinator
{
    private readonly TransactionalOptimizer _optimizer;
    private readonly SimulatorLauncher _launcher;
    private readonly VrRuntimeLauncher? _vrRuntimeLauncher;
    private readonly IXboxSessionCleanup? _xboxSessionCleanup;
    private readonly CompanionApplicationLauncher? _companionApplicationLauncher;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public SessionCoordinator(
        TransactionalOptimizer optimizer,
        SimulatorLauncher launcher,
        VrRuntimeLauncher? vrRuntimeLauncher = null,
        IXboxSessionCleanup? xboxSessionCleanup = null,
        CompanionApplicationLauncher? companionApplicationLauncher = null)
    {
        _optimizer = optimizer;
        _launcher = launcher;
        _vrRuntimeLauncher = vrRuntimeLauncher;
        _xboxSessionCleanup = xboxSessionCleanup;
        _companionApplicationLauncher = companionApplicationLauncher;
    }

    public event Action<string>? StatusChanged
    {
        add
        {
            _optimizer.StatusChanged += value;
            _launcher.StatusChanged += value;
            if (_vrRuntimeLauncher is not null) _vrRuntimeLauncher.StatusChanged += value;
            if (_xboxSessionCleanup is not null) _xboxSessionCleanup.StatusChanged += value;
            if (_companionApplicationLauncher is not null) _companionApplicationLauncher.StatusChanged += value;
        }
        remove
        {
            _optimizer.StatusChanged -= value;
            _launcher.StatusChanged -= value;
            if (_vrRuntimeLauncher is not null) _vrRuntimeLauncher.StatusChanged -= value;
            if (_xboxSessionCleanup is not null) _xboxSessionCleanup.StatusChanged -= value;
            if (_companionApplicationLauncher is not null) _companionApplicationLauncher.StatusChanged -= value;
        }
    }

    public event Action<SessionProgress>? ProgressChanged;
    public event Action<int?>? SimulatorProcessChanged;

    public bool HasRecoveryJournal => _optimizer.HasRecoveryJournal;
    public RestorationReport? LastRestorationReport => _optimizer.LastRestorationReport;
    public bool IsRunning { get; private set; }

    public async Task RunAsync(SimulatorDefinition simulator, OptimizerOptions options, CancellationToken cancellationToken)
    {
        await RunAsync(simulator, options, [], [], cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAsync(
        SimulatorDefinition simulator,
        OptimizerOptions options,
        IReadOnlyList<RunningAppCandidate> applications,
        IReadOnlyList<ServiceCandidate> services,
        IReadOnlyList<CompanionApplicationRule> companionApplications,
        CancellationToken cancellationToken)
    {
        if (!await _sessionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A simulator session is already active.");

        IsRunning = true;
        Process? process = null;
        VrRuntimeSession? runtimeSession = null;
        CompanionApplicationSession? companionSession = null;
        var simulatorExited = false;
        try
        {
            ReportProgress(SessionStage.Prepare, "PREPARE", "Validating the selected simulator and recovery state.");
            ReportProgress(SessionStage.Optimize, "OPTIMIZE", $"Applying the {options.Profile} profile and selected granular controls.");
            await _optimizer.BeginAsync(simulator, options, applications, services, cancellationToken).ConfigureAwait(false);
            ReportProgress(SessionStage.VrRuntime, "VR RUNTIME", options.VrRuntime == VrRuntimePreference.None
                ? "No automatic VR runtime selected."
                : $"Starting or checking {options.VrRuntime}.");
            if (_vrRuntimeLauncher is not null)
                runtimeSession = await _vrRuntimeLauncher.LaunchAsync(options.VrRuntime, cancellationToken).ConfigureAwait(false);

            companionSession = new CompanionApplicationSession();
            if (_companionApplicationLauncher is not null)
                await _companionApplicationLauncher.LaunchAsync(companionApplications, CompanionLaunchTiming.BeforeSimulator,
                    companionSession, options.DryRun, cancellationToken).ConfigureAwait(false);

            ReportProgress(SessionStage.Simulator, "SIMULATOR", $"Launching and monitoring {simulator.Name}.");
            process = await _launcher.LaunchAndWaitAsync(simulator, options, cancellationToken).ConfigureAwait(false);
            if (process is not null)
            {
                var sessionStartedUtc = TryGetStartTimeUtc(process) ?? DateTime.UtcNow;
                var observedProcessIds = new HashSet<int> { process.Id };
                SimulatorProcessChanged?.Invoke(process.Id);
                if (_companionApplicationLauncher is not null)
                    await _companionApplicationLauncher.LaunchAsync(companionApplications, CompanionLaunchTiming.AfterSimulatorStarts,
                        companionSession, options.DryRun, cancellationToken).ConfigureAwait(false);
                await _optimizer.VerifyOrReapplySessionPowerPlanAsync(cancellationToken).ConfigureAwait(false);
                var readyAppsPending = _companionApplicationLauncher is not null
                    && companionApplications.Any(item => item.Enabled && item.LaunchTiming == CompanionLaunchTiming.ReadyToFly);

                while (true)
                {
                    if (readyAppsPending && _companionApplicationLauncher is not null)
                    {
                        var launchReadyApps = await _companionApplicationLauncher.WaitUntilReadyToFlyAsync(
                            process,
                            IsMicrosoftFlightSimulator(simulator),
                            TimeSpan.FromSeconds(options.LaunchTimeoutSeconds),
                            cancellationToken).ConfigureAwait(false);
                        if (launchReadyApps)
                        {
                            await _companionApplicationLauncher.LaunchAsync(companionApplications, CompanionLaunchTiming.ReadyToFly,
                                companionSession, options.DryRun, cancellationToken).ConfigureAwait(false);
                            readyAppsPending = false;
                        }
                    }

                    if (!process.HasExited)
                        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                    var replacement = await _launcher.WaitForReplacementAsync(
                        simulator,
                        options,
                        observedProcessIds,
                        sessionStartedUtc,
                        TimeSpan.FromSeconds(15),
                        cancellationToken).ConfigureAwait(false);
                    if (replacement is null)
                    {
                        simulatorExited = true;
                        break;
                    }

                    await _launcher.RestoreProcessTuningAsync(process, CancellationToken.None).ConfigureAwait(false);
                    process.Dispose();
                    process = replacement;
                    observedProcessIds.Add(process.Id);
                    SimulatorProcessChanged?.Invoke(process.Id);
                }
            }
        }
        finally
        {
            try
            {
                ReportProgress(SessionStage.Restore, "RESTORE", "Returning the recorded system and runtime state.");
                if (process is not null)
                    await _launcher.RestoreProcessTuningAsync(process, CancellationToken.None).ConfigureAwait(false);
                if (simulatorExited && IsMicrosoftFlightSimulator(simulator) && _xboxSessionCleanup is not null)
                {
                    try { await _xboxSessionCleanup.CleanupAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception exception)
                    {
                        ReportProgress(SessionStage.Restore, "RESTORE", "Xbox post-flight cleanup could not complete: " + exception.Message);
                    }
                }
                if (_vrRuntimeLauncher is not null)
                    await _vrRuntimeLauncher.RestoreAsync(runtimeSession, CancellationToken.None).ConfigureAwait(false);
                if (_companionApplicationLauncher is not null)
                    await _companionApplicationLauncher.CleanupAsync(companionSession, CancellationToken.None).ConfigureAwait(false);
                await _optimizer.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                SimulatorProcessChanged?.Invoke(null);
                process?.Dispose();
                companionSession?.Dispose();
                IsRunning = false;
                _sessionGate.Release();
            }
        }
    }

    public Task RunAsync(
        SimulatorDefinition simulator,
        OptimizerOptions options,
        IReadOnlyList<RunningAppCandidate> applications,
        IReadOnlyList<ServiceCandidate> services,
        CancellationToken cancellationToken) =>
        RunAsync(simulator, options, applications, services, [], cancellationToken);

    public Task<RestorationReport> RestoreRecoveryAsync(CancellationToken cancellationToken = default) => _optimizer.RestoreAsync(cancellationToken);

    private void ReportProgress(SessionStage stage, string title, string detail) =>
        ProgressChanged?.Invoke(new SessionProgress(stage, title, detail));

    private static bool IsMicrosoftFlightSimulator(SimulatorDefinition simulator) =>
        simulator.Id.StartsWith("msfs", StringComparison.OrdinalIgnoreCase);

    private static DateTime? TryGetStartTimeUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
