using UIBackend.Domain.Enums;

namespace UIBackend.Application.DTOs;

public record CreateDeviceRequest(
    string Name,
    string FeedUrl,
    string? StreamUsername,
    string? StreamPassword,
    bool MonitoringEnabled = false
);

public record UpdateDeviceRequest(
    string Name,
    string FeedUrl,
    string? StreamUsername,
    string? StreamPassword,
    bool? MonitoringEnabled
);

public record DeviceDto(
    Guid DeviceId,
    string Name,
    string FeedUrl,
    bool HasCredentials,
    DeviceStatus Status,
    bool MonitoringEnabled,
    string? MlWorkerId,
    string? ActiveJobId,
    DateTime? LastSeenAtUtc,
    DateTime? LastSuccessfulInferenceAtUtc,
    string? LastError,
    int ConfigurationVersion,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);

public record DeviceStatusDto(
    Guid DeviceId,
    DeviceStatus Status,
    bool MonitoringEnabled,
    string? ActiveJobId,
    string? MlWorkerId,
    DateTime? LastSeenAtUtc,
    DateTime? LastSuccessfulInferenceAtUtc,
    string? LastError
);

public record DetectionDto(
    Guid DetectionId,
    string EventId,
    Guid DeviceId,
    string JobId,
    string DetectionType,
    double Confidence,
    double? AnomalyScore,
    string ModelName,
    string ModelVersion,
    string? Description,
    string? SnapshotReference,
    DateTime ObservedAtUtc,
    bool IsTest
);

public record AlertDto(
    Guid AlertId,
    Guid DeviceId,
    string DeviceName,
    Guid DetectionId,
    string Title,
    string Description,
    AlertSeverity Severity,
    bool IsRead,
    NotificationDispatchStatus NotificationStatus,
    string? NotificationId,
    int OccurrenceCount,
    DateTime LastOccurrenceAtUtc,
    DateTime CreatedAtUtc,
    bool IsTest
);

public record AlertDetailDto(
    AlertDto Alert,
    DetectionDto Detection,
    OutboxDispatchTraceDto? NotificationTrace
);

public record OutboxDispatchTraceDto(
    Guid OutboxId,
    NotificationDispatchStatus Status,
    int AttemptCount,
    int? LastResponseCode,
    string? LastResponseBody,
    string? LastError,
    DateTime? NextRetryAtUtc,
    DateTime? SentAtUtc
);

public record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize
);

public record MlDetectionEventRequest(
    string EventId,
    Guid DeviceId,
    string JobId,
    DateTime Timestamp,
    string DetectionType,
    double Confidence,
    double? AnomalyScore,
    string ModelName,
    string ModelVersion,
    string? Description,
    Dictionary<string, object>? Metadata,
    string? SnapshotReference
);

public record MlHeartbeatRequest(
    Guid DeviceId,
    string JobId,
    string WorkerId,
    DeviceStatus Status,
    DateTime? LastSuccessfulInferenceAtUtc,
    string? ErrorMessage
);

public record MlIngestionResponse(
    bool Accepted,
    bool Duplicate,
    Guid? DetectionId,
    Guid? AlertId,
    AlertSeverity? AssignedSeverity,
    NotificationDispatchStatus? NotificationStatus,
    int? NotificationResponseCode,
    string? NotificationError,
    string Message
);

public record DevNotificationTestRequest(
    Guid? DeviceId,
    string Scenario // INFO, WARNING, CRITICAL, REPEATED, ESCALATION, RETRY_SIMULATION
);

public record DevNotificationTestResponse(
    bool EventReachedBackend,
    bool AlertCreated,
    Guid? DeviceId,
    Guid? DetectionId,
    Guid? AlertId,
    AlertSeverity AssignedSeverity,
    bool NotificationAttempted,
    NotificationDispatchStatus NotificationStatus,
    int? NotificationServiceStatusCode,
    string? NotificationServiceResponse,
    string? FailureOrRetryState,
    string Message
);
