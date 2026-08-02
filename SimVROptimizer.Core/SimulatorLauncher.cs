using System.ComponentModel;
using System.Diagnostics;

namespace SimVROptimizer.Core;

public sealed class SimulatorLauncher
{
    private readonly FileLogger _logger;
    private readonly CpuOptimizer _cpuOptimizer = new();
    private readonly Dictionary<int, CpuOptimizationScope> _cpuScopes = [];

    public SimulatorLauncher(FileLogger logger) => _logger = logger;
    public event Action<string>? StatusChanged;

    public async Task<Process?> LaunchAndWaitAsync(
        SimulatorDefinition simulator,
        OptimizerOptions options,
        CancellationToken cancellationToken)
    {
        var existingProcesses = simulator.ProcessNames.SelectMany(Process.GetProcessesByName).ToArray();
        var existingPids = existingProcesses.Select(process => process.Id).ToHashSet();
        foreach (var existingProcess in existingProcesses) existingProcess.Dispose();
        var launchedAfterUtc = DateTime.UtcNow.AddSeconds(-2);

        await ReportAsync(options.DryRun
            ? $"Dry-run: would launch {simulator.Name}."
            : $"Launching {simulator.Name}.", cancellationToken);
        if (options.DryRun) return null;

        Start(simulator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(options.LaunchTimeoutSeconds, 30, 900)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                foreach (var process in simulator.ProcessNames.SelectMany(Process.GetProcessesByName))
                {
                    try
                    {
                        if (!existingPids.Contains(process.Id) && process.StartTime.ToUniversalTime() >= launchedAfterUtc)
                        {
                            await ReportAsync($"Detected new simulator process PID {process.Id}.", cancellationToken);
                            try
                            {
                                var scope = _cpuOptimizer.Apply(process, options);
                                _cpuScopes[process.Id] = scope;
                                await ReportAsync(scope.Summary, cancellationToken);
                            }
                            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or EntryPointNotFoundException)
                            {
                                await ReportAsync($"CPU optimization was skipped: {exception.Message}", cancellationToken);
                            }
                            return process;
                        }
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                    {
                        process.Dispose();
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{simulator.Name} did not create a new known process within {options.LaunchTimeoutSeconds} seconds.");
        }
    }

    public async Task RestoreProcessTuningAsync(Process process, CancellationToken cancellationToken = default)
    {
        if (!_cpuScopes.Remove(process.Id, out var scope)) return;
        try
        {
            _cpuOptimizer.Restore(process, scope);
            if (!process.HasExited) await ReportAsync("Original simulator priority and CPU-set assignment restored.", cancellationToken);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            await ReportAsync($"Could not restore simulator CPU settings: {exception.Message}", cancellationToken);
        }
    }

    private static void Start(SimulatorDefinition simulator)
    {
        ProcessStartInfo startInfo;
        if (simulator.LaunchKind == LaunchKind.Uri && simulator.LaunchTarget.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(simulator.LaunchTarget);
        }
        else
        {
            startInfo = new ProcessStartInfo(simulator.LaunchTarget) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(simulator.Arguments)) startInfo.Arguments = simulator.Arguments;
        }
        Process.Start(startInfo);
    }

    private async Task ReportAsync(string message, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(message);
        await _logger.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
