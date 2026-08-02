using System.Diagnostics;
using System.Security.Principal;

namespace SimVROptimizer.App;

internal static class AdminService
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RelaunchElevated(string? arguments = null)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the application executable.");
        Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments = arguments ?? ""
        });
    }
}
