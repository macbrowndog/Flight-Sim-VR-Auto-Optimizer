namespace SimVROptimizer.Core;

public static class ProcessorLoadSummarizer
{
    private const int AmdCoresPerCcd = 8;

    public static IReadOnlyList<ProcessorLoadGroup> Summarize(
        CpuProfile? profile,
        IReadOnlyList<double> logicalProcessorUsage)
    {
        if (logicalProcessorUsage.Count == 0) return [];

        var values = logicalProcessorUsage
            .Select(value => Math.Clamp(value, 0, 100))
            .ToArray();

        if (profile?.IsAmd == true)
        {
            var amdGroups = SummarizeAmdCcds(profile, values);
            if (amdGroups.Count > 0) return amdGroups;
        }

        return [CreateGroup("ALL LOGICAL", values.Select((value, index) => (index, value)))];
    }

    public static string Format(IReadOnlyList<ProcessorLoadGroup> groups)
    {
        if (groups.Count == 0) return "Waiting for CPU samples…";
        return string.Join("   /   ", groups.Select(group =>
            $"{group.Label}  AVG {group.AveragePercent:0.0}%  PEAK L{group.PeakLogicalProcessor:00} {group.PeakPercent:0.0}%"));
    }

    private static IReadOnlyList<ProcessorLoadGroup> SummarizeAmdCcds(CpuProfile profile, double[] values)
    {
        var coreKeys = profile.CpuSets
            .Select(item => (item.Group, item.CoreIndex))
            .Distinct()
            .OrderBy(item => item.Group)
            .ThenBy(item => item.CoreIndex)
            .ToArray();

        if (coreKeys.Length > 0)
        {
            var groups = new List<ProcessorLoadGroup>();
            for (var offset = 0; offset < coreKeys.Length; offset += AmdCoresPerCcd)
            {
                var ccdIndex = offset / AmdCoresPerCcd;
                var cores = coreKeys.Skip(offset).Take(AmdCoresPerCcd).ToHashSet();
                var readings = profile.CpuSets
                    .Where(cpuSet => cores.Contains((cpuSet.Group, cpuSet.CoreIndex)))
                    .Select(cpuSet => ToGlobalLogicalIndex(cpuSet))
                    .Distinct()
                    .Where(index => index >= 0 && index < values.Length)
                    .Select(index => (index, values[index]))
                    .ToArray();

                if (readings.Length > 0)
                    groups.Add(CreateGroup($"CCD{ccdIndex}", readings));
            }

            if (groups.Count > 0) return groups;
        }

        var physicalCores = Math.Max(1, profile.PhysicalCoreCount);
        var ccdCount = Math.Max(1, (int)Math.Ceiling(physicalCores / (double)AmdCoresPerCcd));
        var logicalPerCcd = Math.Max(1, (int)Math.Ceiling(values.Length / (double)ccdCount));
        return values
            .Select((value, index) => (index, value))
            .Chunk(logicalPerCcd)
            .Select((readings, index) => CreateGroup($"CCD{index}", readings))
            .ToArray();
    }

    private static ProcessorLoadGroup CreateGroup(string label, IEnumerable<(int Index, double Value)> readings)
    {
        var values = readings.ToArray();
        var peak = values.MaxBy(item => item.Value);
        return new ProcessorLoadGroup(
            label,
            values.Average(item => item.Value),
            peak.Value,
            values.Length,
            peak.Index);
    }

    private static int ToGlobalLogicalIndex(CpuSetDescriptor cpuSet) =>
        checked(cpuSet.Group * 64 + cpuSet.LogicalProcessorIndex);
}
