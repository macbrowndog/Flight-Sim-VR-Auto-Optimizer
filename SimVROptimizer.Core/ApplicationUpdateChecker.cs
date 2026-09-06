using System.Net.Http.Headers;
using System.Text.Json;

namespace SimVROptimizer.Core;

public sealed record ApplicationUpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string ReleaseName,
    Uri ReleaseUri)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}

public sealed class ApplicationUpdateChecker
{
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/macbrowndog/Flight-Sim-VR-Auto-Optimizer/releases/latest";

    private readonly HttpClient _httpClient;

    public ApplicationUpdateChecker(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<ApplicationUpdateInfo> CheckAsync(
        Version installedVersion,
        CancellationToken cancellationToken = default)
    {
        var current = Normalize(installedVersion);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VR-Auto-Optimizer", current.ToString(3)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tag = RequiredString(root, "tag_name");
        var latest = ParseReleaseVersion(tag);
        var releaseName = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()?.Trim()
            : null;
        var releaseUri = ValidateReleaseUri(RequiredString(root, "html_url"));

        return new(current, latest, string.IsNullOrWhiteSpace(releaseName) ? tag : releaseName, releaseUri);
    }

    public static Version ParseReleaseVersion(string tag)
    {
        var value = tag.Trim().TrimStart('v', 'V');
        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0) value = value[..suffix];
        if (!Version.TryParse(value, out var version))
            throw new InvalidDataException($"The latest release tag '{tag}' is not a valid version.");
        return Normalize(version);
    }

    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static string RequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element)
            || string.IsNullOrWhiteSpace(element.GetString()))
            throw new InvalidDataException($"The GitHub release response did not include {propertyName}.");
        return element.GetString()!.Trim();
    }

    private static Uri ValidateReleaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(
                "/macbrowndog/Flight-Sim-VR-Auto-Optimizer/releases/",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub returned an invalid release address.");
        return uri;
    }
}
