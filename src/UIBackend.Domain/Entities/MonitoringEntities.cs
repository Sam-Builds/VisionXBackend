using UIBackend.Domain.Enums;

namespace UIBackend.Domain.Entities;

public class MonitoredDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerUserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FeedUrl { get; set; } = string.Empty;
    public string? EncryptedStreamCredentials { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Pending;
    public bool MonitoringEnabled { get; set; } = false;
    public string? MlWorkerId { get; set; }
    public string? ActiveJobId { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public DateTime? LastSuccessfulInferenceAtUtc { get; set; }
    public string? LastError { get; set; }
    public int ConfigurationVersion { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; } = false;

    public ICollection<MonitoringJob> Jobs { get; set; } = new List<MonitoringJob>();
    public ICollection<DetectionRecord> Detections { get; set; } = new List<DetectionRecord>();
    public ICollection<AlertRecord> Alerts { get; set; } = new List<AlertRecord>();
}

public class MonitoringJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public string ExternalJobId { get; set; } = string.Empty;
    public MonitoringJobState State { get; set; } = MonitoringJobState.PendingStart;
    public string IdempotencyKey { get; set; } = string.Empty;
    public int Attempts { get; set; } = 0;
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public MonitoredDevice Device { get; set; } = null!;
}

public class DetectionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EventId { get; set; } = string.Empty;
    public Guid DeviceId { get; set; }
    public string OwnerUserId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string DetectionType { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public double? AnomalyScore { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? StructuredMetadataJson { get; set; }
    public string? SnapshotReference { get; set; }
    public DateTime ObservedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsTest { get; set; } = false;

    public MonitoredDevice Device { get; set; } = null!;
}

public class AlertRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public string OwnerUserId { get; set; } = string.Empty;
    public Guid DetectionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;
    public bool IsRead { get; set; } = false;
    public NotificationDispatchStatus NotificationStatus { get; set; } = NotificationDispatchStatus.NotRequired;
    public string? NotificationId { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
    public int OccurrenceCount { get; set; } = 1;
    public DateTime LastOccurrenceAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsTest { get; set; } = false;

    public MonitoredDevice Device { get; set; } = null!;
    public DetectionRecord Detection { get; set; } = null!;
}

public class NotificationOutboxItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AlertId { get; set; }
    public string OwnerUserId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public NotificationDispatchStatus Status { get; set; } = NotificationDispatchStatus.Pending;
    public int AttemptCount { get; set; } = 0;
    public int MaxAttempts { get; set; } = 5;
    public DateTime? NextRetryAtUtc { get; set; }
    public int? LastResponseCode { get; set; }
    public string? LastResponseBody { get; set; }
    public string? LastError { get; set; }
    public bool SimulateTransientFailureOnce { get; set; } = false;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }

    public AlertRecord Alert { get; set; } = null!;
}
