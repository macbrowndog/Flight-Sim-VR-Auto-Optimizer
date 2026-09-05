namespace SimVROptimizer.Core;

public static class UserProfileStore
{
    public static SavedUserProfile SaveOrReplace(AppConfig config, string name)
    {
        name = NormalizeName(name);
        var saved = Snapshot(config, name);
        var existing = config.SavedProfiles.FindIndex(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) config.SavedProfiles[existing] = saved;
        else config.SavedProfiles.Add(saved);
        config.SavedProfiles = config.SavedProfiles
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.ActiveSavedProfileName = saved.Name;
        return saved;
    }

    public static bool TryApply(AppConfig config, string name)
    {
        var saved = config.SavedProfiles.FirstOrDefault(item =>
            item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (saved is null) return false;

        config.SelectedSimulatorId = saved.SelectedSimulatorId;
        config.SessionMode = saved.SessionMode;
        config.Options = Copy(saved.Options);
        config.CustomApplications = saved.CustomApplications.Select(Copy).ToList();
        config.ApplicationSelections = Copy(saved.ApplicationSelections);
        config.ServiceSelections = Copy(saved.ServiceSelections);
        config.ApplicationAfterFlightActions = Copy(saved.ApplicationAfterFlightActions);
        config.ActiveSavedProfileName = saved.Name;
        return true;
    }

    public static bool Delete(AppConfig config, string name)
    {
        var removed = config.SavedProfiles.RemoveAll(item =>
            item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed && string.Equals(config.ActiveSavedProfileName, name.Trim(), StringComparison.OrdinalIgnoreCase))
            config.ActiveSavedProfileName = null;
        return removed;
    }

    public static AppConfig CreateContinuedConfig(AppConfig current, PendingLaunch pending) => new()
    {
        SelectedSimulatorId = pending.SimulatorId,
        SessionMode = pending.SessionMode,
        Options = Copy(pending.Options),
        CustomApplications = pending.CustomApplications.Select(Copy).ToList(),
        ApplicationSelections = Copy(current.ApplicationSelections),
        ServiceSelections = Copy(current.ServiceSelections),
        ApplicationAfterFlightActions = Copy(pending.ApplicationAfterFlightActions.Count > 0
            ? pending.ApplicationAfterFlightActions
            : current.ApplicationAfterFlightActions),
        ActiveSavedProfileName = current.ActiveSavedProfileName,
        SavedProfiles = current.SavedProfiles.Select(Copy).ToList()
    };

    private static SavedUserProfile Snapshot(AppConfig config, string name) => new()
    {
        Name = name,
        SelectedSimulatorId = config.SelectedSimulatorId,
        SessionMode = config.SessionMode,
        Options = Copy(config.Options),
        CustomApplications = config.CustomApplications.Select(Copy).ToList(),
        ApplicationSelections = Copy(config.ApplicationSelections),
        ServiceSelections = Copy(config.ServiceSelections),
        ApplicationAfterFlightActions = Copy(config.ApplicationAfterFlightActions),
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Enter a profile name.", nameof(name));
        if (name.Length > 48) throw new ArgumentException("Profile names can contain no more than 48 characters.", nameof(name));
        return name;
    }

    private static OptimizerOptions Copy(OptimizerOptions source) => new()
    {
        DryRun = source.DryRun,
        Profile = source.Profile,
        UseUltimatePowerPlan = source.UseUltimatePowerPlan,
        EnableNvidiaPersistence = source.EnableNvidiaPersistence,
        UseMsfs2024FastLaunch = source.UseMsfs2024FastLaunch,
        UseOpenXrTurboMode = source.UseOpenXrTurboMode,
        FlushDnsCache = source.FlushDnsCache,
        DisableGameDvr = source.DisableGameDvr,
        ClearStandbyMemory = source.ClearStandbyMemory,
        UseHighResolutionTimer = source.UseHighResolutionTimer,
        DisableFullscreenOptimizations = source.DisableFullscreenOptimizations,
        DisablePowerThrottling = source.DisablePowerThrottling,
        ProcessPriority = source.ProcessPriority,
        UseVendorAwareCpuSets = source.UseVendorAwareCpuSets,
        ContentCreatorMode = source.ContentCreatorMode,
        VrRuntime = source.VrRuntime,
        LaunchTimeoutSeconds = source.LaunchTimeoutSeconds,
        EnablePerformanceDashboard = source.EnablePerformanceDashboard,
        LogPerformanceCsv = source.LogPerformanceCsv,
        EnableOnlineApplicationGuidance = source.EnableOnlineApplicationGuidance
    };

    private static CustomApplicationRule Copy(CustomApplicationRule source) => new()
    {
        ProcessName = source.ProcessName,
        RestartExecutablePath = source.RestartExecutablePath
    };

    private static SavedUserProfile Copy(SavedUserProfile source) => new()
    {
        Name = source.Name,
        SelectedSimulatorId = source.SelectedSimulatorId,
        SessionMode = source.SessionMode,
        Options = Copy(source.Options),
        CustomApplications = source.CustomApplications.Select(Copy).ToList(),
        ApplicationSelections = Copy(source.ApplicationSelections),
        ServiceSelections = Copy(source.ServiceSelections),
        ApplicationAfterFlightActions = Copy(source.ApplicationAfterFlightActions),
        UpdatedAtUtc = source.UpdatedAtUtc
    };

    private static Dictionary<string, bool> Copy(Dictionary<string, bool> source) =>
        new(source, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, ApplicationAfterFlightAction> Copy(
        Dictionary<string, ApplicationAfterFlightAction> source) =>
        new(source, StringComparer.OrdinalIgnoreCase);
}
