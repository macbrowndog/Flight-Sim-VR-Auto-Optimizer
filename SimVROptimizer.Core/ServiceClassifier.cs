namespace SimVROptimizer.Core;

public static class ServiceClassifier
{
    private static readonly string[] RecommendedMarkers =
    [
        "NahimicService", "DiagTrack", "GoogleUpdater", "gupdate", "gupdatem",
        "GoogleChromeElevationService", "CCleaner", "iCloud", "Apple Mobile Device Service",
        "Bonjour Service", "iPod Service", "edgeupdate", "edgeupdatem", "MozillaMaintenance",
        "DropboxUpdate", "dbupdate", "dbupdatem", "Adobe", "AGMService", "AGSService",
        "ClickToRunSvc", "EABackgroundService", "EpicOnlineServices", "GalaxyClientService",
        "GalaxyCommunication"
    ];

    public static WorkloadClassificationResult Classify(string serviceName, bool hasKnownProfile, bool canStop)
    {
        if (!canStop)
            return new(WorkloadClassification.Protected,
                "Required or protected service. It is locked and will remain running.");

        if (!hasKnownProfile)
            return new(WorkloadClassification.Unknown,
                "Unrecognized third-party service. Leave it running unless you know what it supports and can safely pause it.");

        if (RecommendedMarkers.Any(marker => serviceName.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return new(WorkloadClassification.Recommended,
                "Known non-essential background or update service that can be restored after the session.");

        return new(WorkloadClassification.Optional,
            "Known service that may support printing, indexing, lighting, peripherals, or another active feature.");
    }
}
