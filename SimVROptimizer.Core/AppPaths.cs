namespace SimVROptimizer.Core;

public sealed class AppPaths
{
    public AppPaths(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SimVROptimizer");
    }

    public string BaseDirectory { get; }
    public string ConfigFile => Path.Combine(BaseDirectory, "config.json");
    public string JournalFile => Path.Combine(BaseDirectory, "active-session.json");
    public string PendingLaunchFile => Path.Combine(BaseDirectory, "pending-launch.json");
    public string RestorationReportFile => Path.Combine(BaseDirectory, "last-restoration-report.json");
    public string LogFile => Path.Combine(BaseDirectory, "optimizer.log");
    public string TelemetryDirectory => Path.Combine(BaseDirectory, "Telemetry");

    public void EnsureCreated() => Directory.CreateDirectory(BaseDirectory);
}
