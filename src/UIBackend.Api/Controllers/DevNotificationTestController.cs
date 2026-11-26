using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UIBackend.Application.DTOs;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Persistence;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("api/dev/notifications-test")]
[Authorize]
public class DevNotificationTestController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;
    private readonly IAlertClassificationService _classificationService;
    private readonly INotificationOutboxService _outboxService;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _configuration;

    public DevNotificationTestController(
        UIBackendDbContext dbContext,
        IAlertClassificationService classificationService,
        INotificationOutboxService outboxService,
        IWebHostEnvironment env,
        IConfiguration configuration)
    {
        _dbContext = dbContext;
        _classificationService = classificationService;
        _outboxService = outboxService;
        _env = env;
        _configuration = configuration;
    }

    private bool IsTestEndpointEnabled =>
        _env.IsDevelopment() || _configuration.GetValue<bool>("Monitoring:EnableDevTestEndpoints");

    [HttpPost("trigger")]
    public async Task<ActionResult<DevNotificationTestResponse>> TriggerTestNotification(
        [FromBody] DevNotificationTestRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsTestEndpointEnabled)
        {
            return NotFound(new { message = "Development notification testing is disabled in this environment." });
        }

        var userId = GetCurrentUserId();
        var device = request.DeviceId.HasValue
            ? await _dbContext.MonitoredDevices.FirstOrDefaultAsync(d => d.Id == request.DeviceId.Value && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken)
            : await _dbContext.MonitoredDevices.FirstOrDefaultAsync(d => d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);

        if (device == null)
        {
            device = new MonitoredDevice
            {
                Id = Guid.NewGuid(),
                OwnerUserId = userId,
                Name = "[TEST] Virtual Test Camera",
                FeedUrl = "https://visionx.samstack.site/webinput/stream/test-device",
                Status = DeviceStatus.Online,
                MonitoringEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            _dbContext.MonitoredDevices.Add(device);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var scenario = (request.Scenario ?? "WARNING").Trim().ToUpperInvariant();
        var eventId = $"devtest-{Guid.NewGuid():N}";

        MlDetectionEventRequest mlReq;
        bool simulateTransientFailureOnce = false;

        switch (scenario)
        {
            case "INFO":
                mlReq = new MlDetectionEventRequest(
                    eventId, device.Id, "dev-test-job", DateTime.UtcNow,
                    "motion_low", 0.65, 0.20, "VisionX-DevTest", "1.0",
                    "[TEST] Informational low-anomaly event (should NOT send push notification).", null, null);
                break;

            case "CRITICAL":
                mlReq = new MlDetectionEventRequest(
                    eventId, device.Id, "dev-test-job", DateTime.UtcNow,
                    "intrusion", 0.96, 0.92, "VisionX-DevTest", "1.0",
                    $"[TEST] Critical intrusion detected on {device.Name}.", null, null);
                break;

            case "REPEATED":
                // First event creates alert, second within cooldown aggregates without duplicate push
                await _classificationService.ProcessDetectionEventAsync(
                    new MlDetectionEventRequest(
                        $"devtest-rep1-{Guid.NewGuid():N}", device.Id, "dev-test-job", DateTime.UtcNow,
                        "repeated_person", 0.85, 0.55, "VisionX-DevTest", "1.0",
                        $"[TEST] Repeated activity detected on {device.Name} (1/2).", null, null),
                    isTest: true, cancellationToken: cancellationToken);

                mlReq = new MlDetectionEventRequest(
                    $"devtest-rep2-{Guid.NewGuid():N}", device.Id, "dev-test-job", DateTime.UtcNow,
                    "repeated_person", 0.87, 0.58, "VisionX-DevTest", "1.0",
                    $"[TEST] Repeated activity aggregated on {device.Name} (2/2, no duplicate push).", null, null);
                break;

            case "ESCALATION":
                await _classificationService.ProcessDetectionEventAsync(
                    new MlDetectionEventRequest(
                        $"devtest-esc1-{Guid.NewGuid():N}", device.Id, "dev-test-job", DateTime.UtcNow,
                        "escalation_zone", 0.78, 0.52, "VisionX-DevTest", "1.0",
                        $"[TEST] Initial WARNING in zone on {device.Name}.", null, null),
                    isTest: true, cancellationToken: cancellationToken);

                mlReq = new MlDetectionEventRequest(
                    $"devtest-esc2-{Guid.NewGuid():N}", device.Id, "dev-test-job", DateTime.UtcNow,
                    "escalation_zone", 0.95, 0.90, "VisionX-DevTest", "1.0",
                    $"[TEST] Escalated from WARNING to CRITICAL on {device.Name}.", null, null);
                break;

            case "RETRY_SIMULATION":
                simulateTransientFailureOnce = true;
                mlReq = new MlDetectionEventRequest(
                    eventId, device.Id, "dev-test-job", DateTime.UtcNow,
                    "retry_probe", 0.91, 0.82, "VisionX-DevTest", "1.0",
                    $"[TEST] Notification retry test on {device.Name} (simulates 503 then schedules outbox retry).", null, null);
                break;

            default: // WARNING
                mlReq = new MlDetectionEventRequest(
                    eventId, device.Id, "dev-test-job", DateTime.UtcNow,
                    "unusual_activity", 0.84, 0.55, "VisionX-DevTest", "1.0",
                    $"[TEST] Warning activity detected by {device.Name}.", null, null);
                break;
        }

        var result = await _classificationService.ProcessDetectionEventAsync(
            mlReq,
            isTest: true,
            simulateTransientFailureOnce: simulateTransientFailureOnce,
            cancellationToken: cancellationToken);

        NotificationOutboxItem? outbox = result.AlertId.HasValue
            ? await _dbContext.NotificationOutboxItems
                .Where(o => o.AlertId == result.AlertId.Value)
                .OrderByDescending(o => o.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return Ok(new DevNotificationTestResponse(
            EventReachedBackend: true,
            AlertCreated: result.AlertId.HasValue,
            DeviceId: device.Id,
            DetectionId: result.DetectionId,
            AlertId: result.AlertId,
            AssignedSeverity: result.AssignedSeverity ?? AlertSeverity.Info,
            NotificationAttempted: outbox != null,
            NotificationStatus: result.NotificationStatus ?? NotificationDispatchStatus.NotRequired,
            NotificationServiceStatusCode: outbox?.LastResponseCode,
            NotificationServiceResponse: outbox?.LastResponseBody,
            FailureOrRetryState: outbox?.LastError,
            Message: result.Message
        ));
    }

    [HttpGet("outbox")]
    public async Task<ActionResult<IReadOnlyList<OutboxDispatchTraceDto>>> GetRecentOutboxTraces(
        CancellationToken cancellationToken = default)
    {
        if (!IsTestEndpointEnabled)
        {
            return NotFound(new { message = "Development notification testing is disabled in this environment." });
        }

        var userId = GetCurrentUserId();
        var items = await _dbContext.NotificationOutboxItems
            .Where(o => o.OwnerUserId == userId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(25)
            .Select(o => new OutboxDispatchTraceDto(
                o.Id, o.Status, o.AttemptCount, o.LastResponseCode,
                o.LastResponseBody, o.LastError, o.NextRetryAtUtc, o.SentAtUtc))
            .ToListAsync(cancellationToken);

        return Ok(items);
    }

    [HttpPost("outbox/flush")]
    public async Task<IActionResult> FlushPendingOutbox(CancellationToken cancellationToken = default)
    {
        if (!IsTestEndpointEnabled)
        {
            return NotFound(new { message = "Development notification testing is disabled in this environment." });
        }

        var count = await _outboxService.ProcessPendingBatchAsync(cancellationToken);
        return Ok(new { flushedCount = count });
    }

    private string GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier) ??
               User.FindFirstValue("sub") ??
               "anonymous_user";
    }
}
