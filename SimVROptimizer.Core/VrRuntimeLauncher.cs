using System.Diagnostics;
using Microsoft.Win32;

namespace SimVROptimizer.Core;

public sealed record VrRuntimeSession(VrRuntimePreference Runtime, IReadOnlyList<string> ProcessNames, bool StartedByOptimizer);
public sealed record VrRuntimeShutdownPolicy(TimeSpan GracefulTimeout, bool AllowForcedTermination, string Method);

public sealed class VrRuntimeLauncher
{
    private sealed record RuntimeDefinition(
        VrRuntimePreference Runtime,
        string DisplayName,
        IReadOnlyList<string> ProcessNames,
        string? Uri,
        IReadOnlyList<string> ExecutableCandidates);

    private static readonly IReadOnlyDictionary<VrRuntimePreference, RuntimeDefinition> Definitions =
        new Dictionary<VrRuntimePreference, RuntimeDefinition>
        {
            [VrRuntimePreference.VirtualDesktop] = new(
                VrRuntimePreference.VirtualDesktop,
                "Virtual Desktop Streamer",
                ["VirtualDesktop.Streamer"],
                null,
                [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer", "VirtualDesktop.Streamer.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Virtual Desktop Streamer", "VirtualDesktop.Streamer.exe")
                ]),
            [VrRuntimePreference.PimaxPlay] = new(
                VrRuntimePreference.PimaxPlay,
                "Pimax Play",
                ["PimaxPlay", "PimaxClient", "PiTool"],
                null,
                [
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pimax", "PimaxClient", "pimaxui", "PimaxClient.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Pimax", "PimaxClient", "pimaxui", "PimaxClient.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pimax", "Pimax Play", "PimaxPlay.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pimax", "PimaxPlay", "PimaxPlay.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pimax", "Runtime", "PimaxClient.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pimax", "Runtime", "launcher.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pimax", "PimaxPlay", "PimaxPlay.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Pimax", "PimaxPlay.exe")
                ]),
            [VrRuntimePreference.SteamVR] = new(
                VrRuntimePreference.SteamVR,
                "SteamVR",
                ["vrmonitor", "vrserver"],
                "steam://run/250820",
                [])
        };

    private readonly FileLogger _logger;
    public VrRuntimeLauncher(FileLogger logger) => _logger = logger;
    public event Action<string>? StatusChanged;

    public VrRuntimeAvailability CheckAvailability(VrRuntimePreference runtime)
    {
        if (runtime == VrRuntimePreference.None)
            return new(runtime, true, false, "No automatic VR runtime is selected.");

        if (!Definitions.TryGetValue(runtime, out var definition))
            return new(runtime, false, false, $"Unsupported VR runtime selection: {runtime}.");

        if (IsAnyRunning(definition.ProcessNames))
            return new(runtime, true, true, $"{definition.DisplayName} is already running and will be left running after the session.");

        if (definition.ExecutableCandidates.Any(File.Exists))
            return new(runtime, true, false, $"{definition.DisplayName} launcher was found and is ready to start.");

        if (definition.Uri is not null && IsUriProtocolRegistered(definition.Uri))
            return new(runtime, true, false, $"{definition.DisplayName} is available through its registered launcher.");

        return new(runtime, false, false, $"{definition.DisplayName} was selected but its launcher could not be found. Start it manually or select None.");
    }

