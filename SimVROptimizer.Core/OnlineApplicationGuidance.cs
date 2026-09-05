using System.Net.Http.Json;
using System.Text.Json;

namespace SimVROptimizer.Core;

public sealed class OnlineApplicationGuidanceClient
{
    public const string CatalogueUrl =
        "https://raw.githubusercontent.com/macbrowndog/Flight-Sim-VR-Auto-Optimizer/main/catalog/application-guidance.json";

    private readonly HttpClient _httpClient;

    public OnlineApplicationGuidanceClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public async Task<OnlineApplicationCatalogue> DownloadAsync(CancellationToken cancellationToken = default)
    {
        var catalogue = await _httpClient.GetFromJsonAsync<OnlineApplicationCatalogue>(
            CatalogueUrl,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken).ConfigureAwait(false);
        return catalogue ?? throw new InvalidDataException("The online application catalogue was empty.");
    }
}

public sealed class OnlineApplicationCatalogue
{
    public int SchemaVersion { get; set; }
    public List<OnlineApplicationGuidanceEntry> Applications { get; set; } = [];
    public List<OnlineServiceGuidanceEntry> Services { get; set; } = [];
}

public sealed class OnlineServiceGuidanceEntry
{
    public List<string> Names { get; set; } = [];
    public List<string> Publishers { get; set; } = [];
    public List<string> Products { get; set; } = [];
    public List<string> Sha256 { get; set; } = [];
    public string Guidance { get; set; } = "KeepRunning";
    public string Impact { get; set; } = "Unknown";
    public string Why { get; set; } = "Online catalogue recommends leaving this service running.";
    public string OperationalNote { get; set; } = "No known direct MSFS impact.";
}

public sealed class OnlineApplicationGuidanceEntry
{
    public List<string> Names { get; set; } = [];
    public List<string> Publishers { get; set; } = [];
    public List<string> Products { get; set; } = [];
    public List<string> Sha256 { get; set; } = [];
    public string Guidance { get; set; } = "KeepRunning";
    public string Impact { get; set; } = "Unknown";
    public string Why { get; set; } = "Online catalogue recommends leaving this application running.";
    public string MsfsImpact { get; set; } = "No known direct MSFS impact.";
}

public static class OnlineApplicationGuidancePolicy
{
    public static int Apply(IEnumerable<RunningAppCandidate> candidates, OnlineApplicationCatalogue catalogue)
    {
        if (catalogue.SchemaVersion != 1) throw new InvalidDataException("Unsupported online catalogue version.");
        var matched = 0;
        foreach (var candidate in candidates)
        {
            if (!candidate.CanStop || candidate.Classification == WorkloadClassification.Protected) continue;
            var best = catalogue.Applications
                .Select(entry => (Entry: entry, Match: MatchEntry(
                    entry.Names, entry.Publishers, entry.Products, entry.Sha256,
                    candidate.ProcessName, candidate.DisplayName, candidate.Identity)))
                .OrderByDescending(item => item.Match.Score)
                .FirstOrDefault();
            if (best.Entry is null || best.Match.Score == 0) continue;
            var entry = best.Entry;

            candidate.Classification = ParseGuidance(entry.Guidance);
            candidate.Impact = Enum.TryParse<ImpactLevel>(entry.Impact, true, out var impact)
                ? impact
                : ImpactLevel.Unknown;
            candidate.ClassificationReason = entry.Why.Trim();
            candidate.Reason = entry.MsfsImpact.Trim();
            candidate.Identity = PromoteIdentity(candidate.Identity, best.Match.Confidence, best.Match.Source);
            if (candidate.Classification == WorkloadClassification.Protected)
            {
                candidate.CanStop = false;
                candidate.Selected = false;
            }
            matched++;
        }
        return matched;
    }

