using System.Text.Json;

namespace TrainerStudio.Core;

/// <summary>
/// A compact, local, append-only audit record. It intentionally contains only
/// the approved target identity and visible operation outcome; no scripts,
/// credentials or arbitrary process data are recorded.
/// </summary>
public sealed record TrainerAuditEvent(
    DateTimeOffset TimestampUtc,
    string EventType,
    bool Succeeded,
    string TargetId,
    string? FeatureId,
    string Message,
    string? Before,
    string? After);

public sealed class AuditLogWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly object _sync = new();

    public AuditLogWriter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    public string Path { get; }

    public void Append(TrainerAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(auditEvent, Options) + Environment.NewLine;
        lock (_sync)
        {
            File.AppendAllText(Path, line);
        }
    }
}
