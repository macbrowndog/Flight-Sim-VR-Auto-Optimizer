using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimVROptimizer.Core;

public sealed record NvidiaDlssPresetSetting(string Name, string Value, string Source);

public sealed record NvidiaDlssSettings(
    bool Available,
    string Profile,
    string DlssLibraryVersion,
    NvidiaDlssPresetSetting FrameGeneration,
    NvidiaDlssPresetSetting SuperResolution,
    NvidiaDlssPresetSetting RayReconstruction,
    string Status);

/// <summary>Reads NVIDIA driver-profile settings without changing the profile database.</summary>
public static class NvidiaDlssSettingsReader
{
    private const uint DlssFgOverride = 0x10E41E03;
    private const uint DlssFgPreset = 0x10E41DF1;
    private const uint DlssSrOverride = 0x10E41E01;
    private const uint DlssSrPreset = 0x10E41DF3;
    private const uint DlssRrOverride = 0x10E41E02;
    private const uint DlssRrPreset = 0x10E41DF7;

    public static NvidiaDlssSettings Read(string? simulatorId)
    {
        var unavailable = Setting("Frame Generation", "Unavailable", "NVIDIA driver");
        var dlssVersion = FindLoadedDlssVersion();
        if (!OperatingSystem.IsWindows())
            return new(false, "Unavailable", dlssVersion, unavailable,
                Setting("Super Resolution", "Unavailable", "NVIDIA driver"),
                Setting("Ray Reconstruction", "Unavailable", "NVIDIA driver"),
                "NVIDIA driver settings can only be read on Windows.");

        try
        {
            var settings = NvApiReader.Read(simulatorId, dlssVersion);
            if (IsUnavailableDlssVersion(settings.DlssLibraryVersion)
                && !settings.SuperResolution.Value.StartsWith("Use 3D app setting", StringComparison.OrdinalIgnoreCase))
            {
                var runtimeVersion = FindNvidiaRuntimeDlssVersion();
                if (!IsUnavailableDlssVersion(runtimeVersion))
                    settings = settings with { DlssLibraryVersion = runtimeVersion };
            }
            return settings;
        }
        catch (DllNotFoundException)
        {
            return new(false, "No NVIDIA profile", dlssVersion, unavailable,
                Setting("Super Resolution", "Unavailable", "NVIDIA driver"),
                Setting("Ray Reconstruction", "Unavailable", "NVIDIA driver"),
                "NVIDIA NVAPI is unavailable. An NVIDIA display driver was not detected.");
        }
        catch (Exception exception)
        {
            return new(false, "Unavailable", dlssVersion, unavailable,
                Setting("Super Resolution", "Unavailable", "NVIDIA driver"),
                Setting("Ray Reconstruction", "Unavailable", "NVIDIA driver"),
                $"NVIDIA profile settings could not be read: {exception.Message}");
        }
    }

    public static string FormatPreset(uint enabled, uint preset)
    {
        if (enabled == 0) return "Use 3D app setting";
        return preset switch
        {
            0 => "Recommended",
            0x00FFFFFE => "Recommended (Default)",
            0x00FFFFFF => "Recommended (Latest)",
            >= 1 and <= 26 => $"Preset {(char)('A' + preset - 1)}",
            _ => $"Custom (0x{preset:X8})"
        };
    }

    public static string FormatRenderingLabel(string antiAliasing, string dlssLibraryVersion)
    {
        if (!antiAliasing.Equals("DLSS", StringComparison.OrdinalIgnoreCase))
            return antiAliasing.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(dlssLibraryVersion)
            || dlssLibraryVersion.StartsWith("Not ", StringComparison.OrdinalIgnoreCase)
            || dlssLibraryVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || dlssLibraryVersion.Equals("Unavailable", StringComparison.OrdinalIgnoreCase))
            return "DLSS";

