namespace SimVROptimizer.Core;

public sealed record PerformanceSessionSummary(
    Guid Id,
    DateTimeOffset StartedAt,
    string Simulator,
    string Profile,
    double DurationMinutes,
    int SampleCount,
    int FpsSampleCount,
    double? AverageFps,
    double? OnePercentLowFps,
    double? AverageFrameTimeMs,
    double AverageSystemCpuPercent,
    double AverageSimulatorCpuPercent,
    double? AverageMainThreadMs,
    long PeakMemoryMb,
    int StutterCount,
    int CpuSpikeCount,
    string FrameSource,
    string SimulatorVersion = "Unknown",
    string GpuDriverVersion = "Unknown",
    string OptimizerVersion = "Unknown")
{
    public string StartedLabel => StartedAt.LocalDateTime.ToString("g");
    public string DurationLabel => $"{DurationMinutes:0.0} min";
    public string AverageFpsLabel => AverageFps?.ToString("0.0") ?? "—";
    public string OnePercentLowLabel => OnePercentLowFps?.ToString("0.0") ?? "—";
    public string MainThreadLabel => AverageMainThreadMs.HasValue ? $"{AverageMainThreadMs:0.0} ms" : "—";
}

public sealed record PerformanceTrendEntry(PerformanceSessionSummary Session, string Changes)
{
    public DateTimeOffset StartedAt => Session.StartedAt;
    public string StartedLabel => Session.StartedLabel;
    public string Simulator => Session.Simulator;
    public string Profile => Session.Profile;
    public double DurationMinutes => Session.DurationMinutes;
    public string DurationLabel => Session.DurationLabel;
    public double? AverageFps => Session.AverageFps;
    public string AverageFpsLabel => Session.AverageFpsLabel;
    public double? OnePercentLowFps => Session.OnePercentLowFps;
    public string OnePercentLowLabel => Session.OnePercentLowLabel;
    public double? AverageMainThreadMs => Session.AverageMainThreadMs;
    public string MainThreadLabel => Session.MainThreadLabel;
    public int StutterCount => Session.StutterCount;
    public int CpuSpikeCount => Session.CpuSpikeCount;
    public string SimulatorVersion => Session.SimulatorVersion;
    public string GpuDriverVersion => Session.GpuDriverVersion;
}

public sealed class PerformanceHistoryDocument
{
    public List<PerformanceSessionSummary> Sessions { get; set; } = [];
}

public sealed class PerformanceComparisonExportDocument
{
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
    public List<PerformanceSessionSummary> Sessions { get; set; } = [];
}

public sealed record PerformanceSessionComparison(
    PerformanceSessionSummary Latest,
    PerformanceSessionSummary Baseline,
    double? AverageFpsDelta,
    double? OnePercentLowDelta,
    double? MainThreadMsDelta,
    double StuttersPerMinuteDelta,
    double CpuSpikesPerMinuteDelta);

public static class PerformanceSessionAnalyzer
{
    public static PerformanceSessionSummary? Summarize(
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string simulator,
        string profile,
        IReadOnlyList<PerformanceTelemetrySample> samples,
        string simulatorVersion = "Unknown",
        string gpuDriverVersion = "Unknown",
        string optimizerVersion = "Unknown")
    {
        if (samples.Count < 2) return null;
        var fps = samples.Where(sample => sample.Fps.HasValue).Select(sample => sample.Fps!.Value).ToArray();
        var frameTimes = samples.Where(sample => sample.FrameTimeMs.HasValue).Select(sample => sample.FrameTimeMs!.Value).ToArray();
        var mainThread = samples.Where(sample => sample.MainThreadFrameTimeMs.HasValue).Select(sample => sample.MainThreadFrameTimeMs!.Value).ToArray();
        var oneLow = samples.LastOrDefault(sample => sample.OnePercentLowFps.HasValue)?.OnePercentLowFps;
        var frameSource = samples.LastOrDefault(sample => !string.IsNullOrWhiteSpace(sample.FrameSourceStatus))?.FrameSourceStatus
            ?? "Unavailable";

        return new(
            Guid.NewGuid(), startedAt, simulator, profile,
            Math.Max(0, (endedAt - startedAt).TotalMinutes), samples.Count, fps.Length,
            AverageOrNull(fps), oneLow, AverageOrNull(frameTimes),
            samples.Average(sample => sample.SystemCpuPercent),
            samples.Average(sample => sample.SimulatorCpuPercent),
            AverageOrNull(mainThread), samples.Max(sample => sample.SimulatorMemoryMb),
            samples.Count(sample => sample.Stutter), samples.Count(sample => sample.CpuSpike), frameSource,
            Clean(simulatorVersion), Clean(gpuDriverVersion), Clean(optimizerVersion));
    }

