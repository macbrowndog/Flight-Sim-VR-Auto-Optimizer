using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimVROptimizer.Core;

public sealed class ProcessWindowMinimizer
{
    private const int SwMinimize = 6;

    public async Task<bool> MinimizeWhenReadyAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                process.Refresh();
                if (process.HasExited) return false;
                var window = process.MainWindowHandle;
                if (window != IntPtr.Zero)
                {
                    ShowWindowAsync(window, SwMinimize);
                    return true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return false;
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
}
