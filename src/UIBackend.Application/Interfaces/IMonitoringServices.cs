using UIBackend.Application.DTOs;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;

namespace UIBackend.Application.Interfaces;

public interface IFeedUrlValidator
{
    Task<(bool IsValid, string? ErrorMessage)> ValidateAsync(string feedUrl, CancellationToken cancellationToken = default);
}

public interface IStreamCredentialProtector
{
    string? EncryptCredentials(string? username, string? password);
    (string? Username, string? Password) DecryptCredentials(string? encryptedBlob);
}

public record MlStartResult(
    bool Success,
    bool Reachable,
    string? JobId,
    string? WorkerId,
    DeviceStatus ResultingStatus,
    string? ErrorMessage
);

public record MlStopResult(
    bool Success,
    bool Reachable,
    string? ErrorMessage
);

public record MlStatusResult(
    bool Reachable,
    DeviceStatus Status,
    string? WorkerId,
    DateTime? LastSuccessfulInferenceAtUtc,
    string? ErrorMessage
);

public interface IMlServiceClient
{
    Task<MlStartResult> StartMonitoringAsync(
        MonitoredDevice device,
        string? username,
        string? password,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<MlStopResult> StopMonitoringAsync(
        Guid deviceId,
        string jobId,
        CancellationToken cancellationToken = default);

    Task<MlStatusResult> GetJobStatusAsync(
        Guid deviceId,
        string jobId,
        CancellationToken cancellationToken = default);
}

public interface IAlertClassificationService
{
    Task<MlIngestionResponse> ProcessDetectionEventAsync(
        MlDetectionEventRequest request,
        bool isTest = false,
        bool simulateTransientFailureOnce = false,
        CancellationToken cancellationToken = default);
}

public interface INotificationOutboxService
{
    Task<NotificationOutboxItem?> DispatchItemAsync(
        Guid outboxId,
        CancellationToken cancellationToken = default);

    Task<int> ProcessPendingBatchAsync(CancellationToken cancellationToken = default);
}