    public static PerformanceSessionComparison? CompareLatestMatching(IReadOnlyList<PerformanceSessionSummary> sessions)
    {
        if (sessions.Count < 2) return null;
        var latest = sessions.OrderByDescending(session => session.StartedAt).First();
        var baseline = sessions
            .Where(session => session.Id != latest.Id
                && session.Simulator.Equals(latest.Simulator, StringComparison.OrdinalIgnoreCase)
                && session.Profile.Equals(latest.Profile, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(session => session.StartedAt)
            .FirstOrDefault();
        if (baseline is null) return null;

        return new(latest, baseline,
            Delta(latest.AverageFps, baseline.AverageFps),
            Delta(latest.OnePercentLowFps, baseline.OnePercentLowFps),
            Delta(latest.AverageMainThreadMs, baseline.AverageMainThreadMs),
            Rate(latest.StutterCount, latest.DurationMinutes) - Rate(baseline.StutterCount, baseline.DurationMinutes),
            Rate(latest.CpuSpikeCount, latest.DurationMinutes) - Rate(baseline.CpuSpikeCount, baseline.DurationMinutes));
    }

    public static IReadOnlyList<PerformanceTrendEntry> BuildTrend(IReadOnlyList<PerformanceSessionSummary> sessions, int maximum = 20)
    {
        var ordered = sessions.OrderBy(session => session.StartedAt).TakeLast(Math.Max(1, maximum)).ToArray();
        var trend = new List<PerformanceTrendEntry>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = ordered[index];
            var changes = new List<string>();
            if (index > 0)
            {
                var previous = ordered[index - 1];
                var sameSimulator = previous.Simulator.Equals(current.Simulator, StringComparison.OrdinalIgnoreCase);
                if (!sameSimulator)
                    changes.Add($"SIMULATOR {previous.Simulator} → {current.Simulator}");
                else if (KnownChanged(previous.SimulatorVersion, current.SimulatorVersion))
                    changes.Add($"SIM {previous.SimulatorVersion} → {current.SimulatorVersion}");
                if (KnownChanged(previous.GpuDriverVersion, current.GpuDriverVersion))
                    changes.Add($"GPU {previous.GpuDriverVersion} → {current.GpuDriverVersion}");
                if (!previous.Profile.Equals(current.Profile, StringComparison.OrdinalIgnoreCase))
                    changes.Add($"PROFILE {previous.Profile} → {current.Profile}");
            }
            trend.Add(new(current, changes.Count == 0 ? "—" : string.Join(" | ", changes)));
        }
        return trend;
    }

    private static double? AverageOrNull(IReadOnlyCollection<double> values) => values.Count == 0 ? null : values.Average();
    private static double? Delta(double? latest, double? baseline) => latest.HasValue && baseline.HasValue ? latest - baseline : null;
    private static double Rate(int count, double minutes) => count / Math.Max(minutes, 0.01);
    private static bool KnownChanged(string previous, string current) =>
        !string.IsNullOrWhiteSpace(previous) && !string.IsNullOrWhiteSpace(current)
        && !previous.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        && !current.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        && !previous.Equals(current, StringComparison.OrdinalIgnoreCase);
    private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();
}

public sealed class PerformanceHistoryStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PerformanceHistoryStore(string path) => _path = path;

    public async Task<IReadOnlyList<PerformanceSessionSummary>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var document = await JsonStore.LoadOrDefaultAsync(_path, () => new PerformanceHistoryDocument(), cancellationToken).ConfigureAwait(false);
        return document.Sessions.OrderByDescending(session => session.StartedAt).ToArray();
    }

    public async Task AppendAsync(PerformanceSessionSummary summary, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await JsonStore.LoadOrDefaultAsync(_path, () => new PerformanceHistoryDocument(), cancellationToken).ConfigureAwait(false);
            document.Sessions.Add(summary);
            document.Sessions = document.Sessions.OrderByDescending(session => session.StartedAt).Take(100).ToList();
            await JsonStore.SaveAtomicAsync(_path, document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> DeleteAsync(IEnumerable<Guid> sessionIds, CancellationToken cancellationToken = default)
    {
        var ids = sessionIds.ToHashSet();
        if (ids.Count == 0) return 0;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await JsonStore.LoadOrDefaultAsync(_path, () => new PerformanceHistoryDocument(), cancellationToken).ConfigureAwait(false);
            var removed = document.Sessions.RemoveAll(session => ids.Contains(session.Id));
            if (removed > 0)
                await JsonStore.SaveAtomicAsync(_path, document, cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }
}
