using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace SimVROptimizer.Core;

public sealed record GpuTelemetrySnapshot(
    double? LoadPercent,
    double? MemoryUsedMb,
    double? TotalMemoryMb,
    double? MemoryUsedPercent,
    string Status)
{
    public static GpuTelemetrySnapshot Unavailable(string status) => new(null, null, null, null, status);
}

public interface IGpuTelemetrySource
{
    GpuTelemetrySnapshot Read();
}

/// <summary>
/// Reads the version 1 GPU-Z shared-memory sensor table published as GPUZShMem.
/// GPU-Z must be running; this reader never starts or controls it.
/// </summary>
public sealed class GpuZSharedMemoryReader : IGpuTelemetrySource
{
    public const string MappingName = "GPUZShMem";
    public const int MaximumRecords = 128;
    private const int HeaderSize = 12;
    private const int DataRecordSize = 1024;
    private const int SensorNameSize = 512;
    private const int SensorUnitSize = 16;
    private const int SensorRecordSize = SensorNameSize + SensorUnitSize + sizeof(uint) + sizeof(double);
    public const int MappingSize = HeaderSize + MaximumRecords * DataRecordSize + MaximumRecords * SensorRecordSize;

    public GpuTelemetrySnapshot Read()
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.Read);
            using var view = mapping.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.Read);
            if (view.ReadInt32(4) != 0)
                return GpuTelemetrySnapshot.Unavailable("GPU-Z sensors are updating");

            var bytes = new byte[MappingSize];
            view.ReadArray(0, bytes, 0, bytes.Length);
            if (view.ReadInt32(4) != 0)
                return GpuTelemetrySnapshot.Unavailable("GPU-Z sensors are updating");
            return ParseSnapshot(bytes, unchecked((uint)Environment.TickCount));
        }
        catch (FileNotFoundException)
        {
            return GpuTelemetrySnapshot.Unavailable("GPU-Z is not running");
        }
        catch (UnauthorizedAccessException)
        {
            return GpuTelemetrySnapshot.Unavailable("GPU-Z shared memory access was denied");
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return GpuTelemetrySnapshot.Unavailable("GPU-Z sensor data is unavailable");
        }
    }

    public static GpuTelemetrySnapshot ParseSnapshot(ReadOnlySpan<byte> bytes, uint currentTick)
    {
        if (bytes.Length < MappingSize)
            return GpuTelemetrySnapshot.Unavailable("GPU-Z shared memory is incomplete");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (version != 1)
            return GpuTelemetrySnapshot.Unavailable($"Unsupported GPU-Z shared-memory version {version}");
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != 0)
            return GpuTelemetrySnapshot.Unavailable("GPU-Z sensors are updating");

        var lastUpdate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (unchecked(currentTick - lastUpdate) > 10_000)
            return GpuTelemetrySnapshot.Unavailable("GPU-Z sensor data is stale");

        double? totalMemory = null;
        for (var index = 0; index < MaximumRecords; index++)
        {
            var record = bytes.Slice(HeaderSize + index * DataRecordSize, DataRecordSize);
            var key = ReadFixedUnicode(record[..512]);
            if (!IsTotalMemoryKey(key)) continue;
            totalMemory = ParseMemoryCapacityMb(ReadFixedUnicode(record[512..]));
            if (totalMemory.HasValue) break;
        }

        double? gpuLoad = null;
        double? memoryUsed = null;
        var sensorOffset = HeaderSize + MaximumRecords * DataRecordSize;
        for (var index = 0; index < MaximumRecords; index++)
        {
            var record = bytes.Slice(sensorOffset + index * SensorRecordSize, SensorRecordSize);
            var name = ReadFixedUnicode(record[..SensorNameSize]);
            if (name.Length == 0) continue;
            var unit = ReadFixedUnicode(record.Slice(SensorNameSize, SensorUnitSize));
            var value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(record[(SensorNameSize + SensorUnitSize + sizeof(uint))..]));
            if (!double.IsFinite(value)) continue;

            if (name.Equals("GPU Load", StringComparison.OrdinalIgnoreCase))
                gpuLoad = unit.Contains('%') ? Math.Clamp(value, 0, 100) : value;
            else if (name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("Memory Used (Dedicated)", StringComparison.OrdinalIgnoreCase))
                memoryUsed = ConvertMemoryToMb(value, unit);
        }

        if (!gpuLoad.HasValue && !memoryUsed.HasValue)
            return GpuTelemetrySnapshot.Unavailable("GPU-Z GPU Load and Memory Used sensors were not found");
        double? memoryPercent = memoryUsed.HasValue && totalMemory is > 0
            ? Math.Clamp(memoryUsed.Value / totalMemory.Value * 100, 0, 100)
            : null;
        var status = !gpuLoad.HasValue
            ? "GPU-Z GPU Load sensor unavailable"
            : !memoryUsed.HasValue
                ? "GPU-Z Memory Used sensor unavailable"
                : !totalMemory.HasValue
                    ? "GPU-Z total VRAM unavailable"
                    : $"GPU-Z live sensors — {memoryUsed.Value:N0} / {totalMemory.Value:N0} MB VRAM";
        return new(gpuLoad, memoryUsed, totalMemory, memoryPercent, status);
    }

    private static bool IsTotalMemoryKey(string key) =>
        key.Equals("MemSize", StringComparison.OrdinalIgnoreCase)
        || key.Equals("MemorySize", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Memory Size", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Dedicated Memory", StringComparison.OrdinalIgnoreCase);

    private static double? ParseMemoryCapacityMb(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Replace(",", "", StringComparison.Ordinal).Trim();
        var numericLength = 0;
        while (numericLength < normalized.Length
               && (char.IsDigit(normalized[numericLength]) || normalized[numericLength] is '.' or '-'))
            numericLength++;
        if (numericLength == 0
            || !double.TryParse(normalized[..numericLength], NumberStyles.Float, CultureInfo.InvariantCulture,
                out var value)
            || !double.IsFinite(value)
            || value <= 0)
            return null;

        var unit = normalized[numericLength..].Trim();
        return ConvertMemoryToMb(value, unit);
    }

    private static double ConvertMemoryToMb(double value, string unit)
    {
        if (unit.Equals("GB", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("GiB", StringComparison.OrdinalIgnoreCase))
            return value * 1024;
        if (unit.Equals("KB", StringComparison.OrdinalIgnoreCase)
            || unit.Equals("KiB", StringComparison.OrdinalIgnoreCase))
            return value / 1024;
        return value;
    }

    private static string ReadFixedUnicode(ReadOnlySpan<byte> bytes)
    {
        var length = 0;
        while (length + 1 < bytes.Length && (bytes[length] != 0 || bytes[length + 1] != 0)) length += 2;
        return Encoding.Unicode.GetString(bytes[..length]).Trim();
    }
}
