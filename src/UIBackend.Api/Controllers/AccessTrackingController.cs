using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UIBackend.Application.DTOs;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Persistence;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("media")]
[Authorize]
public class AccessTrackingController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;

    public AccessTrackingController(UIBackendDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("{id:guid}/access-history")]
    public async Task<ActionResult<MediaAccessSummaryResponse>> GetMediaAccessHistory(
        Guid id,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        var media = await _dbContext.MediaItems
            .FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId && !m.IsDeleted, cancellationToken);

        if (media == null)
        {
            return NotFound(new { message = "Media item not found." });
        }

        var safeLimit = Math.Clamp(limit, 1, 200);

        var events = await _dbContext.MediaAccessEvents
            .Where(e => e.MediaItemId == id)
            .OrderByDescending(e => e.AccessedAtUtc)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        var totalViews = await _dbContext.MediaAccessEvents.CountAsync(e => e.MediaItemId == id && e.EventType == AccessEventType.View, cancellationToken);
        var totalPlays = await _dbContext.MediaAccessEvents.CountAsync(e => e.MediaItemId == id && e.EventType == AccessEventType.Play, cancellationToken);
        var totalDownloads = await _dbContext.MediaAccessEvents.CountAsync(e => e.MediaItemId == id && e.EventType == AccessEventType.Download, cancellationToken);
        var totalVerifications = await _dbContext.MediaAccessEvents.CountAsync(e => e.MediaItemId == id && e.EventType == AccessEventType.Verify, cancellationToken);

        var items = events.Select(e => new AccessHistoryItemResponse(
            Id: e.Id,
            MediaItemId: e.MediaItemId,
            EventType: e.EventType,
            IpAddress: e.IpAddress,
            UserAgent: e.UserAgent,
            Referer: e.Referer,
            IsCrawlerOrBot: e.IsCrawlerOrBot,
            AccessingUserId: e.AccessingUserId,
            AccessedAtUtc: e.AccessedAtUtc
        )).ToList();

        return Ok(new MediaAccessSummaryResponse(
            MediaItemId: id,
            TotalViews: totalViews,
            TotalPlays: totalPlays,
            TotalDownloads: totalDownloads,
            TotalVerifications: totalVerifications,
            RecentEvents: items
        ));
    }

    private string GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier) ??
               User.FindFirstValue("sub") ??
               "anonymous_user";
    }
}
