using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace SimVROptimizer.Core;

public enum DiagnosticHealth
{
    Ready,
    Review,
    Problem
}

public sealed record OpenXrLayerInfo(string Name, string ManifestPath, string Scope);
public sealed record VrDisplayDiagnostic(string Headset, string RefreshRate, string RenderScale, string MotionReprojection, string Source);
public sealed record VrRuntimeAlignment(DiagnosticHealth Health, string Detail);

public sealed record OpenXrDiagnosticReport(
    DiagnosticHealth Health,
    string Summary,
    string RuntimeName,
    string RuntimeManifest,
    string ApiVersion,
    VrDisplayDiagnostic Display,
    IReadOnlyList<string> RunningComponents,
    IReadOnlyList<OpenXrLayerInfo> ActiveImplicitLayers,
    IReadOnlyList<string> EnvironmentOverrides);

/// <summary>Inspects OpenXR registrations and saved diagnostics without loading or touching the active runtime.</summary>
public static class OpenXrDiagnostics
{
    public const string RuntimeRegistryPath = @"SOFTWARE\Khronos\OpenXR\1";
    public const string ImplicitLayersRegistryPath = @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit";

    private static readonly string[] RuntimeProcesses =
    [
        "vrserver", "vrmonitor", "VirtualDesktop.Streamer", "PimaxClient", "PimaxPlay",
        "PiServer", "OVRServer_x64", "MixedRealityRuntime", "WindowsMixedReality"
    ];

    public static OpenXrDiagnosticReport Read()
    {
        if (!OperatingSystem.IsWindows())
            return new(DiagnosticHealth.Problem, "OpenXR diagnostics are available only on Windows.",
                "Unavailable", "—", "—", new("—", "—", "—", "—", "Unavailable"), [], [], []);

        var forcedRuntime = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
        var registeredRuntime = ReadString(RegistryHive.LocalMachine, RuntimeRegistryPath, "ActiveRuntime");
        var runtimePath = string.IsNullOrWhiteSpace(forcedRuntime) ? registeredRuntime : forcedRuntime;
        var overrides = ReadOverrides(forcedRuntime);
        var layers = ReadImplicitLayers();
        var running = RuntimeProcesses.Where(IsRunning).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var (runtimeName, apiVersion) = ReadRuntimeManifest(runtimePath);
        if (IsPimaxRuntime(runtimeName, runtimePath) && apiVersion == "Not declared")
            apiVersion = "Not declared by Pimax manifest";
        var display = ReadDisplaySettings(runtimeName, runtimePath);

        DiagnosticHealth health;
        string summary;
        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            health = DiagnosticHealth.Problem;
            summary = "No active OpenXR runtime is registered.";
        }
        else if (!File.Exists(runtimePath))
        {
            health = DiagnosticHealth.Problem;
            summary = "The active OpenXR runtime manifest does not exist.";
        }
        else if (overrides.Count > 0 || layers.Count > 1)
        {
            health = DiagnosticHealth.Review;
            summary = overrides.Count > 0
                ? "OpenXR environment overrides are active; review them before troubleshooting runtime selection."
                : "Multiple implicit OpenXR layers are active; review frame-pacing or overlay layers if VR is unstable.";
        }
        else
        {
            health = DiagnosticHealth.Ready;
            summary = "The active OpenXR runtime registration and manifest are available.";
        }