    public static int ApplyServices(IEnumerable<ServiceCandidate> candidates, OnlineApplicationCatalogue catalogue)
    {
        if (catalogue.SchemaVersion != 1) throw new InvalidDataException("Unsupported online catalogue version.");
        var matched = 0;
        foreach (var candidate in candidates)
        {
            if (!candidate.CanStop || candidate.Classification == WorkloadClassification.Protected) continue;
            var best = catalogue.Services
                .Select(entry => (Entry: entry, Match: MatchEntry(
                    entry.Names, entry.Publishers, entry.Products, entry.Sha256,
                    candidate.ServiceName, candidate.DisplayName, candidate.Identity)))
                .OrderByDescending(item => item.Match.Score)
                .FirstOrDefault();
            if (best.Entry is null || best.Match.Score == 0) continue;
            var entry = best.Entry;

            candidate.Classification = ParseGuidance(entry.Guidance);
            candidate.Impact = Enum.TryParse<ImpactLevel>(entry.Impact, true, out var impact)
                ? impact
                : ImpactLevel.Unknown;
            candidate.ClassificationReason = entry.Why.Trim();
            candidate.Reason = entry.OperationalNote.Trim();
            candidate.Identity = PromoteIdentity(candidate.Identity, best.Match.Confidence, best.Match.Source);
            if (candidate.Classification == WorkloadClassification.Protected)
            {
                candidate.CanStop = false;
                candidate.Selected = false;
            }
            matched++;
        }
        return matched;
    }

    private static IdentityMatch MatchEntry(
        IEnumerable<string> names,
        IEnumerable<string> publishers,
        IEnumerable<string> products,
        IEnumerable<string> hashes,
        string systemName,
        string displayName,
        SoftwareIdentity? identity)
    {
        var nameMatch = names.Any(name =>
            Normalize(name).Equals(Normalize(systemName), StringComparison.OrdinalIgnoreCase)
            || Normalize(name).Equals(Normalize(displayName), StringComparison.OrdinalIgnoreCase));
        var hashMatch = identity is { Sha256.Length: > 0 }
            && hashes.Any(hash => hash.Trim().Equals(identity.Sha256, StringComparison.OrdinalIgnoreCase));
        var publisherMatch = identity is { Publisher.Length: > 0 }
            && publishers.Any(publisher => MetadataMatches(identity.Publisher, publisher));
        var productMatch = identity is { Product.Length: > 0 }
            && products.Any(product => MetadataMatches(identity.Product, product));

        if (hashMatch)
            return new(5, SoftwareIdentityConfidence.Verified, "Online catalogue exact SHA-256 match");
        if (identity?.TrustedSignature == true && publisherMatch && productMatch)
            return new(4, SoftwareIdentityConfidence.Verified, "Online catalogue trusted publisher and product match");
        if (identity?.TrustedSignature == true && publisherMatch && nameMatch)
            return new(3, SoftwareIdentityConfidence.Identified, "Online catalogue trusted publisher and executable-name match");
        if (productMatch && nameMatch)
            return new(2, SoftwareIdentityConfidence.Identified, "Online catalogue product and executable-name match");
        if (nameMatch)
            return new(1, SoftwareIdentityConfidence.Likely, "Online catalogue executable-name match");
        return new(0, SoftwareIdentityConfidence.Unidentified, "No online catalogue match");
    }

    private static SoftwareIdentity PromoteIdentity(
        SoftwareIdentity? identity,
        SoftwareIdentityConfidence confidence,
        string source)
    {
        identity ??= new(SoftwareIdentityConfidence.Unidentified, "", "", "", "", false, source);
        var promoted = identity.Confidence >= confidence ? identity.Confidence : confidence;
        return identity with { Confidence = promoted, Source = source };
    }

    private static bool MetadataMatches(string actual, string catalogueValue)
    {
        var expected = catalogueValue.Trim();
        return expected.Length > 0
            && (actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
                || actual.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static WorkloadClassification ParseGuidance(string value) => value.Trim().ToUpperInvariant() switch
    {
        "RECOMMEND" or "RECOMMENDED" => WorkloadClassification.Recommended,
        "PROTECTED" => WorkloadClassification.Protected,
        _ => WorkloadClassification.Unknown
    };

    private static string Normalize(string value) =>
        Path.GetFileNameWithoutExtension(value.Trim()).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    private readonly record struct IdentityMatch(
        int Score,
        SoftwareIdentityConfidence Confidence,
        string Source);
}
