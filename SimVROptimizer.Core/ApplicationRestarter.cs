using System.Diagnostics;

namespace SimVROptimizer.Core;

public interface IApplicationRestarter
{
    bool IsRunning(string processName);
    Task<bool> RestartAndVerifyAsync(string processName, string restartCommand, CancellationToken cancellationToken);
}

public sealed class ApplicationRestarter : IApplicationRestarter
{
    public bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        var running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return running;
    }

    public async Task<bool> RestartAndVerifyAsync(string processName, string restartCommand, CancellationToken cancellationToken)
    {
        var launchTarget = ResolveLaunchTarget(processName, restartCommand);
        if (launchTarget is null) return false;

        // The optimizer normally runs elevated. Route relaunches through Explorer so
        // applications return to the user's standard desktop session.
        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add(launchTarget);
        using (Process.Start(startInfo)) { }

        for (var attempt = 0; attempt < 15; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRunning(processName)) return true;
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static string? ResolveLaunchTarget(string processName, string restartCommand)
    {
        if (restartCommand.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            var shellTarget = restartCommand[6..].Trim();
            if (shellTarget.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return shellTarget;
        }

        if (restartCommand.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
        {
            var recordedPath = restartCommand[4..].Trim();
            if (File.Exists(recordedPath)) return recordedPath;
        }

        if (!processName.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)) return null;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