        return new(health, summary, runtimeName, runtimePath ?? "—", apiVersion, display, running, layers, overrides);
    }

    public static (string Name, string ApiVersion) ParseRuntimeManifest(string json, string fallbackName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var runtime = root.TryGetProperty("runtime", out var runtimeElement) ? runtimeElement : root;
            var name = ReadJsonString(runtime, "name") ?? ReadJsonString(root, "name") ?? fallbackName;
            var api = ReadJsonString(runtime, "api_version") ?? ReadJsonString(root, "api_version") ?? "Not declared";
            return (name, api);
        }
        catch (JsonException)
        {
            return (fallbackName, "Manifest unreadable");
        }
    }

    public static bool IsLayerEnabled(object? registryValue) => registryValue switch
    {
        int value => value == 0,
        uint value => value == 0,
        long value => value == 0,
        _ => false
    };

    public static VrRuntimeAlignment EvaluateRuntimeAlignment(VrRuntimePreference selected, string activeRuntimeName)
    {
        if (selected == VrRuntimePreference.None)
            return new(DiagnosticHealth.Ready, "AUTOMATIC LAUNCH OFF / the registered OpenXR runtime will be used.");

        var matches = selected switch
        {
            VrRuntimePreference.VirtualDesktop => activeRuntimeName.Contains("Virtual Desktop", StringComparison.OrdinalIgnoreCase)
                || activeRuntimeName.Contains("VDXR", StringComparison.OrdinalIgnoreCase),
            VrRuntimePreference.PimaxPlay => activeRuntimeName.Contains("Pimax", StringComparison.OrdinalIgnoreCase),
            VrRuntimePreference.SteamVR => activeRuntimeName.Contains("Steam", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        return matches
            ? new(DiagnosticHealth.Ready, $"MATCHED / {selected} agrees with active runtime {activeRuntimeName}.")
            : new(DiagnosticHealth.Review, $"REVIEW / {selected} is selected for launch, but {activeRuntimeName} is the active OpenXR runtime.");
    }

    public static VrDisplayDiagnostic ParseSteamVrSettings(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var headset = ReadNestedString(root, "LastKnown", "HMDModel") ?? "Not reported";
            if (!root.TryGetProperty("steamvr", out var steamVr) || steamVr.ValueKind != JsonValueKind.Object)
                return new(headset, "Not reported", "Not reported", "Not reported", "SteamVR saved settings");

            var refresh = ReadNestedNumber(steamVr, "preferredRefreshRate") ?? ReadNestedNumber(steamVr, "displayFrequency");
            var scale = ReadNestedNumber(steamVr, "supersampleScale");
            var motion = steamVr.TryGetProperty("motionSmoothing", out var motionValue)
                && motionValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? motionValue.GetBoolean() ? "ON" : "OFF"
                    : "Not reported";
            return new(headset,
                refresh.HasValue ? $"{refresh:0.##} Hz" : "Not reported",
                scale.HasValue ? $"{scale.Value * 100:0}%" : "Not reported",
                motion,
                "SteamVR saved settings");
        }
        catch (JsonException)
        {
            return new("Not reported", "Not reported", "Not reported", "Not reported", "SteamVR settings unreadable");
        }
    }

    public static string? ParsePimaxHeadsetLog(string text)
    {
        const string marker = "This device is ";
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;
            var name = line[(markerIndex + marker.Length)..].Trim().TrimEnd('.', ';');
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return null;
    }

    private static (string Name, string ApiVersion) ReadRuntimeManifest(string? path)
    {
        var fallback = GuessRuntimeName(path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return (fallback, "—");
        try { return ParseRuntimeManifest(File.ReadAllText(path), fallback); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (fallback, "Manifest unreadable");
        }
    }

    private static VrDisplayDiagnostic ReadDisplaySettings(string runtimeName, string? runtimePath)
    {
        if (IsPimaxRuntime(runtimeName, runtimePath))
        {
            var headset = ReadPimaxHeadsetFromLogs() ?? "Not recorded in Pimax device log";
            return new(headset, "Pimax Play setting", "Pimax Play setting", "Smart Smoothing / Pimax Play",
                "Passive Pimax manifest and device-log inspection only; the active runtime is never loaded by diagnostics");
        }

        if (!runtimeName.Contains("Steam", StringComparison.OrdinalIgnoreCase)
            && !(runtimePath?.Contains("steam", StringComparison.OrdinalIgnoreCase) ?? false))
            return new("Not exposed", "Not exposed", "Not exposed", "Not exposed", "Active runtime does not publish these values for external read-only inspection");

        foreach (var path in SteamVrSettingsCandidates())
        {
            if (!File.Exists(path)) continue;
            try { return ParseSteamVrSettings(File.ReadAllText(path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return new("Not reported", "Not reported", "Not reported", "Not reported", "SteamVR settings file not found");
    }

    private static string? ReadPimaxHeadsetFromLogs()
    {
        try
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pimax", "slam");
            if (!Directory.Exists(logDirectory)) return null;
            foreach (var path in Directory.EnumerateFiles(logDirectory, "6DOF_*.txt")
                         .OrderByDescending(File.GetLastWriteTimeUtc).Take(8))
            {
                try
                {
                    var detected = ParsePimaxHeadsetLog(File.ReadAllText(path));
                    if (!string.IsNullOrWhiteSpace(detected)) return detected;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsPimaxRuntime(string runtimeName, string? runtimePath) =>
        runtimeName.Contains("Pimax", StringComparison.OrdinalIgnoreCase)
        || (runtimePath?.Contains("pimax", StringComparison.OrdinalIgnoreCase) ?? false);

    private static IEnumerable<string> SteamVrSettingsCandidates()
    {
        var candidates = new List<string>();
        var steamPath = ReadString(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(steamPath)) candidates.Add(Path.Combine(steamPath, "config", "steamvr.vrsettings"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "config", "steamvr.vrsettings"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "config", "steamvr.vrsettings"));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<OpenXrLayerInfo> ReadImplicitLayers()
    {
        var layers = new List<OpenXrLayerInfo>();
        ReadLayerKey(RegistryHive.LocalMachine, "MACHINE", layers);
        ReadLayerKey(RegistryHive.CurrentUser, "USER", layers);
        return layers.DistinctBy(layer => layer.ManifestPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ReadLayerKey(RegistryHive hive, string scope, ICollection<OpenXrLayerInfo> layers)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ImplicitLayersRegistryPath);
            if (key is null) return;
            foreach (var valueName in key.GetValueNames())
            {
                if (!IsLayerEnabled(key.GetValue(valueName))) continue;
                layers.Add(new(GuessLayerName(valueName), valueName, scope));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static string? ReadString(RegistryHive hive, string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static List<string> ReadOverrides(string? forcedRuntime)
    {
        var values = new List<string>();
        Add("XR_RUNTIME_JSON", forcedRuntime);
        Add("XR_API_LAYER_PATH", Environment.GetEnvironmentVariable("XR_API_LAYER_PATH"));
        Add("XR_ENABLE_API_LAYERS", Environment.GetEnvironmentVariable("XR_ENABLE_API_LAYERS"));
        return values;

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values.Add($"{name}={value}");
        }
    }

    private static bool IsRunning(string processName)
    {
        try
        {
            var processes = Process.GetProcessesByName(processName);
            try { return processes.Length > 0; }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private static string GuessRuntimeName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Not detected";
        if (path.Contains("steam", StringComparison.OrdinalIgnoreCase)) return "SteamVR";
        if (path.Contains("oculus", StringComparison.OrdinalIgnoreCase)) return "Meta / Oculus";
        if (path.Contains("pimax", StringComparison.OrdinalIgnoreCase)) return "Pimax OpenXR";
        if (path.Contains("virtualdesktop", StringComparison.OrdinalIgnoreCase)) return "Virtual Desktop OpenXR";
        if (path.Contains("mixedreality", StringComparison.OrdinalIgnoreCase)) return "Windows Mixed Reality";
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string GuessLayerName(string path)
    {
        if (path.Contains("VR_Optimizer_Turbo", StringComparison.OrdinalIgnoreCase)) return "VR Optimizer Turbo";
        if (path.Contains("openxr_toolkit", StringComparison.OrdinalIgnoreCase)) return "OpenXR Toolkit";
        if (path.Contains("quad", StringComparison.OrdinalIgnoreCase)) return "Quad-Views / foveated rendering";
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string? ReadJsonString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(objectName, out var nested)
        ? ReadJsonString(nested, propertyName)
        : null;

    private static double? ReadNestedNumber(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetDouble(out var value)
            ? value
            : null;
}
