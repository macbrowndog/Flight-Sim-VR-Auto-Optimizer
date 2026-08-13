namespace SimVROptimizer.Core;

public static class ApplicationClassifier
{
    private static readonly HashSet<string> RecommendedBackgroundApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Overwolf", "RTSS", "MSIAfterburner", "NVIDIA Share", "GameBar", "XboxGameBar",
        "LenovoVantage", "OneDrive", "Dropbox", "GoogleDriveFS", "Creative Cloud", "CCXProcess",
        "CCleaner", "CCleaner64", "GoogleUpdater", "GoogleCrashHandler", "GoogleCrashHandler64",
        "iCloudDrive", "iCloudServices", "ApplePhotoStreams", "APSDaemon", "mDNSResponder",
        "AppleMobileDeviceService", "iPodService", "iTunesHelper", "CrossDeviceService"
    };

    public static WorkloadClassificationResult Classify(
        string processName,
        bool hasKnownProfile,
        bool canStop,
        bool isCustom,
        string restartCommand)
    {
        if (!canStop)
            return new(WorkloadClassification.Protected,
                "Required or protected component. It is locked and will remain running.");

        if (isCustom)
            return new(WorkloadClassification.Optional,
                "User-defined rule. It follows your saved choice and is not selected automatically by default.");

        if (!hasKnownProfile)
            return new(WorkloadClassification.Unknown,
                "Not in the verified application catalogue. Leave it running unless you recognize it and know it is unnecessary.");

        var canRestart = !restartCommand.StartsWith("none:", StringComparison.OrdinalIgnoreCase);
        if (RecommendedBackgroundApps.Contains(processName) && canRestart)
            return new(WorkloadClassification.Recommended,
                "Known non-essential background application with supported session restart.");

        if (!canRestart)
            return new(WorkloadClassification.Optional,
                "Known application, but automatic restart is unavailable. Close it manually or select it only when appropriate.");

        return new(WorkloadClassification.Optional,
            "Known application that may be actively used for communication, media, capture, browsing, or hardware control.");
    }
}
