namespace SimVROptimizer.Core;

public static class SimulatorProcessHandoffPolicy
{
    public static bool Supports(SimulatorDefinition simulator) =>
        simulator.Id.StartsWith("dcs-", StringComparison.OrdinalIgnoreCase);

    public static bool IsEligible(
        SimulatorDefinition simulator,
        string processName,
        int processId,
        DateTime processStartedUtc,
        IReadOnlySet<int> observedProcessIds,
        DateTime sessionStartedUtc) =>
        Supports(simulator)
        && simulator.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)
        && !observedProcessIds.Contains(processId)
        && processStartedUtc >= sessionStartedUtc.AddSeconds(-2);
}
