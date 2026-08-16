using Microsoft.Win32;

namespace SimVROptimizer.Core;

public static class OpenXrTurboLayer
{
    public const string RegistryPath = @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit";
    public const string ManifestFileName = "VR_Optimizer_Turbo_Layer.json";
    public const string LibraryFileName = "VR_Optimizer_Turbo_Layer.dll";

    public static string LayerDirectory => Path.Combine(AppContext.BaseDirectory, "Tools", "TurboLayer");
    public static string ManifestPath => Path.Combine(LayerDirectory, ManifestFileName);
    public static string LibraryPath => Path.Combine(LayerDirectory, LibraryFileName);
    public static bool IsPackageAvailable => File.Exists(ManifestPath) && File.Exists(LibraryPath);

    public static bool IsRegistered()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                .OpenSubKey(RegistryPath, writable: false);
            return key?.GetValueNames().Contains(ManifestPath, StringComparer.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }
}
