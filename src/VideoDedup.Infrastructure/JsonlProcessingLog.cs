using System.Security;
using System.Text;
using System.Text.Json;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

/// <summary>
/// Bounded, privacy-safe processing log written as JSON lines under the application data folder. Only counts,
/// durations, worker names and redacted failure kinds are written; a write failure increments a counter instead of
/// propagating, because losing a log line must never stop a scan.
/// </summary>
public sealed class JsonlProcessingLog : IProcessingLog
{
    public const long DefaultMaxBytesPerFile = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _path;
    private readonly long _maxBytesPerFile;
    private readonly int _retainedFiles;
    private long _writeFailures;

    public JsonlProcessingLog(string dataRoot, long maxBytesPerFile = DefaultMaxBytesPerFile, int retainedFiles = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("The application data folder must be an absolute path.", nameof(dataRoot));
        }

        if (maxBytesPerFile < 4096 || maxBytesPerFile > 256 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytesPerFile), "The log size limit must be between 4 KiB and 256 MiB.");
        }

        if (retainedFiles is < 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedFiles), "Between zero and sixteen rotated logs may be kept.");
        }

        _directory = Path.Combine(Path.GetFullPath(dataRoot), "logs");
        _path = Path.Combine(_directory, "processing.jsonl");
        _maxBytesPerFile = maxBytesPerFile;
        _retainedFiles = retainedFiles;
    }

    public string CurrentPath => _path;

    public long WriteFailures
    {
        get { lock (_gate) { return _writeFailures; } }
    }

    public void Write(ProcessingLogEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(new
        {
            ts = entry.TimestampUtc.ToUniversalTime(),
            worker = entry.Worker,
            outcome = entry.Outcome.ToString(),
            ms = entry.ElapsedMilliseconds,
            failure = entry.FailureKind,
        });
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                Rotate(Encoding.UTF8.GetByteCount(line) + 1);
                File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
            {
                _writeFailures++;
            }
        }
    }

    private void Rotate(long incomingBytes)
    {
        var current = new FileInfo(_path);
        if (!current.Exists || current.Length + incomingBytes <= _maxBytesPerFile)
        {
            return;
        }

        if (_retainedFiles == 0)
        {
            File.Delete(_path);
            return;
        }

        var oldest = $"{_path}.{_retainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _retainedFiles - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_path}.{index + 1}", overwrite: true);
            }
        }

        File.Move(_path, $"{_path}.1", overwrite: true);
    }
}
