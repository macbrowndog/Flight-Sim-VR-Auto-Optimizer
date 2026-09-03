using System.Text.RegularExpressions;

namespace SimVROptimizer.Core;

public static partial class OutputParsers
{
    [GeneratedRegex(@"(?i)([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})")]
    private static partial Regex GuidRegex();

    public static string? ParsePowerPlanGuid(string output) => GuidRegex().Match(output) is { Success: true } match
        ? match.Groups[1].Value.ToLowerInvariant()
        : null;

    public static bool IsServiceRunning(string output) =>
        Regex.IsMatch(output, @"(?im)STATE\s*:\s*4\s+RUNNING");

    public static bool IsServiceStoppedCleanly(string output) =>
        Regex.IsMatch(output, @"(?im)STATE\s*:\s*1\s+STOPPED")
        && Regex.IsMatch(output, @"(?im)WIN32_EXIT_CODE\s*:\s*0(?:\s|$)")
        && Regex.IsMatch(output, @"(?im)SERVICE_EXIT_CODE\s*:\s*0(?:\s|$)");

    public static IReadOnlyDictionary<string, bool> ParseNvidiaPersistence(string output)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0])) continue;
            result[parts[0]] = parts[1].Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                || parts[1].Equals("On", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    public static IReadOnlyList<string> ParseRunningServices(string output) =>
        Regex.Matches(output, @"(?im)^SERVICE_NAME:\s*(?<name>[^\r\n]+?)\s*$")
            .Select(match => match.Groups["name"].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
