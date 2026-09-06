using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SimVROptimizer.Core;

public sealed class PerformanceDashboardMonitor : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly ConcurrentQueue<double> _frameTimes = new();
    private readonly List<double> _sessionFrameTimes = [];
    private readonly CpuUsageSampler _cpuSampler = new();
    private CancellationTokenSource? _cancellation;
    private Task? _samplingTask;
    private Process? _presentMon;
    private SimConnectFpsSource? _simConnect;
    private Process? _simulator;
    private TimeSpan _lastSimulatorCpu;
    private int? _mainThreadId;
    private TimeSpan _lastMainThreadCpu;
    private DateTime _lastSampleUtc;
    private StreamWriter? _csv;
    private string _frameSourceStatus = "Waiting for simulator";
    private DateTime _captureStartedUtc;
    private int _captureStage;
    private bool _captureTimeoutReported;
    private bool _presentMonStopping;
    private long _captureFrameRows;
    private int _stdoutDiagnosticLines;
    private int _simulatorProcessId;
    private string _simulatorProcessName = "";
    private string _simConnectUnavailableReason = "SimConnect FPS source was not available";

    public PerformanceDashboardMonitor(AppPaths paths, FileLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public event Action<PerformanceTelemetrySample>? SampleReady;

    public async Task StartAsync(int processId, bool logCsv, CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        _simulator = Process.GetProcessById(processId);
        _simulatorProcessId = processId;
        _simulatorProcessName = _simulator.ProcessName;
        _lastSimulatorCpu = _simulator.TotalProcessorTime;
        (_mainThreadId, _lastMainThreadCpu) = FindMainThread(_simulator);
        _lastSampleUtc = DateTime.UtcNow;
        _cpuSampler.Reset();
        _sessionFrameTimes.Clear();
        _presentMonHeaders = null;
        _captureStage = 0;
        _captureTimeoutReported = false;
        _captureFrameRows = 0;
        _stdoutDiagnosticLines = 0;
        _simConnectUnavailableReason = "SimConnect FPS source was not available";
        while (_frameTimes.TryDequeue(out _)) { }
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (logCsv)
        {
            Directory.CreateDirectory(_paths.TelemetryDirectory);
            var path = Path.Combine(_paths.TelemetryDirectory, $"telemetry-{DateTime.Now:yyyyMMdd-HHmmss}-pid{processId}.csv");
            _csv = new StreamWriter(path, false, new UTF8Encoding(false));
            await _csv.WriteLineAsync("Timestamp,FPS,AverageFPS,OnePercentLowFPS,FrameTimeMs,SystemCPU,SimulatorCPU,MainThreadMs,MemoryMB,CpuSpike,Stutter").ConfigureAwait(false);
            await _logger.WriteAsync($"Performance CSV logging enabled: {path}", cancellationToken).ConfigureAwait(false);
        }

        if (SimConnectFpsSource.TryCreate(_simulator, _logger, out var simConnect, out var simConnectUnavailableReason) && simConnect is not null)
        {
            _simConnect = simConnect;
            _frameSourceStatus = "MSFS SimConnect FPS source starting";
            simConnect.FpsReceived += fps => _frameTimes.Enqueue(1000d / fps);
            simConnect.StatusChanged += status => _frameSourceStatus = status;
            simConnect.Start();
            await _logger.WriteAsync("Using the simulator's installed SimConnect runtime for low-overhead visual FPS.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _simConnectUnavailableReason = simConnectUnavailableReason;
            await _logger.WriteAsync($"SimConnect FPS source unavailable: {_simConnectUnavailableReason}. Starting safe PresentMon fallback.", cancellationToken).ConfigureAwait(false);
            StartPresentMon(processId);
        }
        _samplingTask = SampleLoopAsync(_cancellation.Token);
        await _logger.WriteAsync($"Performance dashboard attached to simulator PID {processId}.", cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        _cancellation?.Cancel();
        if (_samplingTask is not null)
        {
            try { await _samplingTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (_simConnect is not null)
        {
            await _simConnect.DisposeAsync().ConfigureAwait(false);
            _simConnect = null;
        }
        await StopPresentMonCaptureAsync().ConfigureAwait(false);
        if (_csv is not null) { await _csv.DisposeAsync().ConfigureAwait(false); }
        _simulator?.Dispose();
        _csv = null;
        _simulator = null;
        _samplingTask = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_simulator is null) break;
            try
            {
                _simulator.Refresh();
                if (_simulator.HasExited) break;
                var now = DateTime.UtcNow;
                if (_simConnect is null
                    && Interlocked.Read(ref _captureFrameRows) == 0
                    && _captureStage == 0
                    && now - _captureStartedUtc >= TimeSpan.FromSeconds(6))
                {
                    _captureStage = 1;
                    _frameSourceStatus = $"No PID frames detected; retrying {_simulatorProcessName}.exe";
                    await _logger.WriteAsync($"PresentMon received no frames for PID {_simulatorProcessId}; retrying by executable name.", cancellationToken).ConfigureAwait(false);
                    await RestartPresentMonAsync(_simulatorProcessName + ".exe").ConfigureAwait(false);
                }
                else if (_simConnect is null
                    && Interlocked.Read(ref _captureFrameRows) == 0
                    && _captureStage == 1
                    && !_captureTimeoutReported
                    && now - _captureStartedUtc >= TimeSpan.FromSeconds(8))
                {
                    _captureTimeoutReported = true;
                    _frameSourceStatus = BuildFpsUnavailableStatus($"{_simConnectUnavailableReason}; PresentMon found no simulator frames and was stopped safely");
                    await _logger.WriteAsync($"PresentMon produced no frame rows for PID {_simulatorProcessId} or process {_simulatorProcessName}.exe; capture stopped without using global ETW.", cancellationToken).ConfigureAwait(false);
                    await StopPresentMonCaptureAsync().ConfigureAwait(false);
                }
                var elapsed = Math.Max(0.001, (now - _lastSampleUtc).TotalSeconds);
                var processCpuTime = _simulator.TotalProcessorTime;
                var processCpu = (processCpuTime - _lastSimulatorCpu).TotalSeconds / elapsed / Math.Max(1, Environment.ProcessorCount) * 100;
                _lastSimulatorCpu = processCpuTime;
                var mainThreadCpu = SampleMainThreadCpu(elapsed);
                _lastSampleUtc = now;

                var cores = _cpuSampler.Sample();
                var recentFrames = new List<double>();
                while (_frameTimes.TryDequeue(out var frameTime))
                {
                    if (frameTime is > 0 and < 10000) recentFrames.Add(frameTime);
                }
                _sessionFrameTimes.AddRange(recentFrames);
                if (_sessionFrameTimes.Count > 120000) _sessionFrameTimes.RemoveRange(0, _sessionFrameTimes.Count - 120000);

                var currentFrame = recentFrames.Count > 0 ? recentFrames.Average() : (double?)null;
                var fps = currentFrame.HasValue ? 1000d / currentFrame.Value : (double?)null;
                var mainThreadFrameTime = CalculateMainThreadFrameTimeMs(mainThreadCpu, fps);
                var averageFps = _sessionFrameTimes.Count > 0 ? 1000d / _sessionFrameTimes.Average() : (double?)null;
                var oneLow = CalculateOnePercentLow(_sessionFrameTimes);
                var median = Median(_sessionFrameTimes.TakeLast(240).ToArray());
                var stutter = currentFrame.HasValue && currentFrame.Value > Math.Max(33.3, median * 1.75);
                var systemCpu = cores.Count == 0 ? 0 : cores.Average();
                var cpuSpike = systemCpu >= 90 || cores.Any(value => value >= 98);
                var sample = new PerformanceTelemetrySample(
                    DateTimeOffset.Now, fps, averageFps, oneLow, currentFrame,
                    Math.Clamp(systemCpu, 0, 100), Math.Clamp(processCpu, 0, 100),
                    mainThreadFrameTime, _simulator.WorkingSet64 / 1024 / 1024,
                    cores, cpuSpike, stutter, _frameSourceStatus);
                SampleReady?.Invoke(sample);
                if (_csv is not null)
                {
                    await _csv.WriteLineAsync(string.Join(',',
                        sample.Timestamp.ToString("O"), N(sample.Fps), N(sample.AverageFps), N(sample.OnePercentLowFps), N(sample.FrameTimeMs),
                        N(sample.SystemCpuPercent), N(sample.SimulatorCpuPercent), N(sample.MainThreadFrameTimeMs), sample.SimulatorMemoryMb,
                        sample.CpuSpike, sample.Stutter)).ConfigureAwait(false);
                    await _csv.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                break;
            }
        }
    }

    private double? SampleMainThreadCpu(double elapsedSeconds)
    {
        if (_simulator is null) return null;
        if (!_mainThreadId.HasValue)
        {
            (_mainThreadId, _lastMainThreadCpu) = FindMainThread(_simulator);
            return null;
        }

        try
        {
            var thread = _simulator.Threads.Cast<ProcessThread>()
                .FirstOrDefault(item => item.Id == _mainThreadId.Value);
            if (thread is null)
            {
                _mainThreadId = null;
                return null;
            }
            var current = thread.TotalProcessorTime;
            var percent = CalculateThreadCpuPercent(current, _lastMainThreadCpu, TimeSpan.FromSeconds(elapsedSeconds));
            _lastMainThreadCpu = current;
            return percent;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static (int? Id, TimeSpan CpuTime) FindMainThread(Process process)
    {
        try
        {
            var thread = process.Threads.Cast<ProcessThread>()
                .Select(item =>
                {
                    try { return (Thread: item, Started: item.StartTime); }
                    catch { return (Thread: item, Started: DateTime.MaxValue); }
                })
                .OrderBy(item => item.Started)
                .ThenBy(item => item.Thread.Id)
                .FirstOrDefault();
            return thread.Thread is null ? (null, TimeSpan.Zero) : (thread.Thread.Id, thread.Thread.TotalProcessorTime);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return (null, TimeSpan.Zero);
        }
    }

    public static double CalculateThreadCpuPercent(TimeSpan current, TimeSpan previous, TimeSpan elapsed) =>
        Math.Clamp((current - previous).TotalSeconds / Math.Max(0.001, elapsed.TotalSeconds) * 100, 0, 100);

    public static double? CalculateMainThreadFrameTimeMs(double? mainThreadCpuPercent, double? fps)
    {
        if (!mainThreadCpuPercent.HasValue || !fps.HasValue || fps.Value <= 0) return null;
        return Math.Clamp(mainThreadCpuPercent.Value / 100 * 1000 / fps.Value, 0, 1000);
    }

    private void StartPresentMon(int processId, string? processName = null)
    {
        var executable = FindPresentMon();
        if (executable is null)
        {
            _frameSourceStatus = BuildFpsUnavailableStatus($"{_simConnectUnavailableReason}; PresentMon component not found");
            return;
        }
        try
        {
            _presentMonHeaders = null;
            Interlocked.Exchange(ref _captureFrameRows, 0);
            _captureStartedUtc = DateTime.UtcNow;
            _captureTimeoutReported = false;
            var targetArgument = string.IsNullOrWhiteSpace(processName)
                ? $"--process_id {processId}"
                : $"--process_name \"{processName}\"";
            var capture = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };
            capture.StartInfo.Arguments = BuildPresentMonArguments(processId, processName);
            capture.OutputDataReceived += PresentMon_OutputDataReceived;
            capture.ErrorDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data)) return;
                if (eventArgs.Data.Contains("access denied", StringComparison.OrdinalIgnoreCase))
                    _frameSourceStatus = BuildFpsUnavailableStatus("PresentMon access denied; run as Administrator or join Performance Log Users");
                else if (eventArgs.Data.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    _frameSourceStatus = BuildFpsUnavailableStatus(eventArgs.Data[6..].Trim());
                _ = _logger.WriteAsync("PresentMon: " + eventArgs.Data);
            };
            capture.Exited += (_, _) =>
            {
                if (ReferenceEquals(_presentMon, capture)
                    && !_presentMonStopping
                    && _presentMonHeaders is null
                    && !_frameSourceStatus.Contains("FPS unavailable", StringComparison.OrdinalIgnoreCase))
                    _frameSourceStatus = BuildFpsUnavailableStatus("PresentMon ended before frame data was received");
            };
            _presentMon = capture;
            capture.Start();
            capture.BeginOutputReadLine();
            capture.BeginErrorReadLine();
            _frameSourceStatus = string.IsNullOrWhiteSpace(processName)
                ? "FPS collector attached by PID; waiting for frame events"
                : $"FPS collector attached to {processName}; waiting for frame events";
            _ = _logger.WriteAsync($"PresentMon capture started with target {targetArgument}; dropped-frame filtering disabled for VR compatibility.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _frameSourceStatus = BuildFpsUnavailableStatus(exception.Message);
        }
    }

    public static string BuildFpsUnavailableStatus(string detail) =>
        $"MONITORING ACTIVE — CPU/MainThread/memory data active; FPS unavailable — {detail}";

    private string[]? _presentMonHeaders;
    private void PresentMon_OutputDataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Data)) return;
        var values = ParseCsv(eventArgs.Data);
        if (_presentMonHeaders is null)
        {
            values = values.Select(NormalizeHeader).ToArray();
            if (values.Any(value =>
                value.Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase)
                || value.Equals("FrameTime", StringComparison.OrdinalIgnoreCase)))
            {
                _presentMonHeaders = values;
                _frameSourceStatus = "FPS collector connected; waiting for first frame";
                _ = _logger.WriteAsync("PresentMon CSV header received: " + string.Join(",", values));
            }
            else if (Interlocked.Increment(ref _stdoutDiagnosticLines) <= 3)
            {
                _ = _logger.WriteAsync("PresentMon stdout before CSV header: " + eventArgs.Data[..Math.Min(eventArgs.Data.Length, 500)]);
            }
            return;
        }
        var index = Array.FindIndex(_presentMonHeaders, value =>
            value.Equals("msBetweenPresents", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FrameTime", StringComparison.OrdinalIgnoreCase)
            || value.Equals("CPUFrameTime", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index < values.Length && double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var frameTime))
        {
            _frameTimes.Enqueue(frameTime);
            if (Interlocked.Increment(ref _captureFrameRows) == 1)
            {
                _frameSourceStatus = "Simulator FPS via Intel PresentMon - frame data active";
                _ = _logger.WriteAsync("PresentMon received its first simulator frame row.");
            }
        }
    }

    private static string NormalizeHeader(string value) => value.Trim().TrimStart('\uFEFF');

    public static string BuildPresentMonArguments(int processId, string? processName = null)
    {
        var targetArgument = string.IsNullOrWhiteSpace(processName)
            ? $"--process_id {processId}"
            : $"--process_name \"{processName}\"";
        return $"{targetArgument} --output_stdout --no_console_stats --v1_metrics --no_track_gpu --no_track_input --terminate_on_proc_exit --stop_existing_session --session_name SimVROptimizer";
    }

    private async Task RestartPresentMonAsync(string processName)
    {
        await StopPresentMonCaptureAsync().ConfigureAwait(false);
        StartPresentMon(_simulatorProcessId, processName);
    }

    private async Task StopPresentMonCaptureAsync()
    {
        if (_presentMon is null) return;
        _presentMonStopping = true;
        await TerminatePresentMonSessionAsync().ConfigureAwait(false);
        try
        {
            if (!_presentMon.HasExited)
                await _presentMon.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        try { if (!_presentMon.HasExited) _presentMon.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _presentMon.Dispose();
        _presentMon = null;
        _presentMonStopping = false;
    }

    private static async Task TerminatePresentMonSessionAsync()
    {
        var executable = FindPresentMon();
        if (executable is null) return;
        try
        {
            using var terminator = Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = "--terminate_existing_session --session_name SimVROptimizer",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (terminator is not null)
                await terminator.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
    }

    private static string? FindPresentMon()
    {
        var root = AppContext.BaseDirectory;
        var exact = Path.Combine(root, "Tools", "PresentMon.exe");
        if (File.Exists(exact)) return exact;
        return Directory.Exists(Path.Combine(root, "Tools"))
            ? Directory.EnumerateFiles(Path.Combine(root, "Tools"), "PresentMon*-x64.exe").FirstOrDefault()
            : null;
    }

    public static string[] ParseCsv(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"' && quoted && index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
            else if (character == '"') quoted = !quoted;
            else if (character == ',' && !quoted) { values.Add(current.ToString()); current.Clear(); }
            else current.Append(character);
        }
        values.Add(current.ToString());
        return values.ToArray();
    }

    public static double? CalculateOnePercentLow(IReadOnlyList<double> frameTimes)
    {
        if (frameTimes.Count < 10) return null;
        var ordered = frameTimes.OrderBy(value => value).ToArray();
        var worstCount = Math.Max(1, (int)Math.Ceiling(ordered.Length * 0.01));
        return 1000d / ordered.TakeLast(worstCount).Average();
    }

    public static double? HoldLastReading(double? freshValue, double? previousValue, TimeSpan age, TimeSpan holdDuration)
    {
        if (freshValue.HasValue) return freshValue;
        return previousValue.HasValue && age <= holdDuration ? previousValue : null;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        return values[values.Length / 2];
    }

    private static string N(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cpuSampler.Dispose();
    }

    private sealed class CpuUsageSampler : IDisposable
    {
        private const uint PdhFormatDouble = 0x00000200;
        private const uint PdhMoreData = 0x800007D2;
        private IntPtr _query;
        private IntPtr _counter;

        public CpuUsageSampler()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
            if (PdhAddEnglishCounterW(_query, @"\Processor(*)\% Processor Time", IntPtr.Zero, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                return;
            }
            _ = PdhCollectQueryData(_query);
        }

        public void Reset()
        {
            if (_query != IntPtr.Zero) _ = PdhCollectQueryData(_query);
        }

        public IReadOnlyList<double> Sample()
        {
            if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return [];
            uint bufferSize = 0;
            uint itemCount = 0;
            var status = PdhGetFormattedCounterArrayW(_counter, PdhFormatDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != PdhMoreData || bufferSize == 0) return [];
            var buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
            try
            {
                if (PdhGetFormattedCounterArrayW(_counter, PdhFormatDouble, ref bufferSize, ref itemCount, buffer) != 0) return [];
                var result = new List<double>();
                var itemSize = Marshal.SizeOf<PdhFormattedCounterValueItem>();
                for (var index = 0; index < itemCount; index++)
                {
                    var item = Marshal.PtrToStructure<PdhFormattedCounterValueItem>(IntPtr.Add(buffer, checked(index * itemSize)));
                    var name = Marshal.PtrToStringUni(item.Name);
                    if (string.Equals(name, "_Total", StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.Value.Status == 0) result.Add(Math.Clamp(item.Value.DoubleValue, 0, 100));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint Status;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValueItem
    {
        public IntPtr Name;
        public PdhFormattedCounterValue Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
