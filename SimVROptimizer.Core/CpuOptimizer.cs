using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SimVROptimizer.Core;

public sealed record CpuOptimizationScope(
    ProcessPriorityClass OriginalPriority,
    IReadOnlyList<uint> OriginalCpuSetIds,
    bool PriorityChanged,
    bool CpuSetsChanged,
    string Summary);

public sealed class CpuOptimizer
{
    private const int ErrorInsufficientBuffer = 122;

    public CpuProfile GetProfile()
    {
        var cpuSets = ReadSystemCpuSets();
        var vendor = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "VendorIdentifier",
            "Unknown") as string ?? "Unknown";
        var model = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString",
            "Unknown CPU") as string ?? "Unknown CPU";
        var isIntel = vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        var isAmd = vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        var efficiencyClasses = cpuSets.Select(item => item.EfficiencyClass).Distinct().ToArray();
        var physicalCores = cpuSets.Select(item => (item.Group, item.CoreIndex)).Distinct().Count();

        return new CpuProfile(
            vendor,
            model.Trim(),
            isIntel,
            isAmd,
            isAmd && model.Contains("X3D", StringComparison.OrdinalIgnoreCase),
            efficiencyClasses.Length > 1,
            physicalCores,
            cpuSets.Count,
            cpuSets);
    }

    public CpuOptimizationScope Apply(Process process, OptimizerOptions options)
    {
        var profile = GetProfile();
        var originalPriority = process.PriorityClass;
        var originalCpuSets = ReadProcessCpuSets(process.Handle);
        var requestedPriority = options.ProcessPriority switch
        {
            ProcessPriorityPreference.Normal => ProcessPriorityClass.Normal,
            ProcessPriorityPreference.High => ProcessPriorityClass.High,
            _ => ProcessPriorityClass.AboveNormal
        };

        var priorityChanged = false;
        if (requestedPriority != originalPriority)
        {
            process.PriorityClass = requestedPriority;
            priorityChanged = true;
        }

        var cpuSetsChanged = false;
        string strategy;
        if (profile.IsAmd && profile.IsX3D)
        {
            strategy = "AMD X3D detected; Windows scheduler retained (cache/CCD safe)";
        }
        else if (profile.IsAmd)
        {
            strategy = "AMD detected; Windows scheduler retained";
        }
        else if (profile.IsIntel && profile.IsHybrid && options.UseVendorAwareCpuSets)
        {
            var highestClass = profile.CpuSets.Max(item => item.EfficiencyClass);
            var performanceCpuSetIds = profile.CpuSets
                .Where(item => item.EfficiencyClass == highestClass)
                .Select(item => item.Id)
                .ToArray();
            if (performanceCpuSetIds.Length > 0)
            {
                if (SetProcessDefaultCpuSets(process.Handle, performanceCpuSetIds, (uint)performanceCpuSetIds.Length))
                {
                    cpuSetsChanged = true;
                    strategy = $"Intel hybrid detected; {performanceCpuSetIds.Length} performance-class CPU set(s) applied";
                }
                else
                {
                    strategy = $"Intel hybrid detected; CPU set request failed ({new Win32Exception(Marshal.GetLastWin32Error()).Message}), scheduler retained";
                }
            }
            else
            {
                strategy = "Intel hybrid detected; no performance CPU sets were available, scheduler retained";
            }
        }
        else if (profile.IsIntel && profile.IsHybrid)
        {
            strategy = "Intel hybrid detected; Windows scheduler retained (advanced CPU sets not selected)";
        }
        else if (profile.IsIntel)
        {
            strategy = "Intel non-hybrid detected; Windows scheduler retained";
        }
        else
        {
            strategy = "Unknown CPU vendor; Windows scheduler retained";
        }

        var summary = $"CPU Vendor={profile.Vendor}; Model={profile.Model}; Cores={profile.PhysicalCoreCount}; " +
                      $"Logical={profile.LogicalProcessorCount}; Priority={requestedPriority}; Strategy={strategy}.";
        return new CpuOptimizationScope(originalPriority, originalCpuSets, priorityChanged, cpuSetsChanged, summary);
    }

    public void Restore(Process process, CpuOptimizationScope scope)
    {
        if (process.HasExited) return;
        if (scope.CpuSetsChanged)
        {
            var original = scope.OriginalCpuSetIds.ToArray();
            if (!SetProcessDefaultCpuSets(process.Handle, original.Length == 0 ? null : original, (uint)original.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restore original CPU sets.");
        }
        if (scope.PriorityChanged) process.PriorityClass = scope.OriginalPriority;
    }

    private static IReadOnlyList<CpuSetDescriptor> ReadSystemCpuSets()
    {
        _ = GetSystemCpuSetInformation(IntPtr.Zero, 0, out var requiredLength, IntPtr.Zero, 0);
        if (requiredLength == 0) return [];

        var buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
        try
        {
            if (!GetSystemCpuSetInformation(buffer, requiredLength, out var returnedLength, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read Windows CPU set topology.");

            var result = new List<CpuSetDescriptor>();
            uint offset = 0;
            while (offset + 8 <= returnedLength)
            {
                var current = IntPtr.Add(buffer, checked((int)offset));
                var size = unchecked((uint)Marshal.ReadInt32(current, 0));
                var type = Marshal.ReadInt32(current, 4);
                if (size < 8 || offset + size > returnedLength) break;
                if (type == 0 && size >= 20)
                {
                    var id = unchecked((uint)Marshal.ReadInt32(current, 8));
                    var group = unchecked((ushort)Marshal.ReadInt16(current, 12));
                    var logicalIndex = Marshal.ReadByte(current, 14);
                    var coreIndex = Marshal.ReadByte(current, 15);
                    var efficiencyClass = Marshal.ReadByte(current, 18);
                    var flags = Marshal.ReadByte(current, 19);
                    result.Add(new CpuSetDescriptor(id, group, logicalIndex, coreIndex, efficiencyClass, (flags & 0x01) != 0));
                }
                offset += size;
            }
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<uint> ReadProcessCpuSets(IntPtr processHandle)
    {
        var firstSucceeded = GetProcessDefaultCpuSets(processHandle, null, 0, out var requiredCount);
        if (!firstSucceeded && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query the simulator's existing CPU sets.");
        if (requiredCount == 0) return [];

        var ids = new uint[requiredCount];
        if (!GetProcessDefaultCpuSets(processHandle, ids, (uint)ids.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the simulator's existing CPU sets.");
        return ids;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information,
        uint bufferLength,
        out uint returnedLength,
        IntPtr process,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessDefaultCpuSets(
        IntPtr process,
        [Out] uint[]? cpuSetIds,
        uint cpuSetIdCount,
        out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDefaultCpuSets(
        IntPtr process,
        [In] uint[]? cpuSetIds,
        uint cpuSetIdCount);
}
