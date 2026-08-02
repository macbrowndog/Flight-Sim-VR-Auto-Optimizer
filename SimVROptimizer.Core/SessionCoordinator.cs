using System.Diagnostics;

namespace SimVROptimizer.Core;

public sealed class SessionCoordinator
{
    private readonly TransactionalOptimizer _optimizer;
    private readonly SimulatorLauncher _launcher;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public SessionCoordinator(TransactionalOptimizer optimizer, SimulatorLauncher launcher)
    {
        _optimizer = optimizer;
        _launcher = launcher;
    }

    public event Action<string>? StatusChanged
    {
        add { _optimizer.StatusChanged += value; _launcher.StatusChanged += value; }
        remove { _optimizer.StatusChanged -= value; _launcher.StatusChanged -= value; }
    }

    public bool HasRecoveryJournal => _optimizer.HasRecoveryJournal;
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
        try
        {
            await _optimizer.BeginAsync(simulator.Name, options, applications, services, cancellationToken).ConfigureAwait(false);
            process = await _launcher.LaunchAndWaitAsync(simulator, options, cancellationToken).ConfigureAwait(false);
            if (process is not null)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (process is not null)
                    await _launcher.RestoreProcessTuningAsync(process, CancellationToken.None).ConfigureAwait(false);
                await _optimizer.RestoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                process?.Dispose();
                IsRunning = false;
                _sessionGate.Release();
            }
        }
    }

    public Task RestoreRecoveryAsync(CancellationToken cancellationToken = default) => _optimizer.RestoreAsync(cancellationToken);
}
