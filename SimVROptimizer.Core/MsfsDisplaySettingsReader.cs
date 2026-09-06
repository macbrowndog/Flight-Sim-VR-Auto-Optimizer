using System.Text.RegularExpressions;

namespace SimVROptimizer.Core;

public sealed record MsfsGraphicsDisplaySetting(
    string AntiAliasing,
    string DlssMode,
    string GraphicsVersion,
    string Preset);

public sealed record MsfsDisplaySettings(
    string ConfigPath,
    string UserConfigVersion,
    MsfsGraphicsDisplaySetting Desktop,
    MsfsGraphicsDisplaySetting Vr);

public static partial class MsfsDisplaySettingsReader
{
    public static MsfsDisplaySettings? ReadForSimulator(string? simulatorId)
    {
        if (string.IsNullOrWhiteSpace(simulatorId)
            || !simulatorId.StartsWith("msfs", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var path in CandidatePaths(simulatorId))
        {
            if (!File.Exists(path)) continue;
            try { return Parse(File.ReadAllText(path), path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static MsfsDisplaySettings Parse(string content, string configPath = "UserCfg.opt")
    {
        var desktop = ParseGraphics(content, vr: false);
        var vr = ParseGraphics(content, vr: true);
        return new MsfsDisplaySettings(
            configPath,
            Value(content, "Version", "Unknown"),
            desktop,
            vr);
    }

    private static MsfsGraphicsDisplaySetting ParseGraphics(string content, bool vr)
    {
        var sectionName = vr ? "GraphicsVR" : "Graphics";
        var section = SectionHeaderRegex(sectionName).Match(content);
        var version = section.Success ? section.Groups["version"].Value : "Unknown";
        var preset = section.Success ? FormatPreset(section.Groups["preset"].Value) : "Unknown";
        return new MsfsGraphicsDisplaySetting(
            Value(content, vr ? "AntiAliasingVR" : "AntiAliasing", "Unknown").ToUpperInvariant(),
            Value(content, vr ? "DLSSModeVR" : "DLSSMode", "Unknown").ToUpperInvariant(),
            version,
            preset);
    }

    private static string Value(string content, string key, string fallback)
    {
        var match = Regex.Match(
            content,
            $@"(?m)^\s*{Regex.Escape(key)}\s+(?<value>[^\r\n]+?)\s*$",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Trim().Trim('"') : fallback;
    }

    private static Regex SectionHeaderRegex(string sectionName) => new(
        $@"(?m)^\s*\{{{Regex.Escape(sectionName)}\s*\r?\n\s*Version\s+(?<version>\S+)\s*\r?\n\s*Preset\s+(?<preset>\S+)",
        RegexOptions.CultureInvariant);

    private static string FormatPreset(string value) => value switch
    {
        "VRLow" => "VR Low",
        "VRMedium" => "VR Medium",
        "VRHigh" => "VR High",
        "VRUltra" => "VR Ultra",
        _ => value
    };

    private static IEnumerable<string> CandidatePaths(string simulatorId)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var is2024 = simulatorId.Contains("2024", StringComparison.OrdinalIgnoreCase);
        var isStore = simulatorId.Contains("store", StringComparison.OrdinalIgnoreCase);

        if (is2024 && isStore)
            yield return Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");
        if (is2024)
            yield return Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt");
        if (!is2024 && isStore)
            yield return Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");
        if (!is2024)
            yield return Path.Combine(roaming, "Microsoft Flight Simulator", "UserCfg.opt");
    }
}
