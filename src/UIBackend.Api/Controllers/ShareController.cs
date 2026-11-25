using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UIBackend.Application.DTOs;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Persistence;
using UIBackend.Infrastructure.Security;
using UIBackend.Infrastructure.Verification;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("")]
public class ShareController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;
    private readonly IMediaStorageService _storageService;
    private readonly INotificationServiceClient _notificationClient;
    private readonly IVerificationOrchestrator _orchestrator;
    private readonly ILogger<ShareController> _logger;

    public ShareController(
        UIBackendDbContext dbContext,
        IMediaStorageService storageService,
        INotificationServiceClient notificationClient,
        IVerificationOrchestrator orchestrator,
        ILogger<ShareController> logger)
    {
        _dbContext = dbContext;
        _storageService = storageService;
        _notificationClient = notificationClient;
        _orchestrator = orchestrator;
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
        var tokenHash = ComputeTokenHash(token);

        var shareLink = new ShareLink
        {
            Id = Guid.NewGuid(),
            MediaItemId = mediaItem.Id,
            CreatedByUserId = userId,
            Token = token,
            ShareTokenHash = tokenHash,
            IsPublic = request.IsPublic,
            AllowDownload = request.AllowDownload,
            ExpiresAtUtc = expiresAt,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.ShareLinks.Add(shareLink);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var (scheme, host, pathBase) = GetRequestOrigin();
        var shareUrl = $"{scheme}://{host}{pathBase}/s/{token}";

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

    [HttpPost("media/{id:guid}/share/{shareId:guid}/revoke")]
    [Authorize]
    public async Task<IActionResult> RevokeShareLink(
        Guid id,
        Guid shareId,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var shareLink = await _dbContext.ShareLinks
            .FirstOrDefaultAsync(s => s.Id == shareId && s.MediaItemId == id && s.CreatedByUserId == userId, cancellationToken);

        if (shareLink == null)
        {
            return NotFound(new { message = "Share link not found." });
        }

        shareLink.IsRevoked = true;
        shareLink.RevokedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { message = "Share link revoked." });
    }

    [HttpGet("s/{token}")]
    [HttpGet("share/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetMediaFirstSharePage(
        string token,
        CancellationToken cancellationToken = default)
    {
        var (shareLink, errorResult) = await ValidateShareLinkAsync(token, cancellationToken);
        if (errorResult != null) return errorResult;

        var media = shareLink!.MediaItem;
        await RecordAccessAndNotifyAsync(shareLink, AccessEventType.View, cancellationToken);

        var (scheme, host, pathBase) = GetRequestOrigin();
        var baseUrl = $"{scheme}://{host}{pathBase}/s/{token}";
        var mediaUrl = $"{baseUrl}/media";
        var downloadUrl = $"{baseUrl}/media?download=true";

        var statusLabel = media.SourceType == SourceType.Camera
            ? "Registered VisionX Capture"
            : "Imported & Registered by VisionX";
        var statusColor = media.SourceType == SourceType.Camera ? "#2e7d32" : "#6b705c";
        var safeTitle = WebUtility.HtmlEncode(media.OriginalFileName);

        var mediaTag = media.MediaType == MediaType.Video
            ? $@"<video controls playsinline preload=""metadata"" src=""{mediaUrl}"" style=""max-width:100%;max-height:78vh;border-radius:12px;background:#000;""></video>"
            : $@"<img src=""{mediaUrl}"" alt=""{safeTitle}"" style=""max-width:100%;max-height:78vh;object-fit:contain;border-radius:12px;"" />";

        var ogMediaTag = media.MediaType == MediaType.Video
            ? $@"<meta property=""og:video"" content=""{mediaUrl}"" /><meta property=""og:type"" content=""video.other"" />"
            : $@"<meta property=""og:image"" content=""{mediaUrl}"" /><meta property=""og:type"" content=""website"" />";

        var downloadBtn = shareLink.AllowDownload
            ? $@"<a href=""{downloadUrl}"" class=""btn btn-primary"">Download Original</a>"
            : string.Empty;

        var html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"" />
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"" />
  <title>{safeTitle} — VisionX</title>
  <meta property=""og:title"" content=""{safeTitle}"" />
  <meta property=""og:description"" content=""{statusLabel} — Shared via VisionX"" />
  {ogMediaTag}
  <style>
    body {{ margin:0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background:#141613; color:#f7f8f4; display:flex; flex-direction:column; min-height:100vh; }}
    .stage {{ flex:1; display:flex; align-items:center; justify-content:center; padding:24px; }}
    .bar {{ padding:14px 24px; background:#1d211c; border-top:1px solid #2c3228; display:flex; flex-wrap:wrap; align-items:center; justify-content:space-between; gap:12px; }}
    .status {{ display:inline-flex; align-items:center; gap:8px; padding:6px 12px; border-radius:999px; font-size:12.5px; font-weight:600; background:rgba(255,255,255,0.08); color:#f7f8f4; border:1px solid {statusColor}; }}
    .actions {{ display:flex; flex-wrap:wrap; gap:10px; align-items:center; }}
    .btn {{ text-decoration:none; padding:8px 14px; border-radius:8px; font-size:13px; font-weight:600; color:#f7f8f4; background:#252a23; border:1px solid #333a2f; }}
    .btn-primary {{ background:#d4a373; color:#1c1813; border-color:#d4a373; }}
    .meta-links {{ font-size:12px; color:#a3a99b; display:flex; gap:12px; }}
    .meta-links a {{ color:#ccd5ae; text-decoration:none; }}
  </style>
</head>
<body>
  <main class=""stage"">
    {mediaTag}
  </main>
  <footer class=""bar"">
    <div>
      <span class=""status"">{statusLabel}</span>
    </div>
    <div class=""actions"">
      {downloadBtn}
      <div class=""meta-links"">
        <a href=""{baseUrl}/verification.json"" target=""_blank"">Verification JSON</a>
        <a href=""{baseUrl}/provenance.json"" target=""_blank"">Provenance JSON</a>
        <a href=""{baseUrl}/metadata.json"" target=""_blank"">Metadata JSON</a>
      </div>
    </div>
  </footer>
</body>
</html>";

        return Content(html, "text/html; charset=utf-8");
    }

    [HttpGet("s/{token}/media")]
    [HttpGet("share/{token}/media")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSharedMediaFile(
        string token,
        [FromQuery] bool download = false,
        CancellationToken cancellationToken = default)
    {
        var (shareLink, errorResult) = await ValidateShareLinkAsync(token, cancellationToken);
        if (errorResult != null) return errorResult;

        if (download && !shareLink!.AllowDownload)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Download is disabled for this share link." });
        }

        var media = shareLink!.MediaItem;
        var eventType = download
            ? AccessEventType.Download
            : (media.MediaType == MediaType.Video ? AccessEventType.Play : AccessEventType.View);

        await RecordAccessAndNotifyAsync(shareLink, eventType, cancellationToken);

        Response.Headers.CacheControl = shareLink.IsPublic ? "public, max-age=300" : "private, no-store";

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

    [HttpGet("s/{token}/provenance.json")]
    [HttpGet("share/{token}/provenance.json")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSharedProvenanceJson(
        string token,
        CancellationToken cancellationToken = default)
    {
        var (shareLink, errorResult) = await ValidateShareLinkAsync(token, cancellationToken);
        if (errorResult != null) return errorResult;

        var media = shareLink!.MediaItem;
        var capture = media.CaptureRecord;

        // Safe public disclosure only: NO private user IDs, NO private device keys, NO internal storage paths
        return Ok(new
        {
            schemaVersion = "2.0",
            sourceClassification = media.SourceType == SourceType.Camera
                ? "Registered as captured through VisionX"
                : "Imported and registered by VisionX",
            originalSha256 = media.OriginalSha256,
            signedSha256 = media.SignedSha256,
            c2pa = new
            {
                status = capture?.C2paStatus.ToString() ?? "NoCredentials",
                issuer = capture?.C2paIssuer ?? "None",
                algorithm = capture?.C2paClaimAlgorithm ?? "None",
                manifest = string.IsNullOrWhiteSpace(capture?.C2paManifestJson)
                    ? (System.Text.Json.JsonElement?)null
                    : System.Text.Json.JsonDocument.Parse(capture.C2paManifestJson).RootElement
            },
            capturedAtUtc = capture?.CapturedAtUtc ?? media.CreatedAtUtc
        });
    }

    [HttpGet("s/{token}/verification.json")]
    [HttpGet("share/{token}/verification.json")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSharedVerificationJson(
        string token,
        CancellationToken cancellationToken = default)
    {
        var (shareLink, errorResult) = await ValidateShareLinkAsync(token, cancellationToken);
        if (errorResult != null) return errorResult;

        var media = shareLink!.MediaItem;
        var latestRunId = await _dbContext.VerificationRuns
            .Where(r => r.MediaItemId == media.Id)
            .OrderByDescending(r => r.StartedAtUtc)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestRunId.HasValue)
        {
            var report = await _orchestrator.GetVerificationRunAsync(latestRunId.Value, cancellationToken);
            if (report != null) return Ok(report);
        }

        // Compute fresh independent verification on demand if none persisted yet
        await using var stream = await _storageService.GetMediaStreamAsync(media.StoragePath, cancellationToken);
        if (stream == null) return NotFound(new { message = "Media file missing on storage." });

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        var freshReport = await _orchestrator.VerifyMediaAsync(ms.ToArray(), media.OriginalFileName, media.MimeType, null, cancellationToken);
        return Ok(freshReport);
    }

    [HttpGet("s/{token}/metadata.json")]
    [HttpGet("share/{token}/metadata.json")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSharedMetadataJson(
        string token,
        CancellationToken cancellationToken = default)
    {
        var (shareLink, errorResult) = await ValidateShareLinkAsync(token, cancellationToken);
        if (errorResult != null) return errorResult;

        var media = shareLink!.MediaItem;
        return Ok(new
        {
            fileName = media.OriginalFileName,
            mimeType = media.MimeType,
            mediaType = media.MediaType.ToString(),
            fileSizeBytes = media.FileSizeBytes,
            sourceType = media.SourceType.ToString(),
            allowDownload = shareLink.AllowDownload,
            expiresAtUtc = shareLink.ExpiresAtUtc,
            createdAtUtc = media.CreatedAtUtc
        });
    }

    private async Task<(ShareLink? ShareLink, IActionResult? ErrorResult)> ValidateShareLinkAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var shareLink = await _dbContext.ShareLinks
            .Include(s => s.MediaItem)
            .ThenInclude(m => m.CaptureRecord)
            .FirstOrDefaultAsync(s => s.Token == token && !s.IsRevoked, cancellationToken);

        if (shareLink == null)
        {
            return (null, NotFound(new { message = "Share link not found or has been revoked." }));
        }

        if (shareLink.ExpiresAtUtc.HasValue && shareLink.ExpiresAtUtc.Value < DateTime.UtcNow)
        {
            return (null, StatusCode(StatusCodes.Status410Gone, new { message = "Share link has expired." }));
        }

        if (shareLink.MediaItem.IsDeleted)
        {
            return (null, NotFound(new { message = "The shared media has been removed by the owner." }));
        }

        if (!shareLink.IsPublic && !(User.Identity?.IsAuthenticated ?? false))
        {
            return (null, Unauthorized(new { message = "Authentication required for private share link." }));
        }

        return (shareLink, null);
    }

    private async Task RecordAccessAndNotifyAsync(
        ShareLink shareLink,
        AccessEventType eventType,
        CancellationToken cancellationToken)
    {
        var media = shareLink.MediaItem;
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();
        var isCrawler = BotDetector.IsCrawlerOrBot(userAgent);
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

        // Do not count owner's own access or automated crawler previews as human view count
        if (!isCrawler && currentUserId != media.UserId)
        {
            shareLink.AccessCount++;
        }

        var accessEvent = new MediaAccessEvent
        {
            Id = Guid.NewGuid(),
            MediaItemId = media.Id,
            ShareLinkId = shareLink.Id,
            AccessingUserId = currentUserId,
            EventType = eventType,
            IpAddress = clientIp,
            UserAgent = userAgent,
            Referer = Request.Headers.Referer.ToString(),
            IsCrawlerOrBot = isCrawler,
            OwnerNotified = false,
            AccessedAtUtc = DateTime.UtcNow
        };

        _dbContext.MediaAccessEvents.Add(accessEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!isCrawler && currentUserId != media.UserId && shareLink.OwnerNotificationEnabled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _notificationClient.NotifyOwnerMediaAccessedAsync(
                        media.UserId,
                        media.Id,
                        media.OriginalFileName,
                        eventType,
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
    }

    private (string Scheme, string Host, string PathBase) GetRequestOrigin()
    {
        var scheme = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? Request.Scheme;
        var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? Request.Host.Value ?? "visionx.samstack.site";
        var pathBase = Request.PathBase.Value ?? "/a";
        return (scheme, host, pathBase);
    }

    private string GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier) ??
               User.FindFirstValue("sub") ??
               "anonymous_user";
    }

    private static string ComputeTokenHash(string token)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(Encoding.UTF8.GetBytes(token)));
    }
}
