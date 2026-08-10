using System.Runtime.InteropServices;

namespace SimVROptimizer.Core;

internal static class AdvancedSystemTuning
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const uint HalfMillisecondIn100Nanoseconds = 5_000;

    public static bool TryRequestHalfMillisecondTimer(out uint actualResolution, out string error)
    {
        var status = NtSetTimerResolution(HalfMillisecondIn100Nanoseconds, true, out actualResolution);
        error = status == 0 ? "" : $"NTSTATUS 0x{status:X8}";
        return status == 0;
    }

    public static void ReleaseHalfMillisecondTimer()
    {
        _ = NtSetTimerResolution(HalfMillisecondIn100Nanoseconds, false, out _);
    }

    public static bool TryPurgeStandbyMemory(out string error)
    {
        var command = MemoryPurgeStandbyList;
        var status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        error = status == 0 ? "" : $"NTSTATUS 0x{status:X8}";
        return status == 0;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredResolution, bool setResolution, out uint currentResolution);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int systemInformationClass, ref int systemInformation, int systemInformationLength);
}
