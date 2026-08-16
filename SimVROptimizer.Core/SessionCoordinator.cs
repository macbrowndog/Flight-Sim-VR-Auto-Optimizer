using System.Diagnostics;

namespace SimVROptimizer.Core;

public sealed class SessionCoordinator
{
    private readonly TransactionalOptimizer _optimizer;
    private readonly SimulatorLauncher _launcher;
    private readonly VrRuntimeLauncher? _vrRuntimeLauncher;
    private readonly IXboxSessionCleanup? _xboxSessionCleanup;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public SessionCoordinator(
        TransactionalOptimizer optimizer,
        SimulatorLauncher launcher,
        VrRuntimeLauncher? vrRuntimeLauncher = null,
        IXboxSessionCleanup? xboxSessionCleanup = null)
    {
        _optimizer = optimizer;
        _launcher = launcher;
        _vrRuntimeLauncher = vrRuntimeLauncher;
        _xboxSessionCleanup = xboxSessionCleanup;
    }

    public event Action<string>? StatusChanged
    {
        add
        {
            _optimizer.StatusChanged += value;
            _launcher.StatusChanged += value;
            if (_vrRuntimeLauncher is not null) _vrRuntimeLauncher.StatusChanged += value;
            if (_xboxSessionCleanup is not null) _xboxSessionCleanup.StatusChanged += value;
        }
        remove
        {
            _optimizer.StatusChanged -= value;
            _launcher.StatusChanged -= value;
            if (_vrRuntimeLauncher is not null) _vrRuntimeLauncher.StatusChanged -= value;
            if (_xboxSessionCleanup is not null) _xboxSessionCleanup.StatusChanged -= value;
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
        CancellationToken cancellationToken)
    {
        if (!await _sessionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A simulator session is already active.");

        IsRunning = true;
        Process? process = null;
        VrRuntimeSession? runtimeSession = null;
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

            ReportProgress(SessionStage.Simulator, "SIMULATOR", $"Launching and monitoring {simulator.Name}.");
            process = await _launcher.LaunchAndWaitAsync(simulator, options, cancellationToken).ConfigureAwait(false);
            if (process is not null)
            {
                SimulatorProcessChanged?.Invoke(process.Id);
                await _optimizer.VerifyOrReapplySessionPowerPlanAsync(cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                simulatorExited = true;
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
                await _optimizer.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                SimulatorProcessChanged?.Invoke(null);
                process?.Dispose();
                IsRunning = false;
                _sessionGate.Release();
            }
        }
    }

    public Task<RestorationReport> RestoreRecoveryAsync(CancellationToken cancellationToken = default) => _optimizer.RestoreAsync(cancellationToken);

    private void ReportProgress(SessionStage stage, string title, string detail) =>
        ProgressChanged?.Invoke(new SessionProgress(stage, title, detail));

    private static bool IsMicrosoftFlightSimulator(SimulatorDefinition simulator) =>
        simulator.Id.StartsWith("msfs", StringComparison.OrdinalIgnoreCase);
}
