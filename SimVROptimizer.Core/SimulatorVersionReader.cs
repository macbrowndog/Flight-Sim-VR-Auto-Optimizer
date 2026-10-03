using System.ComponentModel;
using System.Diagnostics;

namespace SimVROptimizer.Core;

/// <summary>
/// Resolves a simulator version without relying solely on executable access.
/// Microsoft Store executables can be protected by WindowsApps permissions, so
/// their installed package version is preferred.
/// </summary>
public sealed class SimulatorVersionReader(ICommandRunner commands)
{
    public const string Unknown = "Unknown";

    public async Task<string> ReadAsync(int processId, string? simulatorId,
        CancellationToken cancellationToken = default)
    {
        var packageName = StorePackageName(simulatorId);
        if (packageName is not null)
        {
            var packageVersion = await ReadStorePackageVersionAsync(packageName, cancellationToken)
                .ConfigureAwait(false);
            if (!packageVersion.Equals(Unknown, StringComparison.OrdinalIgnoreCase))
                return packageVersion;
        }

        return ReadProcessVersion(processId);
    }

    private async Task<string> ReadStorePackageVersionAsync(string packageName,
        CancellationToken cancellationToken)
    {
        var command =
            $"$package = Get-AppxPackage -Name '{packageName}' -ErrorAction SilentlyContinue | " +
            "Sort-Object Version -Descending | Select-Object -First 1; " +
            "if ($package) { $package.Version.ToString() }";
        try
        {
            var result = await commands.RunAsync("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", command], cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded) return Unknown;
            foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Trim();
                if (Version.TryParse(candidate, out var version)) return version.ToString();
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Fall through to executable metadata when PowerShell is unavailable.
        }
        return Unknown;
    }

    public static string ReadProcessVersion(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var information = process.MainModule?.FileVersionInfo;
            return First(information?.ProductVersion, information?.FileVersion) ?? Unknown;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
                                          or NotSupportedException or ArgumentException)
        {
            return Unknown;
        }
    }

    private static string? StorePackageName(string? simulatorId) => simulatorId?.ToLowerInvariant() switch
    {
        "msfs2024-store" => "Microsoft.Limitless",
        "msfs2020-store" => "Microsoft.FlightSimulator",
        _ => null
    };

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
