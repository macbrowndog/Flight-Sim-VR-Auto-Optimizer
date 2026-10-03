using System.Diagnostics;

namespace SimVROptimizer.Core;

public sealed class ApplicationResourceSampler
{
    public async Task<IReadOnlyDictionary<string, double>> SampleCpuAsync(
        IEnumerable<string> processNames,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        interval = interval <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : interval;
        var names = processNames.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var before = Capture(names);
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var after = Capture(names);
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (!before.TryGetValue(name, out var first) || !after.TryGetValue(name, out var second)) continue;
            var cpuDelta = second - first;
            if (cpuDelta < TimeSpan.Zero) continue;
            result[name] = CalculateCpuPercent(cpuDelta, elapsed, Environment.ProcessorCount);
        }

        return result;
    }

    public static double CalculateCpuPercent(TimeSpan cpuDelta, TimeSpan elapsed, int logicalProcessorCount)
    {
        if (cpuDelta < TimeSpan.Zero || elapsed <= TimeSpan.Zero || logicalProcessorCount <= 0) return 0;
        return Math.Clamp(cpuDelta.TotalMilliseconds / elapsed.TotalMilliseconds / logicalProcessorCount * 100, 0, 100);
    }

    private static Dictionary<string, TimeSpan> Capture(IEnumerable<string> processNames)
    {
        var result = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in processNames)
        {
            var total = TimeSpan.Zero;
            var captured = false;
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        total += process.TotalProcessorTime;
                        captured = true;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // The process exited or access was denied between enumeration and sampling.
                    }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            if (captured) result[name] = total;
        }
        return result;
    }
}
