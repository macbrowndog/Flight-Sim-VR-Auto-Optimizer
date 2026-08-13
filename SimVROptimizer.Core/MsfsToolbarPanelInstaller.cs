using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimVROptimizer.Core;

public sealed record ToolbarPanelInstallationStatus(
    string? CommunityFolder,
    string? TargetDirectory,
    bool PackageAvailable,
    bool IsInstalled,
    string Detail);

public sealed class MsfsToolbarPanelInstaller
{
    public const string PackageName = "flightdecktools-vr-dashboard";
    private const string PackageTitle = "VR Optimizer";
    private const string LegacyPackageTitle = "FlightDeckTools VR Dashboard";

    private readonly string _packageSource;
    private readonly IReadOnlyList<string> _userConfigCandidates;

    public MsfsToolbarPanelInstaller(string packageSource, IEnumerable<string>? userConfigCandidates = null)
    {
        _packageSource = Path.GetFullPath(packageSource);
        _userConfigCandidates = (userConfigCandidates ?? DefaultUserConfigCandidates()).ToArray();
    }

    public ToolbarPanelInstallationStatus GetStatus()
    {
        var community = FindCommunityFolder();
        var target = community is null ? null : Path.Combine(community, PackageName);
        var available = IsCompletePackage(_packageSource);
        var installed = target is not null && IsOurPackage(target);
        var detail = community is null
            ? "MSFS 2024 Community folder was not found. Start MSFS 2024 once, then try again."
            : installed
                ? "VR Dashboard toolbar package is installed. Restart MSFS after updates or removal."
                : available
                    ? "VR Dashboard toolbar package is ready to install. MSFS must be restarted after installation."
                    : "The compiled VR Dashboard package is not included in this build.";
        return new(community, target, available, installed, detail);
    }

    public async Task<ToolbarPanelInstallationStatus> InstallAsync(CancellationToken cancellationToken = default)
    {
        var status = GetStatus();
        if (!status.PackageAvailable) throw new InvalidOperationException("The compiled VR Dashboard package is not available.");
        if (status.CommunityFolder is null || status.TargetDirectory is null)
            throw new DirectoryNotFoundException(status.Detail);

        Directory.CreateDirectory(status.CommunityFolder);
        var temporary = Path.Combine(status.CommunityFolder, $".{PackageName}.installing-{Guid.NewGuid():N}");
        try
        {
            await CopyDirectoryAsync(_packageSource, temporary, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(status.TargetDirectory))
            {
                if (!IsOurPackage(status.TargetDirectory))
                    throw new InvalidOperationException($"Refusing to replace an unrecognized package at '{status.TargetDirectory}'.");
                Directory.Delete(status.TargetDirectory, true);
            }
            Directory.Move(temporary, status.TargetDirectory);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
        return GetStatus();
    }

    public Task<ToolbarPanelInstallationStatus> RemoveAsync()
    {
        var status = GetStatus();
        if (status.TargetDirectory is not null && Directory.Exists(status.TargetDirectory))
        {
            if (!IsOurPackage(status.TargetDirectory))
                throw new InvalidOperationException($"Refusing to remove an unrecognized package at '{status.TargetDirectory}'.");
            Directory.Delete(status.TargetDirectory, true);
        }
        return Task.FromResult(GetStatus());
    }

    public string? FindCommunityFolder()
    {
        foreach (var configPath in _userConfigCandidates)
        {
            if (!File.Exists(configPath)) continue;
            var match = Regex.Match(
                File.ReadAllText(configPath),
                "InstalledPackagesPath\\s+\"(?<path>[^\"]+)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var packages = Environment.ExpandEnvironmentVariables(match.Groups["path"].Value.Trim());
            if (string.IsNullOrWhiteSpace(packages)) continue;
            return Path.Combine(Path.GetFullPath(packages), "Community");
        }
        return null;
    }

    internal static bool IsOurPackage(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) return false;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("title", out var title)) return false;
            var value = title.GetString();
            return string.Equals(value, PackageTitle, StringComparison.Ordinal)
                || string.Equals(value, LegacyPackageTitle, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsCompletePackage(string directory) =>
        IsOurPackage(directory)
        && File.Exists(Path.Combine(directory, "layout.json"))
        && Directory.Exists(Path.Combine(directory, "html_ui"))
        && Directory.Exists(Path.Combine(directory, "ingamepanels"))
        && Directory.EnumerateFiles(Path.Combine(directory, "ingamepanels"), "*.spb").Any();

    private static IEnumerable<string> DefaultUserConfigCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "UserCfg.opt");
        yield return Path.Combine(roaming, "Microsoft Flight Simulator 2024", "UserCfg.opt");
    }

    private static async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, sourceFile);
            var destinationFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            await using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await using var output = new FileStream(destinationFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }
}