    public async Task<VrRuntimeSession?> LaunchAsync(VrRuntimePreference runtime, CancellationToken cancellationToken)
    {
        if (runtime == VrRuntimePreference.None)
        {
            await ReportAsync("VR runtime: no automatic runtime selected.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (!Definitions.TryGetValue(runtime, out var definition))
            throw new InvalidOperationException($"Unsupported VR runtime selection: {runtime}.");

        if (IsAnyRunning(definition.ProcessNames))
        {
            await ReportAsync($"{definition.DisplayName} is already running; it will be left running after the session.", cancellationToken).ConfigureAwait(false);
            return new VrRuntimeSession(runtime, definition.ProcessNames, false);
        }

        if (definition.Uri is not null)
        {
            Process.Start(new ProcessStartInfo(definition.Uri) { UseShellExecute = true });
        }
        else
        {
            var executable = definition.ExecutableCandidates.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException($"{definition.DisplayName} was selected but its launcher could not be found. Start it manually or select None.");
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }

        await ReportAsync($"Launching {definition.DisplayName}.", cancellationToken).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            while (!IsAnyRunning(definition.ProcessNames))
                await Task.Delay(750, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{definition.DisplayName} did not become ready within 45 seconds.");
        }

        await ReportAsync($"{definition.DisplayName} is ready.", cancellationToken).ConfigureAwait(false);
        return new VrRuntimeSession(runtime, definition.ProcessNames, true);
    }

    public async Task RestoreAsync(VrRuntimeSession? session, CancellationToken cancellationToken = default)
    {
        if (session is null || !session.StartedByOptimizer) return;

        if (session.Runtime == VrRuntimePreference.SteamVR)
        {
            await ShutdownSteamVrGracefullyAsync(session, cancellationToken).ConfigureAwait(false);
            return;
        }

        var processes = session.ProcessNames.SelectMany(Process.GetProcessesByName).ToArray();
        foreach (var process in processes)
        {
            using (process)
            {
                try { process.CloseMainWindow(); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        foreach (var process in session.ProcessNames.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        await ReportAsync($"{session.Runtime} was closed because the optimizer started it.", cancellationToken).ConfigureAwait(false);
    }

    public static VrRuntimeShutdownPolicy GetShutdownPolicy(VrRuntimePreference runtime) =>
        runtime == VrRuntimePreference.SteamVR
            ? new(TimeSpan.FromSeconds(30), false, "SteamVR graceful -shutdown command")
            : new(TimeSpan.FromMilliseconds(750), true, "Close window, then terminate remaining processes");

    private async Task ShutdownSteamVrGracefullyAsync(VrRuntimeSession session, CancellationToken cancellationToken)
    {
        var policy = GetShutdownPolicy(VrRuntimePreference.SteamVR);
        var monitorPath = FindRunningProcessPath("vrmonitor");

        await ReportAsync(
            "Requesting a graceful SteamVR shutdown so Bluetooth base-station standby can complete.",
            cancellationToken).ConfigureAwait(false);

        var requestSent = false;
        if (!string.IsNullOrWhiteSpace(monitorPath))
        {
            try
            {
                using var command = Process.Start(new ProcessStartInfo(monitorPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = "-shutdown"
                });
                requestSent = command is not null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                await ReportAsync($"SteamVR shutdown command could not be started: {exception.Message}", cancellationToken).ConfigureAwait(false);
            }
        }

        if (!requestSent)
        {
            foreach (var process in Process.GetProcessesByName("vrmonitor"))
            {
                using (process)
                {
                    try { requestSent |= process.CloseMainWindow(); }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
        }

        var stopped = await WaitForExitAsync(session.ProcessNames, policy.GracefulTimeout, cancellationToken).ConfigureAwait(false);
        if (stopped)
        {
            await ReportAsync(
                "SteamVR completed its graceful shutdown; base-station standby was allowed to finish.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReportAsync(
            $"SteamVR did not finish shutting down within {policy.GracefulTimeout.TotalSeconds:0} seconds. It was left running instead of being force-closed, so base-station power management can continue.",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForExitAsync(
        IReadOnlyList<string> processNames,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsAnyRunning(processNames) && DateTime.UtcNow < deadline)
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        return !IsAnyRunning(processNames);
    }

    private static string? FindRunningProcessPath(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
        return null;
    }

    private static bool IsAnyRunning(IEnumerable<string> processNames)
    {
        foreach (var processName in processNames)
        {
            var processes = Process.GetProcessesByName(processName);
            try { if (processes.Length > 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    private static bool IsUriProtocolRegistered(string uri)
    {
        var separator = uri.IndexOf(':');
        if (separator <= 0) return false;
        var scheme = uri[..separator];
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key is not null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ReportAsync(string message, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(message);
        await _logger.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
