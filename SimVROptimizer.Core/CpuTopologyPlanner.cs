namespace SimVROptimizer.Core;

public static class CpuTopologyPlanner
{
    public static CpuOptimizationPlan Create(CpuProfile profile, bool useVendorAwareCpuSets)
    {
        var groupCount = Math.Max(1, profile.CpuSets.Select(item => item.Group).Distinct().Count());
        var groupNote = groupCount > 1 ? $" across {groupCount} processor groups" : "";

        if (profile.IsAmd && profile.IsX3D)
            return Scheduler(groupCount, $"AMD X3D detected{groupNote}; Windows scheduler retained for cache/CCD safety");
        if (profile.IsAmd)
            return Scheduler(groupCount, $"AMD detected{groupNote}; Windows scheduler retained");
        if (!profile.IsIntel)
            return Scheduler(groupCount, $"Unknown CPU vendor{groupNote}; Windows scheduler retained");
        if (!profile.IsHybrid || profile.CpuSets.Select(item => item.EfficiencyClass).Distinct().Count() < 2)
            return Scheduler(groupCount, $"Intel uniform-core topology detected{groupNote}; Windows scheduler retained");
        if (!useVendorAwareCpuSets)
            return Scheduler(groupCount, $"Intel hybrid detected{groupNote}; Windows scheduler retained (performance CPU Sets not selected)");

        var highestPerformanceClass = profile.CpuSets.Max(item => item.EfficiencyClass);
        var selected = profile.CpuSets
            .Where(item => item.EfficiencyClass == highestPerformanceClass)
            .Where(item => !item.Parked && !item.Allocated && !item.RealTime)
            .Select(item => item.Id)
            .Distinct()
            .Order()
            .ToArray();

        if (selected.Length == 0)
            return Scheduler(groupCount, $"Intel hybrid detected{groupNote}; performance CPU Sets are parked or reserved, so Windows scheduler was retained");

        var usableCount = profile.CpuSets.Count(item => !item.Parked && !item.Allocated && !item.RealTime);
        if (selected.Length >= usableCount)
            return Scheduler(groupCount, $"Intel topology did not expose a distinct usable performance-core set{groupNote}; Windows scheduler retained");

        return new CpuOptimizationPlan(
            CpuSchedulingStrategy.IntelPerformanceCpuSets,
            selected,
            groupCount,
            $"Intel hybrid detected{groupNote}; {selected.Length} unparked, unreserved performance CPU Set(s) selected");
    }

    private static CpuOptimizationPlan Scheduler(int groupCount, string description) =>
        new(CpuSchedulingStrategy.SchedulerManaged, [], groupCount, description);
}
