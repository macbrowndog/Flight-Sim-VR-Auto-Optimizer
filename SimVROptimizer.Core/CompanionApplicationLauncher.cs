using System.ComponentModel;
using System.Diagnostics;

namespace SimVROptimizer.Core;

public sealed class CompanionApplicationSession : IDisposable
{
    internal List<(CompanionApplicationRule Rule, Process Process)> StartedProcesses { get; } = [];
    public int StartedProcessCount => StartedProcesses.Count;

    public void Dispose()
    {
        foreach (var (_, process) in StartedProcesses) process.Dispose();
        StartedProcesses.Clear();
    }
}

public sealed class CompanionApplicationLauncher
{
    private readonly FileLogger _logger;
    private readonly ProcessWindowMinimizer _windowMinimizer = new();

    public CompanionApplicationLauncher(FileLogger logger) => _logger = logger;

    public event Action<string>? StatusChanged;

    public async Task<bool> WaitUntilReadyToFlyAsync(
        Process simulator,
        bool isMicrosoftFlightSimulator,
        TimeSpan fallbackTimeout,
        CancellationToken cancellationToken)
    {
        fallbackTimeout = TimeSpan.FromSeconds(Math.Clamp(fallbackTimeout.TotalSeconds, 60, 900));
        var unavailableReason = "SimConnect is not available";
        SimConnectFpsSource? source = null;
        if (!isMicrosoftFlightSimulator
            || !SimConnectFpsSource.TryCreate(simulator, _logger, out source, out unavailableReason)
            || source is null)
        {
            var reason = isMicrosoftFlightSimulator
                ? unavailableReason
                : "this simulator does not expose MSFS SimConnect FlightLoaded";
            await ReportAsync($"Ready-to-fly detection unavailable ({reason}); using the {fallbackTimeout.TotalSeconds:0}-second fallback.", cancellationToken).ConfigureAwait(false);
            return await WaitForFallbackOrExitAsync(simulator, fallbackTimeout, cancellationToken).ConfigureAwait(false);
        }

        await using (source)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.FlightLoaded += () => ready.TrySetResult();
            source.Start();
            await ReportAsync($"Waiting for MSFS SimConnect FlightLoaded before starting ready-to-fly companion apps (fallback {fallbackTimeout.TotalSeconds:0} seconds).", cancellationToken).ConfigureAwait(false);
            var timeout = Task.Delay(fallbackTimeout, cancellationToken);
            var simulatorExited = simulator.WaitForExitAsync(cancellationToken);
            var completed = await Task.WhenAny(ready.Task, timeout, simulatorExited).ConfigureAwait(false);
            if (completed == ready.Task)
            {
                await ReportAsync("MSFS reports FlightLoaded; starting ready-to-fly companion apps.", cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (completed == simulatorExited)
            {
                await ReportAsync("Simulator exited before ready-to-fly companion apps were launched.", CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            await ReportAsync("Ready-to-fly wait reached its fallback timeout; starting configured companion apps.", cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    public async Task LaunchAsync(
        IEnumerable<CompanionApplicationRule> rules,
        CompanionLaunchTiming timing,
        CompanionApplicationSession session,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        foreach (var rule in rules.Where(rule => rule.Enabled && rule.LaunchTiming == timing))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Environment.ExpandEnvironmentVariables(rule.ExecutablePath.Trim().Trim('"'));
            var name = DisplayName(rule, path);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                await ReportAsync($"Companion app skipped: {name} executable was not found at '{path}'.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var processName = Path.GetFileNameWithoutExtension(path);
            if (IsProcessRunning(processName))
            {
                await ReportAsync($"Companion app already running: {name}; no duplicate was launched.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (dryRun)
            {
                await ReportAsync($"Dry-run: would launch companion app {name} ({timing}).", cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var delay = Math.Clamp(rule.LaunchDelaySeconds, 0, 300);
                if (delay > 0)
                {
                    await ReportAsync($"Waiting {delay} second(s) before launching {name}.", cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
                }
                var startInfo = new ProcessStartInfo(path)
                {
                    WorkingDirectory = Path.GetDirectoryName(path) ?? "",
                    UseShellExecute = true
                };
                if (rule.RunAsAdministrator) startInfo.Verb = "runas";
                var process = Process.Start(startInfo);
                if (process is null)
                {
                    await ReportAsync($"Companion app did not return a process: {name}.", cancellationToken).ConfigureAwait(false);
                    continue;
                }
                session.StartedProcesses.Add((rule, process));
                await ReportAsync($"Launched companion app {name} ({timing}; PID {process.Id}; administrator: {(rule.RunAsAdministrator ? "requested" : "inherit")}).", cancellationToken).ConfigureAwait(false);
                if (rule.MinimizeAfterLaunch)
                    _ = MinimizeAfterLaunchAsync(process, name, cancellationToken);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                await ReportAsync($"Companion app could not be launched: {name}: {exception.Message}", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task MinimizeAfterLaunchAsync(Process process, string name, CancellationToken cancellationToken)
    {
        try
        {
            var minimized = await _windowMinimizer.MinimizeWhenReadyAsync(process, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            await ReportAsync(minimized
                ? $"Minimized companion app after launch: {name}."
                : $"Companion app {name} did not expose a normal window to minimize within 20 seconds.", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task CleanupAsync(CompanionApplicationSession? session, CancellationToken cancellationToken)
    {
        if (session is null) return;
        foreach (var (rule, process) in session.StartedProcesses.AsEnumerable().Reverse())
        {
            if (rule.CleanupAction != CompanionCleanupAction.CloseOnSessionEnd) continue;
            var name = DisplayName(rule, rule.ExecutablePath);
            try
            {
                if (process.HasExited) continue;
                await ReportAsync($"Closing companion app {name}.", cancellationToken).ConfigureAwait(false);
                if (process.CloseMainWindow())
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* fall through to the safe forced close */ }
                }
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                await ReportAsync($"Companion app closed: {name}.", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                await ReportAsync($"Companion app cleanup warning for {name}: {exception.Message}", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ReportAsync(string message, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(message);
        await _logger.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private static string DisplayName(CompanionApplicationRule rule, string path) =>
        string.IsNullOrWhiteSpace(rule.Name) ? Path.GetFileNameWithoutExtension(path) : rule.Name.Trim();

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    private static async Task<bool> WaitForFallbackOrExitAsync(Process simulator, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var delay = Task.Delay(timeout, cancellationToken);
        var exited = simulator.WaitForExitAsync(cancellationToken);
        var completed = await Task.WhenAny(delay, exited).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return completed == delay;
    }
}
