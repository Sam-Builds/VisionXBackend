using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UIBackend.Application.DTOs;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Persistence;
using UIBackend.Infrastructure.Security;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("")]
public class ShareController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;
    private readonly IMediaStorageService _storageService;
    private readonly INotificationServiceClient _notificationClient;
    private readonly ILogger<ShareController> _logger;

    public ShareController(
        UIBackendDbContext dbContext,
        IMediaStorageService storageService,
        INotificationServiceClient notificationClient,
        ILogger<ShareController> logger)
    {
        _dbContext = dbContext;
        _storageService = storageService;
        _notificationClient = notificationClient;
        _logger = logger;
    }

    [HttpPost("media/{id:guid}/share")]
    [Authorize]
    public async Task<ActionResult<ShareLinkResponse>> CreateShareLink(
        Guid id,
        [FromBody] CreateShareLinkRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        var mediaItem = await _dbContext.MediaItems
            .FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId && !m.IsDeleted, cancellationToken);

        if (mediaItem == null)
        {
            return NotFound(new { message = "Media item not found." });
        }

        DateTime? expiresAt = request.ExpiresInDays.HasValue && request.ExpiresInDays.Value > 0
            ? DateTime.UtcNow.AddDays(request.ExpiresInDays.Value)
            : null;

        var token = Guid.NewGuid().ToString("N");
        var shareLink = new ShareLink
        {
            Id = Guid.NewGuid(),
            MediaItemId = mediaItem.Id,
            CreatedByUserId = userId,
            Token = token,
            IsPublic = request.IsPublic,
            AllowDownload = request.AllowDownload,
            ExpiresAtUtc = expiresAt,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.ShareLinks.Add(shareLink);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? Request.Host.Value;
        var pathBase = Request.PathBase.Value ?? "/a";
        var shareUrl = $"{scheme}://{host}{pathBase}/share/{token}";

        return Ok(new ShareLinkResponse(
            Id: shareLink.Id,
            MediaItemId: mediaItem.Id,
            Token: token,
            ShareUrl: shareUrl,
            IsPublic: shareLink.IsPublic,
            AllowDownload: shareLink.AllowDownload,
            ExpiresAtUtc: shareLink.ExpiresAtUtc,
            AccessCount: 0,
            IsRevoked: false,
            CreatedAtUtc: shareLink.CreatedAtUtc
        ));
    }

    [HttpGet("share/{token}")]
    [AllowAnonymous]
    public async Task<ActionResult<PublicShareViewResponse>> GetSharedMediaInfo(
        string token,
        CancellationToken cancellationToken = default)
    {
        var shareLink = await _dbContext.ShareLinks
            .Include(s => s.MediaItem)
            .ThenInclude(m => m.CaptureRecord)
            .FirstOrDefaultAsync(s => s.Token == token && !s.IsRevoked, cancellationToken);

        if (shareLink == null)
        {
            return NotFound(new { message = "Share link not found or has been revoked." });
        }

        if (shareLink.ExpiresAtUtc.HasValue && shareLink.ExpiresAtUtc.Value < DateTime.UtcNow)
        {
            return StatusCode(StatusCodes.Status410Gone, new { message = "Share link has expired." });
        }

        var media = shareLink.MediaItem;
        if (media.IsDeleted)
        {
            return NotFound(new { message = "The shared media has been removed by owner." });
        }

        // Record Access Event
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();
        var isCrawler = BotDetector.IsCrawlerOrBot(userAgent);
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

        shareLink.AccessCount++;

        var accessEvent = new MediaAccessEvent
        {
            Id = Guid.NewGuid(),
            MediaItemId = media.Id,
            ShareLinkId = shareLink.Id,
            AccessingUserId = currentUserId,
            EventType = AccessEventType.View,
            IpAddress = clientIp,
            UserAgent = userAgent,
            Referer = Request.Headers.Referer.ToString(),
            IsCrawlerOrBot = isCrawler,
            OwnerNotified = false,
            AccessedAtUtc = DateTime.UtcNow
        };

        _dbContext.MediaAccessEvents.Add(accessEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Notify owner if not crawler and not self
        if (!isCrawler && currentUserId != media.UserId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _notificationClient.NotifyOwnerMediaAccessedAsync(
                        media.UserId,
                        media.Id,
                        media.OriginalFileName,
                        AccessEventType.View,
                        clientIp,
                        currentUserId
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send background notification to owner.");
                }
            });
        }

        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? Request.Host.Value;
        var pathBase = Request.PathBase.Value ?? "/a";

        var mediaUrl = $"{scheme}://{host}{pathBase}/share/{token}/media?download=true";
        var streamUrl = $"{scheme}://{host}{pathBase}/share/{token}/media";

        return Ok(new PublicShareViewResponse(
            Token: token,
            MediaItemId: media.Id,
            FileName: media.OriginalFileName,
            MimeType: media.MimeType,
            MediaType: media.MediaType,
            FileSizeBytes: media.FileSizeBytes,
            SourceType: media.SourceType,
            CapturedAtUtc: media.CaptureRecord?.CapturedAtUtc ?? media.CreatedAtUtc,
            C2paStatus: media.CaptureRecord?.C2paStatus ?? C2paStatus.NoCredentials,
            SynthIdStatus: media.CaptureRecord?.SynthIdStatus ?? SynthIdStatus.NotDetected,
            SynthIdConfidence: media.CaptureRecord?.SynthIdConfidence,
            C2paIssuer: media.CaptureRecord?.C2paIssuer ?? "VisionX Trust Authority",
            OriginalSha256: media.OriginalSha256,
            AllowDownload: shareLink.AllowDownload,
            MediaUrl: mediaUrl,
            StreamUrl: streamUrl
        ));
    }

    [HttpGet("share/{token}/media")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSharedMediaFile(
        string token,
        [FromQuery] bool download = false,
        CancellationToken cancellationToken = default)
    {
        var shareLink = await _dbContext.ShareLinks
            .Include(s => s.MediaItem)
            .FirstOrDefaultAsync(s => s.Token == token && !s.IsRevoked, cancellationToken);

        if (shareLink == null)
        {
            return NotFound(new { message = "Share link not found or has been revoked." });
        }

        if (shareLink.ExpiresAtUtc.HasValue && shareLink.ExpiresAtUtc.Value < DateTime.UtcNow)
        {
            return StatusCode(StatusCodes.Status410Gone, new { message = "Share link has expired." });
        }

        if (download && !shareLink.AllowDownload)
        {
            return Forbid("Download is disabled for this share link.");
        }

        var media = shareLink.MediaItem;
        if (media.IsDeleted)
        {
            return NotFound(new { message = "The shared media has been removed." });
        }

        // Record play/download event
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();
        var isCrawler = BotDetector.IsCrawlerOrBot(userAgent);
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var eventType = download ? AccessEventType.Download : (media.MediaType == MediaType.Video ? AccessEventType.Play : AccessEventType.View);

        var accessEvent = new MediaAccessEvent
        {
            Id = Guid.NewGuid(),
            MediaItemId = media.Id,
            ShareLinkId = shareLink.Id,
            AccessingUserId = currentUserId,
            EventType = eventType,
            IpAddress = clientIp,
            UserAgent = userAgent,
            IsCrawlerOrBot = isCrawler,
            OwnerNotified = false,
            AccessedAtUtc = DateTime.UtcNow
        };
        _dbContext.MediaAccessEvents.Add(accessEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!isCrawler && currentUserId != media.UserId && eventType == AccessEventType.Download)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _notificationClient.NotifyOwnerMediaAccessedAsync(
                        media.UserId,
                        media.Id,
                        media.OriginalFileName,
                        AccessEventType.Download,
                        clientIp,
                        currentUserId
                    );
                }
                catch { /* ignore */ }
            });
        }

        if (download)
        {
            var stream = await _storageService.GetMediaStreamAsync(media.StoragePath, cancellationToken);
            if (stream == null) return NotFound(new { message = "Media file not found on disk." });
            return File(stream, media.MimeType, media.OriginalFileName);
        }

        var rangeHeader = Request.Headers.Range.ToString();
        var rangeResult = await _storageService.GetMediaRangeStreamAsync(media.StoragePath, rangeHeader, cancellationToken);
        if (rangeResult == null) return NotFound(new { message = "Media file not found on disk." });

        var (rangeStream, totalLength, start, end) = rangeResult.Value;
        Response.Headers.AcceptRanges = "bytes";

        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
            return File(rangeStream, media.MimeType, enableRangeProcessing: false);
        }

        return File(rangeStream, media.MimeType);
    }

    private string GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier) ??
               User.FindFirstValue("sub") ??
               "anonymous_user";
    }
}
