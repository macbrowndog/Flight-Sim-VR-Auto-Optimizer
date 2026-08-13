using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimVROptimizer.Core;

public sealed class SimConnectFpsSource : IAsyncDisposable
{
    private const uint FrameEventId = 1;
    private const uint ReceiveIdEventFrame = 7;
    private readonly string _libraryPath;
    private readonly FileLogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _worker;
    private IntPtr _library;
    private IntPtr _connection;
    private SimConnectClose? _close;

    private SimConnectFpsSource(string libraryPath, FileLogger logger)
    {
        _libraryPath = libraryPath;
        _logger = logger;
    }

    public event Action<double>? FpsReceived;
    public event Action<string>? StatusChanged;

    public void Start() => _worker ??= RunAsync(_cancellation.Token);

    public static bool TryCreate(Process simulator, FileLogger logger, out SimConnectFpsSource? source)
    {
        source = null;
        string? executablePath;
        try { executablePath = simulator.MainModule?.FileName; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        var libraryPath = FindLibraryNearExecutable(executablePath);
        if (libraryPath is null) return false;
        source = new SimConnectFpsSource(libraryPath, logger);
        return true;
    }

    public static string? FindLibraryNearExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        foreach (var name in new[] { "SimConnect_internal.dll", "SimConnect.dll" })
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _library = NativeLibrary.Load(_libraryPath);
            var open = GetExport<SimConnectOpen>("SimConnect_Open");
            var subscribe = GetExport<SimConnectSubscribeToSystemEvent>("SimConnect_SubscribeToSystemEvent");
            var getNext = GetExport<SimConnectGetNextDispatch>("SimConnect_GetNextDispatch");
            _close = GetExport<SimConnectClose>("SimConnect_Close");

            StatusChanged?.Invoke("MSFS SimConnect FPS source waiting for simulator");
            var openFailureReported = false;
            while (!cancellationToken.IsCancellationRequested && _connection == IntPtr.Zero)
            {
                var result = open(out _connection, "VR Auto-Optimizer", IntPtr.Zero, 0, IntPtr.Zero, 0);
                if (result >= 0 && _connection != IntPtr.Zero) break;
                _connection = IntPtr.Zero;
                if (!openFailureReported)
                {
                    openFailureReported = true;
                    StatusChanged?.Invoke($"MSFS SimConnect waiting for flight session (HRESULT 0x{result:X8})");
                    await _logger.WriteAsync($"SimConnect is installed but not ready yet: HRESULT 0x{result:X8}.", cancellationToken).ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            if (_connection == IntPtr.Zero) return;

            var subscribeResult = subscribe(_connection, FrameEventId, "Frame");
            if (subscribeResult < 0)
            {
                StatusChanged?.Invoke($"MSFS SimConnect FPS unavailable (HRESULT 0x{subscribeResult:X8})");
                await _logger.WriteAsync($"SimConnect Frame subscription failed: HRESULT 0x{subscribeResult:X8}.", cancellationToken).ConfigureAwait(false);
                return;
            }
            StatusChanged?.Invoke("MSFS visual FPS via SimConnect - waiting for first frame");
            await _logger.WriteAsync($"SimConnect FPS source connected using {_libraryPath}.", cancellationToken).ConfigureAwait(false);

            var firstFrame = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                var receivedAny = false;
                while (getNext(_connection, out var data, out var dataSize) >= 0 && data != IntPtr.Zero && dataSize >= 12)
                {
                    receivedAny = true;
                    var receiveId = unchecked((uint)Marshal.ReadInt32(data, 8));
                    if (receiveId != ReceiveIdEventFrame || dataSize < 32) continue;
                    var fps = Marshal.PtrToStructure<SimConnectReceiveEventFrame>(data).FrameRate;
                    if (!float.IsFinite(fps) || fps <= 0 || fps > 1000) continue;
                    FpsReceived?.Invoke(fps);
                    if (firstFrame)
                    {
                        firstFrame = false;
                        StatusChanged?.Invoke("MSFS visual FPS via SimConnect - frame data active");
                        await _logger.WriteAsync("SimConnect received its first MSFS visual frame event.", cancellationToken).ConfigureAwait(false);
                    }
                }
                await Task.Delay(receivedAny ? 5 : 25, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            StatusChanged?.Invoke("MSFS SimConnect FPS unavailable - " + exception.Message);
            await _logger.WriteAsync("SimConnect FPS source failed: " + exception.Message).ConfigureAwait(false);
        }
    }

    private T GetExport<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        if (_connection != IntPtr.Zero && _close is not null)
        {
            try { _close(_connection); }
            catch (Exception exception) when (exception is InvalidOperationException or AccessViolationException) { }
            _connection = IntPtr.Zero;
        }
        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
        _cancellation.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SimConnectReceiveEventFrame
    {
        public uint Size;
        public uint Version;
        public uint ReceiveId;
        public uint GroupId;
        public uint EventId;
        public uint Data;
        public float FrameRate;
        public float SimulationSpeed;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SimConnectOpen(out IntPtr connection, string name, IntPtr window, uint userEvent, IntPtr eventHandle, uint configIndex);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int SimConnectSubscribeToSystemEvent(IntPtr connection, uint eventId, string eventName);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SimConnectGetNextDispatch(IntPtr connection, out IntPtr data, out uint dataSize);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SimConnectClose(IntPtr connection);
}
