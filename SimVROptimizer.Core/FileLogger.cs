namespace SimVROptimizer.Core;

public sealed class FileLogger
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileLogger(string path) => _path = path;

    public async Task WriteAsync(string message, CancellationToken cancellationToken = default)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.AppendAllTextAsync(_path, line, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
