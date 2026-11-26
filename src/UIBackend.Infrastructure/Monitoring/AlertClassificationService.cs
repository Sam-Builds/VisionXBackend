using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UIBackend.Application.DTOs;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;

namespace UIBackend.Infrastructure.Monitoring;

public class AlertClassificationService : IAlertClassificationService
{
    private readonly IUIBackendDbContext _dbContext;
    private readonly INotificationOutboxService _outboxService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AlertClassificationService> _logger;

    public AlertClassificationService(
        IUIBackendDbContext dbContext,
        INotificationOutboxService outboxService,
        IConfiguration configuration,
        ILogger<AlertClassificationService> logger)
    {
        _dbContext = dbContext;
        _outboxService = outboxService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<MlIngestionResponse> ProcessDetectionEventAsync(
        MlDetectionEventRequest request,
        bool isTest = false,
        bool simulateTransientFailureOnce = false,
        CancellationToken cancellationToken = default)
    {
        // 1. Validate inputs
        if (string.IsNullOrWhiteSpace(request.EventId) ||
            string.IsNullOrWhiteSpace(request.DetectionType) ||
            request.Confidence < 0.0 || request.Confidence > 1.0 ||
            (request.AnomalyScore.HasValue && (request.AnomalyScore.Value < 0.0 || request.AnomalyScore.Value > 1.0)))
        {
            return new MlIngestionResponse(false, false, null, null, null, null, null, null, "Invalid detection event payload or out-of-range numeric values.");
        }

        // 2. Idempotency check on EventId
        var existingDetection = await _dbContext.DetectionRecords
            .FirstOrDefaultAsync(d => d.EventId == request.EventId, cancellationToken);
        if (existingDetection != null)
        {
            var existingAlert = await _dbContext.AlertRecords
                .FirstOrDefaultAsync(a => a.DetectionId == existingDetection.Id, cancellationToken);

            return new MlIngestionResponse(
                Accepted: true,
                Duplicate: true,
                DetectionId: existingDetection.Id,
                AlertId: existingAlert?.Id,
                AssignedSeverity: existingAlert?.Severity,
                NotificationStatus: existingAlert?.NotificationStatus,
                NotificationResponseCode: null,
                NotificationError: null,
                Message: "Duplicate EventId ignored idempotently."
            );
        }

        // 3. Verify device & job ownership
        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == request.DeviceId && !d.IsDeleted, cancellationToken);

        if (device == null)
        {
            return new MlIngestionResponse(false, false, null, null, null, null, null, null, "Target device not found.");
        }

        if (!isTest)
        {
            var validJob = await _dbContext.MonitoringJobs
                .AnyAsync(j => j.DeviceId == device.Id && j.ExternalJobId == request.JobId, cancellationToken)
                || string.Equals(device.ActiveJobId, request.JobId, StringComparison.Ordinal);

            if (!validJob)
            {
                return new MlIngestionResponse(false, false, null, null, null, null, null, null, "JobId does not belong to the specified device.");
            }

            device.LastSeenAtUtc = DateTime.UtcNow;
            device.LastSuccessfulInferenceAtUtc = request.Timestamp.ToUniversalTime();
            if (device.MonitoringEnabled)
            {
                device.Status = DeviceStatus.Online;
                device.LastError = null;
            }
        }

        // 4. Persist raw DetectionRecord (always preserved with original model values)
        var detection = new DetectionRecord
        {
            Id = Guid.NewGuid(),
            EventId = request.EventId,
            DeviceId = device.Id,
            OwnerUserId = device.OwnerUserId,
            JobId = request.JobId,
            DetectionType = request.DetectionType.Trim(),
            Confidence = request.Confidence,
            AnomalyScore = request.AnomalyScore,
            ModelName = request.ModelName,
            ModelVersion = request.ModelVersion,
            Description = request.Description,
            StructuredMetadataJson = request.Metadata != null ? JsonSerializer.Serialize(request.Metadata) : null,
            SnapshotReference = request.SnapshotReference,
            ObservedAtUtc = request.Timestamp.ToUniversalTime(),
            CreatedAtUtc = DateTime.UtcNow,
            IsTest = isTest
        };
        _dbContext.DetectionRecords.Add(detection);

        // 5. Central Backend Severity Classification Policy (never trust ML-supplied severity)
        var severity = ClassifySeverity(request.DetectionType, request.AnomalyScore, request.Confidence);
        var cooldownSeconds = _configuration.GetValue<int?>("AlertPolicy:NotificationCooldownSeconds") ?? 120;
        var dedupKey = $"{device.Id:N}:{request.DetectionType.Trim().ToLowerInvariant()}:{(isTest ? "test" : "live")}";
        var now = DateTime.UtcNow;

        // 6. Check recent active alert for deduplication / escalation
        var recentAlert = await _dbContext.AlertRecords
            .Where(a => a.DeviceId == device.Id &&
                        a.DeduplicationKey == dedupKey &&
                        a.ResolvedAtUtc == null &&
                        a.LastOccurrenceAtUtc >= now.AddSeconds(-cooldownSeconds))
            .OrderByDescending(a => a.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        AlertRecord alert;
        bool shouldEnqueueNotification = false;
        string notificationActionReason = "Created";

        if (recentAlert != null)
        {
            recentAlert.OccurrenceCount++;
            recentAlert.LastOccurrenceAtUtc = now;
            if (!string.IsNullOrWhiteSpace(request.Description))
            {
                recentAlert.Description = request.Description;
            }

            // Escalation: WARNING -> CRITICAL triggers a fresh notification
            if (recentAlert.Severity == AlertSeverity.Warning && severity == AlertSeverity.Critical)
            {
                recentAlert.Severity = AlertSeverity.Critical;
                recentAlert.Title = BuildAlertTitle(AlertSeverity.Critical, request.DetectionType, device.Name, isTest);
                shouldEnqueueNotification = ShouldNotify(AlertSeverity.Critical);
                notificationActionReason = "Escalated from WARNING to CRITICAL";
            }

            alert = recentAlert;
        }
        else
        {
            var title = BuildAlertTitle(severity, request.DetectionType, device.Name, isTest);
            var desc = BuildDescription(request, device.Name);

            shouldEnqueueNotification = ShouldNotify(severity);

            alert = new AlertRecord
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                OwnerUserId = device.OwnerUserId,
                DetectionId = detection.Id,
                Title = title,
                Description = desc,
                Severity = severity,
                IsRead = false,
                NotificationStatus = shouldEnqueueNotification ? NotificationDispatchStatus.Pending : NotificationDispatchStatus.NotRequired,
                DeduplicationKey = dedupKey,
                OccurrenceCount = 1,
                LastOccurrenceAtUtc = now,
                CreatedAtUtc = now,
                IsTest = isTest
            };
            _dbContext.AlertRecords.Add(alert);
        }

        // 7. Create Outbox item in the same transaction if notification qualifies
        NotificationOutboxItem? outboxItem = null;
        if (shouldEnqueueNotification)
        {
            var outboxEventId = $"alert-{alert.Id:N}-{alert.Severity.ToString().ToLowerInvariant()}";
            var alreadyInOutbox = await _dbContext.NotificationOutboxItems.AnyAsync(o => o.EventId == outboxEventId, cancellationToken);
            if (!alreadyInOutbox)
            {
                var payloadObj = new
                {
                    eventId = outboxEventId,
                    eventType = $"alert.{alert.Severity.ToString().ToLowerInvariant()}",
                    source = isTest ? "VisionXBackend.DevTest" : "VisionXBackend",
                    correlationId = alert.Id.ToString(),
                    title = alert.Title,
                    body = alert.Description,
                    categoryCode = "alerts",
                    routeKey = "alert.details",
                    routeParameters = new Dictionary<string, string>
                    {
                        { "alertId", alert.Id.ToString() },
                        { "deviceId", device.Id.ToString() },
                        { "deviceName", device.Name },
                        { "severity", alert.Severity.ToString().ToUpperInvariant() },
                        { "isTest", isTest.ToString().ToLowerInvariant() }
                    },
                    directRecipientUserIds = new[] { device.OwnerUserId },
                    occurredAt = now
                };

                outboxItem = new NotificationOutboxItem
                {
                    Id = Guid.NewGuid(),
                    AlertId = alert.Id,
                    OwnerUserId = device.OwnerUserId,
                    EventId = outboxEventId,
                    PayloadJson = JsonSerializer.Serialize(payloadObj),
                    Status = NotificationDispatchStatus.Pending,
                    AttemptCount = 0,
                    MaxAttempts = 5,
                    SimulateTransientFailureOnce = simulateTransientFailureOnce,
                    CreatedAtUtc = now
                };
                _dbContext.NotificationOutboxItems.Add(outboxItem);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // 8. Dispatch outbox immediately (if Notifications service fails, alert stays saved & worker retries)
        if (outboxItem != null)
        {
            outboxItem = await _outboxService.DispatchItemAsync(outboxItem.Id, cancellationToken) ?? outboxItem;
        }

        return new MlIngestionResponse(
            Accepted: true,
            Duplicate: false,
            DetectionId: detection.Id,
            AlertId: alert.Id,
            AssignedSeverity: alert.Severity,
            NotificationStatus: alert.NotificationStatus,
            NotificationResponseCode: outboxItem?.LastResponseCode,
            NotificationError: outboxItem?.LastError,
            Message: $"{notificationActionReason}. Severity={alert.Severity}, NotificationStatus={alert.NotificationStatus}."
        );
    }

    private AlertSeverity ClassifySeverity(string detectionType, double? anomalyScore, double confidence)
    {
        var infoMax = _configuration.GetValue<double?>("AlertPolicy:InfoMaxAnomalyScore") ?? 0.40;
        var warningMax = _configuration.GetValue<double?>("AlertPolicy:WarningMaxAnomalyScore") ?? 0.75;
        var criticalTypes = _configuration.GetSection("AlertPolicy:CriticalAnomalyTypes").Get<string[]>()
            ?? new[] { "weapon", "fire", "intrusion", "violence", "fall" };

        var lowerType = detectionType.Trim().ToLowerInvariant();
        if (criticalTypes.Any(t => lowerType.Contains(t.ToLowerInvariant())) && (anomalyScore ?? confidence) >= infoMax)
        {
            return AlertSeverity.Critical;
        }

        var score = anomalyScore ?? confidence;
        if (score >= warningMax) return AlertSeverity.Critical;
        if (score >= infoMax) return AlertSeverity.Warning;
        return AlertSeverity.Info;
    }

    private bool ShouldNotify(AlertSeverity severity)
    {
        return severity switch
        {
            AlertSeverity.Info => _configuration.GetValue<bool?>("AlertPolicy:NotifyOnInfo") ?? false,
            AlertSeverity.Warning => _configuration.GetValue<bool?>("AlertPolicy:NotifyOnWarning") ?? true,
            AlertSeverity.Critical => _configuration.GetValue<bool?>("AlertPolicy:NotifyOnCritical") ?? true,
            _ => false
        };
    }

    private static string BuildAlertTitle(AlertSeverity severity, string detectionType, string deviceName, bool isTest)
    {
        var prefix = isTest ? "[TEST] " : string.Empty;
        var sevLabel = severity switch
        {
            AlertSeverity.Critical => "Critical alert",
            AlertSeverity.Warning => "Warning alert",
            _ => "Info"
        };
        return $"{prefix}{sevLabel} - {deviceName}";
    }

    private static string BuildDescription(MlDetectionEventRequest request, string deviceName)
    {
        if (!string.IsNullOrWhiteSpace(request.Description))
            return request.Description;

        if (request.Metadata != null)
        {
            foreach (var key in new[] { "threats", "reasons" })
            {
                if (request.Metadata.TryGetValue(key, out var raw) && raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
                {
                    var items = el.EnumerateArray()
                        .Select(x => x.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    if (items.Count > 0)
                        return string.Join(" • ", items);
                }
            }
            if (request.Metadata.TryGetValue("actionLabel", out var act) && act != null)
            {
                var label = act.ToString();
                if (!string.IsNullOrWhiteSpace(label))
                    return $"{label} detected on {deviceName} ({request.Confidence:P0} confidence).";
            }
        }

        return $"Unusual {request.DetectionType} activity detected by {deviceName} ({request.Confidence:P0} confidence).";
    }
}