        var versionText = NormalizeVersion(dlssLibraryVersion.Trim());
        var metadataIndex = versionText.IndexOfAny([' ', '(', '+']);
        if (metadataIndex > 0) versionText = versionText[..metadataIndex];
        if (Version.TryParse(versionText.TrimStart('v', 'V'), out var version))
        {
            versionText = version.Revision >= 0
                ? version.ToString(4)
                : version.Build >= 0 ? version.ToString(3) : version.ToString();
        }
        return "DLSS v" + versionText.TrimStart('v', 'V');
    }

    private static NvidiaDlssPresetSetting Setting(string name, string value, string source) => new(name, value, source);

    private static bool IsUnavailableDlssVersion(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.StartsWith("Not ", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Unavailable", StringComparison.OrdinalIgnoreCase);

    private static string FindLoadedDlssVersion()
    {
        foreach (var processName in new[] { "FlightSimulator2024", "FlightSimulator" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        foreach (ProcessModule module in process.Modules)
                        {
                            // Check ModuleName first. Reading FileName for an unrelated protected
                            // module can throw and must not abort the entire simulator scan.
                            if (!string.Equals(module.ModuleName, "nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase))
                                continue;
                            try
                            {
                                var version = module.FileVersionInfo;
                                return NormalizeVersion(version.FileVersion ?? version.ProductVersion ?? "Unknown");
                            }
                            catch (Exception exception) when (exception is InvalidOperationException
                                or System.ComponentModel.Win32Exception
                                or System.UnauthorizedAccessException) { }
                        }
                    }
                    catch (Exception exception) when (exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception
                        or System.UnauthorizedAccessException) { }
                }
            }
        }
        return "Not loaded / start MSFS to read";
    }

    public static string ParseNgxDlssVersion(IEnumerable<string> lines)
    {
        var inDlssSection = false;
        Version? newestVersion = null;
        string? newestText = null;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inDlssSection = line.Equals("[dlss]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inDlssSection || line.StartsWith('#') || line.StartsWith(';')) continue;

            var separator = line.IndexOf('=');
            if (separator < 0 || !Version.TryParse(line[(separator + 1)..].Trim(), out var candidate))
                continue;
            if (newestVersion is null || candidate > newestVersion)
            {
                newestVersion = candidate;
                newestText = line[(separator + 1)..].Trim();
            }
        }
        return newestText ?? "Not available";
    }

    private static string NormalizeVersion(string value) => value.Replace(',', '.');

    private static string FindNvidiaRuntimeDlssVersion()
    {
        if (!OperatingSystem.IsWindows()) return "Not available";

        try
        {
            var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var modelsRoot = Path.Combine(commonData, "NVIDIA", "NGX", "models");
            if (!Directory.Exists(modelsRoot)) return "Not available";

            Version? newestVersion = null;
            string? newestText = null;
            var configPaths = new List<string>();
            var activeConfig = Path.Combine(modelsRoot, "nvngx_config.txt");
            if (File.Exists(activeConfig)) configPaths.Add(activeConfig);
            var serverConfigRoot = Path.Combine(modelsRoot, "config", "versions");
            if (Directory.Exists(serverConfigRoot))
                configPaths.AddRange(Directory.EnumerateFiles(
                    serverConfigRoot, "nvngx_server_config.txt", SearchOption.AllDirectories));

            foreach (var configPath in configPaths)
            {
                try
                {
                    var text = ParseNgxDlssVersion(File.ReadLines(configPath));
                    if (Version.TryParse(text, out var candidate)
                        && (newestVersion is null || candidate > newestVersion))
                    {
                        newestVersion = candidate;
                        newestText = text;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            return newestText ?? "Not available";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return "Not available";
    }

    private static class NvApiReader
    {
        private const int Ok = 0;
        private const uint NvApiInitializeId = 0x0150E828;
        private const uint DrsCreateSessionId = 0x0694D52E;
        private const uint DrsDestroySessionId = 0xDAD9CFF8;
        private const uint DrsLoadSettingsId = 0x375DBD6B;
        private const uint DrsFindApplicationByNameId = 0xEEE566B2;
        private const uint DrsGetCurrentGlobalProfileId = 0x617BFF9F;
        private const uint DrsGetSettingId = 0x73BF8338;
        private const int ApplicationV4Size = 20492;
        private const int SettingV1Size = 12320;
        private const int SettingLocationOffset = 4108;
        private const int CurrentDwordOffset = 8220;

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface(uint functionId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSessionDelegate(out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DestroySessionDelegate(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadSettingsDelegate(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private delegate int FindApplicationDelegate(IntPtr session, [MarshalAs(UnmanagedType.LPWStr)] string applicationName, out IntPtr profile, IntPtr application);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetGlobalProfileDelegate(IntPtr session, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSettingDelegate(IntPtr session, IntPtr profile, uint settingId, IntPtr setting);

        public static NvidiaDlssSettings Read(string? simulatorId, string dlssVersion)
        {
            var initialize = Function<InitializeDelegate>(NvApiInitializeId);
            Ensure(initialize(), "initialize NVAPI");
            var create = Function<CreateSessionDelegate>(DrsCreateSessionId);
            var destroy = Function<DestroySessionDelegate>(DrsDestroySessionId);
            Ensure(create(out var session), "create the NVIDIA profile session");
            try
            {
                Ensure(Function<LoadSettingsDelegate>(DrsLoadSettingsId)(session), "load NVIDIA profile settings");
                var (profile, profileName) = FindProfile(session, simulatorId);
                var get = Function<GetSettingDelegate>(DrsGetSettingId);
                var fg = ReadPreset(session, profile, get, "Frame Generation", DlssFgOverride, DlssFgPreset);
                var sr = ReadPreset(session, profile, get, "Super Resolution", DlssSrOverride, DlssSrPreset);
                var rr = ReadPreset(session, profile, get, "Ray Reconstruction", DlssRrOverride, DlssRrPreset);
                return new(true, profileName, dlssVersion, fg, sr, rr,
                    "Read-only values from the NVIDIA driver profile. No setting was changed.");
            }
            finally
            {
                destroy(session);
            }
        }

        private static (IntPtr Profile, string Name) FindProfile(IntPtr session, string? simulatorId)
        {
            var names = simulatorId?.Contains("2024", StringComparison.OrdinalIgnoreCase) == true
                ? new[] { "FlightSimulator2024.exe", "FlightSimulator.exe" }
                : new[] { "FlightSimulator.exe", "FlightSimulator2024.exe" };
            var find = Function<FindApplicationDelegate>(DrsFindApplicationByNameId);
            var application = Marshal.AllocHGlobal(ApplicationV4Size);
            try
            {
                Zero(application, ApplicationV4Size);
                Marshal.WriteInt32(application, ApplicationV4Size | (4 << 16));
                foreach (var name in names)
                {
                    if (find(session, name, out var profile, application) == Ok && profile != IntPtr.Zero)
                        return (profile, $"MSFS / {name}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(application);
            }

            Ensure(Function<GetGlobalProfileDelegate>(DrsGetCurrentGlobalProfileId)(session, out var global), "open the NVIDIA global profile");
            return (global, "NVIDIA global profile / MSFS profile not found");
        }

        private static NvidiaDlssPresetSetting ReadPreset(IntPtr session, IntPtr profile, GetSettingDelegate get,
            string name, uint enableId, uint presetId)
        {
            var enable = ReadDword(session, profile, get, enableId);
            var preset = ReadDword(session, profile, get, presetId);
            if (enable is null && preset is null)
                return Setting(name, "Use 3D app setting", "Driver default");

            var value = FormatPreset(enable?.Value ?? 0, preset?.Value ?? 0);
            var location = enable?.Location ?? preset?.Location ?? 3;
            var source = location == 0 ? "MSFS profile" : "Inherited / global profile";
            if (location != 0 && !value.StartsWith("Use 3D app setting", StringComparison.Ordinal))
                value = "Use global – " + value;
            return Setting(name, value, source);
        }

        private static (uint Value, int Location)? ReadDword(IntPtr session, IntPtr profile, GetSettingDelegate get, uint id)
        {
            var setting = Marshal.AllocHGlobal(SettingV1Size);
            try
            {
                Zero(setting, SettingV1Size);
                Marshal.WriteInt32(setting, SettingV1Size | (1 << 16));
                if (get(session, profile, id, setting) != Ok) return null;
                return (unchecked((uint)Marshal.ReadInt32(setting, CurrentDwordOffset)), Marshal.ReadInt32(setting, SettingLocationOffset));
            }
            finally
            {
                Marshal.FreeHGlobal(setting);
            }
        }

        private static T Function<T>(uint id) where T : Delegate
        {
            var pointer = QueryInterface(id);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException($"NVAPI function 0x{id:X8} is unavailable.");
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        private static void Ensure(int status, string action)
        {
            if (status != Ok) throw new InvalidOperationException($"Unable to {action} (NVAPI status {status}).");
        }

        private static void Zero(IntPtr pointer, int length) => Marshal.Copy(new byte[length], 0, pointer, length);
    }
}
