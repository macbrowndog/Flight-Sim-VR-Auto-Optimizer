using System.Runtime.InteropServices;

namespace SimVROptimizer.Core;

internal static class AdvancedSystemTuning
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;
    private const string ProfilePrivilege = "SeProfileSingleProcessPrivilege";
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
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenAdjustPrivileges, out var token))
        {
            error = $"could not open the process token (Win32 {Marshal.GetLastWin32Error()})";
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, ProfilePrivilege, out var luid))
            {
                error = $"could not locate {ProfilePrivilege} (Win32 {Marshal.GetLastWin32Error()})";
                return false;
            }

            var requested = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };
            var bufferSize = (uint)Marshal.SizeOf<TokenPrivileges>();
            if (!AdjustTokenPrivileges(token, false, ref requested, bufferSize, out var previous, out _))
            {
                error = $"could not enable {ProfilePrivilege} (Win32 {Marshal.GetLastWin32Error()})";
                return false;
            }

            var privilegeError = Marshal.GetLastWin32Error();
            if (privilegeError == ErrorNotAllAssigned)
            {
                error = $"{ProfilePrivilege} is not available to this process";
                return false;
            }

            try
            {
                var command = MemoryPurgeStandbyList;
                var status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
                error = status == 0 ? "" : $"NTSTATUS 0x{status:X8}";
                return status == 0;
            }
            finally
            {
                _ = AdjustTokenPrivileges(token, false, ref previous, bufferSize, out _, out _);
            }
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetTimerResolution(uint desiredResolution, bool setResolution, out uint currentResolution);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int systemInformationClass, ref int systemInformation, int systemInformationLength);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        nint tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        out TokenPrivileges previousState,
        out uint returnLength);
}
