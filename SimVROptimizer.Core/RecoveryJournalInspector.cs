using System.Diagnostics;

namespace SimVROptimizer.Core;

public static class RecoveryJournalInspector
{
    public static bool IsOwnerProcessActive(SessionJournal journal)
    {
        if (journal.OwnerProcessId <= 0 || journal.OwnerProcessStartedAtUtc is null) return false;
        try
        {
            using var process = Process.GetProcessById(journal.OwnerProcessId);
            if (process.HasExited) return false;
            var actualStart = process.StartTime.ToUniversalTime();
            return Math.Abs((actualStart - journal.OwnerProcessStartedAtUtc.Value.UtcDateTime).TotalSeconds) < 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
