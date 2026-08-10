namespace SimVROptimizer.Core;

public sealed class FileLogger
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileLogger(string path, long maxBytes = 2 * 1024 * 1024, int retainedFiles = 5)
    {
        _path = path;
        _maxBytes = Math.Max(1024, maxBytes);
        _retainedFiles = Math.Clamp(retainedFiles, 1, 20);
    }

    public async Task WriteAsync(string message, CancellationToken cancellationToken = default)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            RotateIfNeeded(System.Text.Encoding.UTF8.GetByteCount(line));
            await File.AppendAllTextAsync(_path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length + incomingBytes <= _maxBytes) return;

        var oldest = $"{_path}.{_retainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}";
            if (File.Exists(source)) File.Move(source, $"{_path}.{index + 1}");
        }
        File.Move(_path, $"{_path}.1");
    }
}
