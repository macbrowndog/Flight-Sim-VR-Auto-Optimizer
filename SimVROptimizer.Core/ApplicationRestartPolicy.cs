namespace SimVROptimizer.Core;

public static class ApplicationRestartPolicy
{
    public static bool CanLaunchDirectly(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        var path = Environment.ExpandEnvironmentVariables(executablePath).Trim().Trim('"').Replace('/', '\\');
        return !path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("\\SystemApps\\", StringComparison.OrdinalIgnoreCase);
    }
}
