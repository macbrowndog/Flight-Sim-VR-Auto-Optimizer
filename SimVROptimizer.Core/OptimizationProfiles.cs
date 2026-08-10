namespace SimVROptimizer.Core;

public static class OptimizationProfiles
{
    public static void Apply(OptimizerOptions options, OptimizationProfile profile)
    {
        options.Profile = profile;
        options.UseUltimatePowerPlan = true;
        options.ProcessPriority = ProcessPriorityPreference.High;
        options.UseVendorAwareCpuSets = true;
        options.EnableNvidiaPersistence = true;
        options.UseMsfs2024FastLaunch = true;
        options.FlushDnsCache = true;
        options.DisableGameDvr = false;
        options.ClearStandbyMemory = false;
        options.UseHighResolutionTimer = false;
        options.DisableFullscreenOptimizations = false;
        options.DisablePowerThrottling = false;
        options.ApplyNetworkMemoryOptimizations = false;

        if (profile == OptimizationProfile.Aggressive)
        {
            options.DisableGameDvr = true;
            options.ClearStandbyMemory = true;
            options.UseHighResolutionTimer = true;
            options.DisableFullscreenOptimizations = true;
            options.DisablePowerThrottling = true;
            options.ApplyNetworkMemoryOptimizations = true;
            return;
        }
    }
}
