using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimVROptimizer.Core;

public sealed record DashboardTelemetryFrame(
    int SchemaVersion,
    DateTimeOffset Timestamp,
    bool SessionActive,
    string Status,
    string Simulator,
    int StutterCount,
    int CpuSpikeCount,
    PerformanceTelemetrySample? Sample,
    string CpuName,
    IReadOnlyList<ProcessorLoadGroup> ProcessorGroups);

/// <summary>
/// Publishes dashboard samples to the MSFS toolbar panel over a loopback-only,
/// read-only WebSocket. The listener deliberately exposes no optimizer commands.
/// </summary>
public sealed class DashboardTelemetryServer : IAsyncDisposable
{
    public const int DefaultPort = 48624;
    public const string WebSocketPath = "/dashboard";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FileLogger _logger;
    private readonly int _port;
    private CpuProfile? _cpuProfile;
    private readonly ConcurrentDictionary<long, ClientConnection> _clients = new();
    private readonly object _snapshotGate = new();
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _nextClientId;
    private DashboardTelemetryFrame _snapshot = new(
        4, DateTimeOffset.UtcNow, false, "Optimizer ready", "", 0, 0, null, "", []);

    public DashboardTelemetryServer(FileLogger logger, int port = DefaultPort, CpuProfile? cpuProfile = null)
    {
        _logger = logger;
        _port = port;
        _cpuProfile = cpuProfile;
    }

    public int Port => _port;
    public string Endpoint => $"ws://127.0.0.1:{_port}{WebSocketPath}";
    public bool IsRunning => _listener is not null;

    public void SetCpuProfile(CpuProfile profile)
    {
        _cpuProfile = profile;
        lock (_snapshotGate) _snapshot = _snapshot with { CpuName = profile.Model };
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null) return Task.CompletedTask;

        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = listener;
        _cancellation = cancellation;
        _acceptLoop = AcceptLoopAsync(listener, cancellation.Token);
        return _logger.WriteAsync($"VR toolbar telemetry bridge listening on {Endpoint} (read-only, loopback only).", cancellationToken);
    }

    public void BeginSession(string simulator)
    {
        lock (_snapshotGate)
        {
            _snapshot = new DashboardTelemetryFrame(
                4, DateTimeOffset.UtcNow, true, "Waiting for the first performance sample", simulator, 0, 0, null,
                _cpuProfile?.Model ?? "", []);
        }
        BroadcastSnapshot();
    }

    public void Publish(PerformanceTelemetrySample sample)
    {
        lock (_snapshotGate)
        {
            _snapshot = _snapshot with
            {
                Timestamp = sample.Timestamp,
                SessionActive = true,
                Status = sample.FrameSourceStatus,
                StutterCount = _snapshot.StutterCount + (sample.Stutter ? 1 : 0),
                CpuSpikeCount = _snapshot.CpuSpikeCount + (sample.CpuSpike ? 1 : 0),
                Sample = sample,
                CpuName = _cpuProfile?.Model ?? "",
                ProcessorGroups = ProcessorLoadSummarizer.Summarize(_cpuProfile, sample.LogicalProcessorUsage)
            };
        }
        BroadcastSnapshot();
    }

    public void EndSession(string status = "Flight session complete")
    {
        lock (_snapshotGate)
        {
            _snapshot = _snapshot with
            {
                Timestamp = DateTimeOffset.UtcNow,
                SessionActive = false,
                Status = status
            };
        }
        BroadcastSnapshot();
    }

    public void ResetStutterCounter()
    {
        lock (_snapshotGate)
        {
            _snapshot = _snapshot with
            {
                Timestamp = DateTimeOffset.UtcNow,
                StutterCount = 0
            };
        }
        BroadcastSnapshot();
    }

    public void ResetCpuSpikeCounter()
    {
        lock (_snapshotGate)
        {
            _snapshot = _snapshot with
            {
                Timestamp = DateTimeOffset.UtcNow,
                CpuSpikeCount = 0
            };
        }
        BroadcastSnapshot();
    }

    public DashboardTelemetryFrame GetSnapshot()
    {
        lock (_snapshotGate) return _snapshot;
    }

    internal string SerializeSnapshot()
    {
        lock (_snapshotGate) return JsonSerializer.Serialize(_snapshot, JsonOptions);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.WriteAsync("VR toolbar telemetry listener stopped unexpectedly: " + exception.Message).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        try
        {
            var stream = client.GetStream();
            var request = await ReadHttpRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!TryGetWebSocketKey(request, out var webSocketKey)
                || !request.StartsWith($"GET {WebSocketPath} ", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttpErrorAsync(stream, cancellationToken).ConfigureAwait(false);
                client.Dispose();
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                webSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = "HTTP/1.1 101 Switching Protocols\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);

            var id = Interlocked.Increment(ref _nextClientId);
            var connection = new ClientConnection(client, stream);
            _clients[id] = connection;
            if (!await connection.SendTextAsync(SerializeSnapshot(), cancellationToken).ConfigureAwait(false))
                RemoveClient(id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
        }
        catch
        {
            client.Dispose();
        }
    }

    private void BroadcastSnapshot()
    {
        var payload = SerializeSnapshot();
        foreach (var pair in _clients)
            _ = SendToClientAsync(pair.Key, pair.Value, payload);
    }

    private async Task SendToClientAsync(long id, ClientConnection connection, string payload)
    {
        try
        {
            if (!await connection.SendTextAsync(payload, _cancellation?.Token ?? CancellationToken.None).ConfigureAwait(false))
                RemoveClient(id);
        }
        catch
        {
            RemoveClient(id);
        }
    }

    private void RemoveClient(long id)
    {
        if (_clients.TryRemove(id, out var connection)) connection.Dispose();
    }

    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            if (length >= 4 && Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }
        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    private static bool TryGetWebSocketKey(string request, out string key)
    {
        foreach (var line in request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            const string header = "Sec-WebSocket-Key:";
            if (!line.StartsWith(header, StringComparison.OrdinalIgnoreCase)) continue;
            key = line[header.Length..].Trim();
            return key.Length > 0;
        }
        key = "";
        return false;
    }

    private static async Task WriteHttpErrorAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        const string response = "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var cancellation = _cancellation;
        var listener = _listener;
        var acceptLoop = _acceptLoop;
        _cancellation = null;
        _listener = null;
        _acceptLoop = null;
        cancellation?.Cancel();
        listener?.Stop();
        foreach (var id in _clients.Keys) RemoveClient(id);
        if (acceptLoop is not null)
        {
            try { await acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cancellation?.Dispose();
    }

    private sealed class ClientConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendGate = new(1, 1);

        public ClientConnection(TcpClient client, NetworkStream stream)
        {
            _client = client;
            _stream = stream;
        }

        public async Task<bool> SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            var headerLength = payload.Length <= 125 ? 2 : payload.Length <= ushort.MaxValue ? 4 : 10;
            var frame = new byte[headerLength + payload.Length];
            frame[0] = 0x81;
            if (payload.Length <= 125)
            {
                frame[1] = (byte)payload.Length;
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                frame[1] = 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)payload.Length;
            }
            else
            {
                frame[1] = 127;
                var length = (ulong)payload.Length;
                for (var index = 0; index < 8; index++) frame[2 + index] = (byte)(length >> (56 - index * 8));
            }
            payload.CopyTo(frame.AsSpan(headerLength));

            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_client.Connected) return false;
                await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public void Dispose()
        {
            _client.Dispose();
            _sendGate.Dispose();
        }
    }
}
