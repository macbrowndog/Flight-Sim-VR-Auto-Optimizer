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
        "GameBar", "GameBarFTServer", "GameBarPresenceWriter"
    ];

    private readonly FileLogger _logger;
    private readonly IReadOnlyList<string> _applicationNames;

    public XboxSessionCleanup(
        FileLogger logger,
        IReadOnlyList<string>? applicationNames = null)
    {
        _logger = logger;
        _applicationNames = applicationNames ?? DefaultApplicationNames;
    }

    public event Action<string>? StatusChanged;

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        await ReportAsync("MSFS has exited; closing Xbox and Game Bar interface processes while leaving Gaming Services and Xbox online services available.", cancellationToken).ConfigureAwait(false);
        await CloseApplicationsAsync(cancellationToken).ConfigureAwait(false);
        await ReportAsync("Xbox post-flight cleanup complete. Gaming Services, authentication, game-save and networking services were left untouched.", cancellationToken).ConfigureAwait(false);
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

    private async Task ReportAsync(string message, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(message);
        await _logger.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
