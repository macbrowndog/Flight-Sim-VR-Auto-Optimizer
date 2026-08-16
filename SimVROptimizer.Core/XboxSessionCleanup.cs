using System.Diagnostics;

namespace SimVROptimizer.Core;

public interface IXboxSessionCleanup
{
    event Action<string>? StatusChanged;
    Task CleanupAsync(CancellationToken cancellationToken = default);
}

public sealed class XboxSessionCleanup : IXboxSessionCleanup
{
    public static readonly IReadOnlyList<string> DefaultApplicationNames =
    [
        "XboxPcApp", "XboxGameBar", "XboxGameBarWidgets", "XboxGameCallableUI",
        "GameBar", "GameBarFTServer", "GameBarPresenceWriter",
        "GamingServices", "GamingServicesNet"
    ];

    public static readonly IReadOnlyList<string> DefaultServiceNames =
    [
        "GamingServices", "GamingServicesNet", "XblAuthManager", "XblGameSave",
        "XboxGipSvc", "XboxNetApiSvc"
    ];

    private readonly ICommandRunner _commands;
    private readonly FileLogger _logger;
    private readonly IReadOnlyList<string> _applicationNames;
    private readonly IReadOnlyList<string> _serviceNames;

    public XboxSessionCleanup(
        ICommandRunner commands,
        FileLogger logger,
        IReadOnlyList<string>? applicationNames = null,
        IReadOnlyList<string>? serviceNames = null)
    {
        _commands = commands;
        _logger = logger;
        _applicationNames = applicationNames ?? DefaultApplicationNames;
        _serviceNames = serviceNames ?? DefaultServiceNames;
    }

    public event Action<string>? StatusChanged;

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        await ReportAsync("MSFS has exited; closing Xbox session applications and services so the next launch starts cleanly.", cancellationToken).ConfigureAwait(false);
        await CloseApplicationsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var serviceName in _serviceNames)
            await StopServiceAsync(serviceName, cancellationToken).ConfigureAwait(false);

        // A Gaming Services process may reject termination while its service is active.
        // Retry application cleanup after the service stop requests have completed.
        await CloseApplicationsAsync(cancellationToken).ConfigureAwait(false);

        await ReportAsync("Xbox post-flight cleanup complete. Service startup settings were not changed.", cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseApplicationsAsync(CancellationToken cancellationToken)
    {
        var foundAny = false;
        foreach (var name in _applicationNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    foundAny = true;
                    try { process.CloseMainWindow(); }
                    catch (InvalidOperationException) { }
                }
            }
        }

        if (foundAny) await Task.Delay(750, cancellationToken).ConfigureAwait(false);

        foreach (var name in _applicationNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        await ReportAsync($"Xbox application {name} could not be closed: {exception.Message}", cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private async Task StopServiceAsync(string serviceName, CancellationToken cancellationToken)
    {
        var query = await _commands.RunAsync("sc.exe", ["query", serviceName], cancellationToken).ConfigureAwait(false);
        if (!query.Succeeded || !OutputParsers.IsServiceRunning(query.StandardOutput)) return;

        var stop = await _commands.RunAsync("sc.exe", ["stop", serviceName], cancellationToken).ConfigureAwait(false);
        if (!stop.Succeeded)
        {
            var detail = string.IsNullOrWhiteSpace(stop.StandardError) ? stop.StandardOutput : stop.StandardError;
            await ReportAsync($"Xbox service {serviceName} could not be stopped: {detail.Trim()}", cancellationToken).ConfigureAwait(false);
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var afterStop = await _commands.RunAsync("sc.exe", ["query", serviceName], cancellationToken).ConfigureAwait(false);
            if (!afterStop.Succeeded || !OutputParsers.IsServiceRunning(afterStop.StandardOutput))
            {
                await ReportAsync($"Stopped Xbox service {serviceName} after MSFS exit.", cancellationToken).ConfigureAwait(false);
                return;
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        await ReportAsync($"Xbox service {serviceName} remained active or was restarted by Windows.", cancellationToken).ConfigureAwait(false);
    }

    private async Task ReportAsync(string message, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(message);
        await _logger.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
