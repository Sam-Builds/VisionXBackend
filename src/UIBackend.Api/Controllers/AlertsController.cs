using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UIBackend.Application.DTOs;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Persistence;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("api/alerts")]
[Authorize]
public class AlertsController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;

    public AlertsController(UIBackendDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResponse<AlertDto>>> GetAlerts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] AlertSeverity? severity = null,
        [FromQuery] bool? unreadOnly = null,
        [FromQuery] Guid? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var query = _dbContext.AlertRecords
            .Include(a => a.Device)
            .Where(a => a.OwnerUserId == userId);

        if (severity.HasValue) query = query.Where(a => a.Severity == severity.Value);
        if (unreadOnly == true) query = query.Where(a => !a.IsRead);
        if (deviceId.HasValue) query = query.Where(a => a.DeviceId == deviceId.Value);

        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(a => new AlertDto(
                a.Id, a.DeviceId, a.Device.Name, a.DetectionId,
                a.Title, a.Description, a.Severity, a.IsRead,
                a.NotificationStatus, a.NotificationId, a.OccurrenceCount,
                a.LastOccurrenceAtUtc, a.CreatedAtUtc, a.IsTest
            ))
            .ToListAsync(cancellationToken);

        return Ok(new PagedResponse<AlertDto>(items, total, safePage, safeSize));
    }

    [HttpGet("{alertId:guid}")]
    public async Task<ActionResult<AlertDetailDto>> GetAlertDetail(Guid alertId, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var alert = await _dbContext.AlertRecords
            .Include(a => a.Device)
            .Include(a => a.Detection)
            .FirstOrDefaultAsync(a => a.Id == alertId && a.OwnerUserId == userId, cancellationToken);

        if (alert == null) return NotFound(new { message = "Alert not found." });

        var outbox = await _dbContext.NotificationOutboxItems
            .Where(o => o.AlertId == alert.Id)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var alertDto = new AlertDto(
            alert.Id, alert.DeviceId, alert.Device.Name, alert.DetectionId,
            alert.Title, alert.Description, alert.Severity, alert.IsRead,
            alert.NotificationStatus, alert.NotificationId, alert.OccurrenceCount,
            alert.LastOccurrenceAtUtc, alert.CreatedAtUtc, alert.IsTest
        );

        var detectionDto = new DetectionDto(
            alert.Detection.Id, alert.Detection.EventId, alert.Detection.DeviceId,
            alert.Detection.JobId, alert.Detection.DetectionType,
            alert.Detection.Confidence, alert.Detection.AnomalyScore,
            alert.Detection.ModelName, alert.Detection.ModelVersion,
            alert.Detection.Description, alert.Detection.SnapshotReference,
            alert.Detection.ObservedAtUtc, alert.Detection.IsTest
        );

        OutboxDispatchTraceDto? traceDto = outbox == null
            ? null
            : new OutboxDispatchTraceDto(
                outbox.Id, outbox.Status, outbox.AttemptCount,
                outbox.LastResponseCode, outbox.LastResponseBody,
                outbox.LastError, outbox.NextRetryAtUtc, outbox.SentAtUtc
            );

        return Ok(new AlertDetailDto(alertDto, detectionDto, traceDto));
    }

    [HttpPost("{alertId:guid}/read")]
    public async Task<IActionResult> MarkAlertRead(Guid alertId, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var alert = await _dbContext.AlertRecords
            .FirstOrDefaultAsync(a => a.Id == alertId && a.OwnerUserId == userId, cancellationToken);

        if (alert == null) return NotFound(new { message = "Alert not found." });

        alert.IsRead = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Alert marked as read." });
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var unread = await _dbContext.AlertRecords
            .Where(a => a.OwnerUserId == userId && !a.IsRead)
            .ToListAsync(cancellationToken);

        foreach (var a in unread) a.IsRead = true;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { updated = unread.Count });
    }

    private string GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier) ??
               User.FindFirstValue("sub") ??
               "anonymous_user";
    }
}
