namespace SimVROptimizer.Core;

public static class UserProfileStore
{
    public static SavedUserProfile SaveOrReplace(AppConfig config, string name, ProfileAssociations? associations = null)
    {
        name = NormalizeName(name);
        var existing = config.SavedProfiles.FindIndex(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (associations is null && existing >= 0) associations = config.SavedProfiles[existing].Associations;
        var saved = Snapshot(config, name, associations);
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
        config.CompanionApplications = saved.CompanionApplications.Select(Copy).ToList();
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

    public static SavedUserProfile Duplicate(AppConfig config, string sourceName, string newName)
    {
        var source = FindRequired(config, sourceName);
        newName = NormalizeName(newName);
        if (config.SavedProfiles.Any(item => item.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A profile named '{newName}' already exists.", nameof(newName));
        var copy = Copy(source);
        copy.Name = newName;
        copy.UpdatedAtUtc = DateTimeOffset.UtcNow;
        config.SavedProfiles.Add(copy);
        Sort(config);
        return copy;
    }

    public static SavedUserProfile Rename(AppConfig config, string oldName, string newName)
    {
        var profile = FindRequired(config, oldName);
        newName = NormalizeName(newName);
        if (!profile.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)
            && config.SavedProfiles.Any(item => item.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A profile named '{newName}' already exists.", nameof(newName));
        var wasActive = string.Equals(config.ActiveSavedProfileName, profile.Name, StringComparison.OrdinalIgnoreCase);
        profile.Name = newName;
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (wasActive) config.ActiveSavedProfileName = newName;
        Sort(config);
        return profile;
    }

    public static async Task ExportAsync(SavedUserProfile profile, string path, CancellationToken cancellationToken = default) =>
        await JsonStore.SaveAtomicAsync(path, new ProfileExportDocument { Profile = Copy(profile) }, cancellationToken).ConfigureAwait(false);

    public static async Task<SavedUserProfile> ReadImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var document = await JsonStore.LoadRequiredAsync<ProfileExportDocument>(path, cancellationToken).ConfigureAwait(false);
        if (document.FormatVersion != 1) throw new InvalidDataException($"Unsupported profile format version: {document.FormatVersion}.");
        if (document.Profile is null || document.Profile.Options is null)
            throw new InvalidDataException("The profile file is incomplete.");
        document.Profile.Name = NormalizeName(document.Profile.Name);
        return Copy(document.Profile);
    }

    public static SavedUserProfile Import(AppConfig config, SavedUserProfile profile, bool replace)
    {
        profile = Copy(profile);
        profile.Name = NormalizeName(profile.Name);
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var existing = config.SavedProfiles.FindIndex(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0 && !replace) throw new ArgumentException($"A profile named '{profile.Name}' already exists.");
        if (existing >= 0) config.SavedProfiles[existing] = profile;
        else config.SavedProfiles.Add(profile);
        Sort(config);
        return profile;
    }

    public static IReadOnlyList<ProfileDifference> Diff(AppConfig current, SavedUserProfile saved, ProfileAssociations? associations = null)
    {
        var differences = new List<ProfileDifference>();
        Add("General", "Simulator", saved.SelectedSimulatorId, current.SelectedSimulatorId);
        Add("General", "Workflow mode", saved.SessionMode, current.SessionMode);
        foreach (var property in typeof(OptimizerOptions).GetProperties().Where(property => property.CanRead))
            Add("Optimizer", Humanize(property.Name), property.GetValue(saved.Options), property.GetValue(current.Options));
        Add("Custom apps", "Rules", Rules(saved.CustomApplications), Rules(current.CustomApplications));
        Add("Companion apps", "Preload rules", CompanionRules(saved.CompanionApplications), CompanionRules(current.CompanionApplications));
        AddDictionary("Applications", saved.ApplicationSelections, current.ApplicationSelections);
        AddDictionary("Services", saved.ServiceSelections, current.ServiceSelections);
        AddDictionary("After flight", saved.ApplicationAfterFlightActions, current.ApplicationAfterFlightActions);
        var currentAssociations = associations ?? new ProfileAssociations { SimulatorId = current.SelectedSimulatorId ?? "" };
        var savedAssociations = saved.Associations ?? new ProfileAssociations();
        Add("Associations", "Simulator", savedAssociations.SimulatorId, currentAssociations.SimulatorId);
        Add("Associations", "Aircraft", savedAssociations.Aircraft, currentAssociations.Aircraft);
        Add("Associations", "VR headset", savedAssociations.VrHeadset, currentAssociations.VrHeadset);
        Add("Associations", "Monitor configuration", savedAssociations.MonitorConfiguration, currentAssociations.MonitorConfiguration);
        return differences;

        void Add(string category, string setting, object? savedValue, object? currentValue)
        {
            var left = Format(savedValue);
            var right = Format(currentValue);
            if (!left.Equals(right, StringComparison.Ordinal)) differences.Add(new(category, setting, left, right));
        }

        void AddDictionary<T>(string category, IReadOnlyDictionary<string, T> savedValues, IReadOnlyDictionary<string, T> currentValues)
        {
            foreach (var key in savedValues.Keys.Concat(currentValues.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                savedValues.TryGetValue(key, out var savedValue);
                currentValues.TryGetValue(key, out var currentValue);
                Add(category, key, savedValues.ContainsKey(key) ? savedValue : null, currentValues.ContainsKey(key) ? currentValue : null);
            }
        }
    }

    public static AppConfig CreateContinuedConfig(AppConfig current, PendingLaunch pending) => new()
    {
        SelectedSimulatorId = pending.SimulatorId,
        ManualSimulators = current.ManualSimulators.ToList(),
        SessionMode = pending.SessionMode,
        Options = Copy(pending.Options),
        CustomApplications = pending.CustomApplications.Select(Copy).ToList(),
        CompanionApplications = pending.CompanionApplications.Select(Copy).ToList(),
        ApplicationSelections = Copy(current.ApplicationSelections),
        ServiceSelections = Copy(current.ServiceSelections),
        ApplicationAfterFlightActions = Copy(pending.ApplicationAfterFlightActions.Count > 0
            ? pending.ApplicationAfterFlightActions
            : current.ApplicationAfterFlightActions),
        ActiveSavedProfileName = current.ActiveSavedProfileName,
        SavedProfiles = current.SavedProfiles.Select(Copy).ToList()
    };

    private static SavedUserProfile Snapshot(AppConfig config, string name, ProfileAssociations? associations) => new()
    {
        Name = name,
        SelectedSimulatorId = config.SelectedSimulatorId,
        SessionMode = config.SessionMode,
        Options = Copy(config.Options),
        CustomApplications = config.CustomApplications.Select(Copy).ToList(),
        CompanionApplications = config.CompanionApplications.Select(Copy).ToList(),
        ApplicationSelections = Copy(config.ApplicationSelections),
        ServiceSelections = Copy(config.ServiceSelections),
        ApplicationAfterFlightActions = Copy(config.ApplicationAfterFlightActions),
        Associations = Copy(associations ?? new ProfileAssociations { SimulatorId = config.SelectedSimulatorId ?? "" }),
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

    private static CompanionApplicationRule Copy(CompanionApplicationRule source) => new()
    {
        Enabled = source.Enabled,
        RunAsAdministrator = source.RunAsAdministrator,
        MinimizeAfterLaunch = source.MinimizeAfterLaunch,
        Name = source.Name,
        ExecutablePath = source.ExecutablePath,
        LaunchTiming = source.LaunchTiming,
        LaunchDelaySeconds = source.LaunchDelaySeconds,
        CleanupAction = source.CleanupAction
    };

    private static SavedUserProfile Copy(SavedUserProfile source) => new()
    {
        Name = source.Name,
        SelectedSimulatorId = source.SelectedSimulatorId,
        SessionMode = source.SessionMode,
        Options = Copy(source.Options),
        CustomApplications = source.CustomApplications.Select(Copy).ToList(),
        CompanionApplications = source.CompanionApplications.Select(Copy).ToList(),
        ApplicationSelections = Copy(source.ApplicationSelections),
        ServiceSelections = Copy(source.ServiceSelections),
        ApplicationAfterFlightActions = Copy(source.ApplicationAfterFlightActions),
        Associations = Copy(source.Associations ?? new ProfileAssociations()),
        UpdatedAtUtc = source.UpdatedAtUtc
    };

    private static ProfileAssociations Copy(ProfileAssociations source) => new()
    {
        SimulatorId = source.SimulatorId,
        Aircraft = source.Aircraft,
        VrHeadset = source.VrHeadset,
        MonitorConfiguration = source.MonitorConfiguration
    };

    private static SavedUserProfile FindRequired(AppConfig config, string name) =>
        config.SavedProfiles.FirstOrDefault(item => item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException("Choose a saved profile first.", nameof(name));

    private static void Sort(AppConfig config) => config.SavedProfiles = config.SavedProfiles
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Rules(IEnumerable<CustomApplicationRule> rules) => string.Join(" | ", rules
        .Select(rule => $"{rule.ProcessName}={rule.RestartExecutablePath}")
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static string CompanionRules(IEnumerable<CompanionApplicationRule> rules) => string.Join(" | ", rules
        .Select(rule => $"{rule.Enabled}:{rule.RunAsAdministrator}:{rule.MinimizeAfterLaunch}:{rule.Name}:{rule.ExecutablePath}:{rule.LaunchTiming}:{rule.LaunchDelaySeconds}:{rule.CleanupAction}")
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static string Format(object? value) => value switch
    {
        null => "—",
        bool flag => flag ? "On" : "Off",
        string text when string.IsNullOrWhiteSpace(text) => "—",
        _ => value.ToString() ?? "—"
    };

    private static string Humanize(string name) => string.Concat(name.Select((character, index) =>
        index > 0 && char.IsUpper(character) ? " " + character : character.ToString()));

    private static Dictionary<string, bool> Copy(Dictionary<string, bool> source) =>
        new(source, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, ApplicationAfterFlightAction> Copy(
        Dictionary<string, ApplicationAfterFlightAction> source) =>
        new(source, StringComparer.OrdinalIgnoreCase);
}
