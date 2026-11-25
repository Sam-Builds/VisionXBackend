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
[Route("captures")]
[Authorize]
public class CapturesController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;
    private readonly IMediaStorageService _storageService;
    private readonly ILogger<CapturesController> _logger;

    public CapturesController(
        UIBackendDbContext dbContext,
        IMediaStorageService storageService,
        ILogger<CapturesController> logger)
    {
        _dbContext = dbContext;
        _storageService = storageService;
        _logger = logger;
    }

    [HttpPost("register")]
    public async Task<ActionResult<CaptureResponse>> RegisterCapture(
        [FromBody] RegisterCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        if (!request.MediaItemId.HasValue)
        {
            return BadRequest(new { message = "MediaItemId is required to bind capture record." });
        }

        var mediaItem = await _dbContext.MediaItems
            .Include(m => m.CaptureRecord)
            .FirstOrDefaultAsync(m => m.Id == request.MediaItemId.Value && m.UserId == userId, cancellationToken);

        if (mediaItem == null)
        {
            return NotFound(new { message = "Media item not found for user." });
        }

        var captureRecord = mediaItem.CaptureRecord;
        if (captureRecord == null)
        {
            captureRecord = new CaptureRecord
            {
                Id = Guid.NewGuid(),
                MediaItemId = mediaItem.Id,
                UserId = userId,
                SourceType = request.SourceType,
                CaptureDeviceId = request.CaptureDeviceId ?? string.Empty,
                DeviceModel = request.DeviceModel,
                DeviceOs = request.DeviceOs,
                LocationLatLong = request.LocationLatLong ?? string.Empty,
                CapturedAtUtc = request.CapturedAtUtc ?? DateTime.UtcNow,
                C2paIssuer = "VisionX Trust Authority",
                C2paClaimAlgorithm = "RS256",
                C2paStatus = request.SourceType == SourceType.Camera ? C2paStatus.VerifiedOriginal : C2paStatus.VerifiedProvenanceTransformed,
                CreatedAtUtc = DateTime.UtcNow
            };
            _dbContext.CaptureRecords.Add(captureRecord);
        }
        else
        {
            captureRecord.DeviceModel = request.DeviceModel ?? captureRecord.DeviceModel;
            captureRecord.DeviceOs = request.DeviceOs ?? captureRecord.DeviceOs;
            captureRecord.LocationLatLong = request.LocationLatLong ?? captureRecord.LocationLatLong;
            if (request.CapturedAtUtc.HasValue)
            {
                captureRecord.CapturedAtUtc = request.CapturedAtUtc.Value;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(MapToResponse(mediaItem, captureRecord));
    }

    [HttpGet]
    public async Task<ActionResult<CaptureListResponse>> GetCaptures(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] SourceType? sourceType = null,
        [FromQuery] MediaType? mediaType = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        var query = _dbContext.MediaItems
            .Include(m => m.CaptureRecord)
            .Include(m => m.ShareLinks)
            .Include(m => m.AccessEvents)
            .Where(m => m.UserId == userId && !m.IsDeleted);

        if (sourceType.HasValue)
        {
            query = query.Where(m => m.SourceType == sourceType.Value);
        }

        if (mediaType.HasValue)
        {
            query = query.Where(m => m.MediaType == mediaType.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(m => m.OriginalFileName.ToLower().Contains(search.ToLower()));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);

        var items = await query
            .OrderByDescending(m => m.CreatedAtUtc)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(cancellationToken);

        var responses = items.Select(m => MapToResponse(m, m.CaptureRecord)).ToList();

        return Ok(new CaptureListResponse(
            Items: responses,
            TotalCount: totalCount,
            Page: safePage,
            PageSize: safePageSize
        ));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CaptureResponse>> GetCaptureById(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        var mediaItem = await _dbContext.MediaItems
            .Include(m => m.CaptureRecord)
            .Include(m => m.ShareLinks)
            .Include(m => m.AccessEvents)
            .FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId && !m.IsDeleted, cancellationToken);

        if (mediaItem == null)
        {
            return NotFound(new { message = "Capture not found." });
        }

        return Ok(MapToResponse(mediaItem, mediaItem.CaptureRecord));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteCapture(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        var mediaItem = await _dbContext.MediaItems
            .FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId, cancellationToken);

        if (mediaItem == null)
        {
            return NotFound(new { message = "Capture not found." });
        }

        mediaItem.IsDeleted = true;
        mediaItem.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _storageService.DeleteMediaAsync(mediaItem.StoragePath, cancellationToken);

        return Ok(new { message = "Capture deleted successfully." });
    }

    private string GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier) ??
               User.FindFirstValue("sub") ??
               "anonymous_user";
    }

    private static CaptureResponse MapToResponse(MediaItem m, CaptureRecord? c)
    {
        return new CaptureResponse(
            Id: c?.Id ?? m.Id,
            MediaItemId: m.Id,
            UserId: m.UserId,
            SourceType: m.SourceType,
            MediaType: m.MediaType,
            CapturedAtUtc: c?.CapturedAtUtc ?? m.CreatedAtUtc,
            C2paStatus: c?.C2paStatus ?? C2paStatus.NoCredentials,
            SynthIdStatus: c?.SynthIdStatus ?? SynthIdStatus.NotDetected,
            SynthIdConfidence: c?.SynthIdConfidence,
            C2paIssuer: c?.C2paIssuer ?? "VisionX",
            OriginalSha256: m.OriginalSha256,
            SignedSha256: m.SignedSha256,
            FileSizeBytes: m.FileSizeBytes,
            FileName: m.OriginalFileName,
            MimeType: m.MimeType,
            DeviceModel: c?.DeviceModel,
            DeviceOs: c?.DeviceOs,
            LocationLatLong: c?.LocationLatLong,
            ShareLinksCount: m.ShareLinks.Count,
            AccessEventsCount: m.AccessEvents.Count,
            CreatedAtUtc: m.CreatedAtUtc
        );
    }
}
