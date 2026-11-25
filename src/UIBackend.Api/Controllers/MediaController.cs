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
[Route("media")]
public class MediaController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;
    private readonly IMediaStorageService _storageService;
    private readonly IC2paService _c2paService;
    private readonly ISynthIdDetectionProvider _synthIdProvider;
    private readonly ILogger<MediaController> _logger;

    public MediaController(
        UIBackendDbContext dbContext,
        IMediaStorageService storageService,
        IC2paService c2paService,
        ISynthIdDetectionProvider synthIdProvider,
        ILogger<MediaController> logger)
    {
        _dbContext = dbContext;
        _storageService = storageService;
        _c2paService = c2paService;
        _synthIdProvider = synthIdProvider;
        _logger = logger;
    }

    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(200_000_000)] // 200 MB limit
    public async Task<ActionResult<MediaUploadResponse>> Upload(
        [FromForm] IFormFile file,
        [FromForm] SourceType sourceType = SourceType.Camera,
        [FromForm] string? deviceModel = null,
        [FromForm] string? location = null,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No file provided or file is empty." });
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                     User.FindFirstValue("sub") ??
                     "anonymous_user";

        var fileName = Path.GetFileName(file.FileName);
        var mimeType = file.ContentType;
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            mimeType = fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "image/jpeg";
        }

        var mediaType = mimeType.StartsWith("video", StringComparison.OrdinalIgnoreCase) 
            ? MediaType.Video 
            : MediaType.Image;

        // Read uploaded file to memory buffer
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var rawBytes = ms.ToArray();

        // 1. Calculate raw original SHA-256
        var originalSha256 = _storageService.ComputeSha256(new MemoryStream(rawBytes));

        // 2. Perform SynthID AI watermark detection
        var synthResult = await _synthIdProvider.InspectMediaAsync(rawBytes, fileName, mimeType, cancellationToken);

        // 3. Inject C2PA Manifest & Sign
        var manifestResult = await _c2paService.CreateAndEmbedManifestAsync(
            rawBytes,
            fileName,
            mimeType,
            userId,
            sourceType,
            deviceModel,
            location,
            cancellationToken
        );

        var signedSha256 = _storageService.ComputeSha256(new MemoryStream(manifestResult.EmbeddedMediaBytes));

        // 4. Save signed media to permanent storage
        var mediaItemId = Guid.NewGuid();
        var storagePath = await _storageService.StoreMediaAsync(
            userId,
            mediaItemId,
            fileName,
            new MemoryStream(manifestResult.EmbeddedMediaBytes),
            cancellationToken
        );

        // 5. Create Database Records
        var mediaItem = new MediaItem
        {
            Id = mediaItemId,
            UserId = userId,
            OriginalFileName = fileName,
            MimeType = mimeType,
            MediaType = mediaType,
            FileSizeBytes = manifestResult.EmbeddedMediaBytes.Length,
            OriginalSha256 = originalSha256,
            SignedSha256 = signedSha256,
            StoragePath = storagePath,
            SourceType = sourceType,
            CreatedAtUtc = DateTime.UtcNow
        };

        var captureRecord = new CaptureRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = mediaItemId,
            UserId = userId,
            SourceType = sourceType,
            DeviceModel = deviceModel,
            LocationLatLong = location ?? string.Empty,
            CapturedAtUtc = DateTime.UtcNow,
            C2paManifestJson = manifestResult.ManifestJson,
            C2paManifestSignature = manifestResult.Signature,
            C2paIssuer = manifestResult.Issuer,
            C2paClaimAlgorithm = manifestResult.Algorithm,
            C2paStatus = sourceType == SourceType.Camera ? C2paStatus.VerifiedOriginal : C2paStatus.VerifiedProvenanceTransformed,
            SynthIdStatus = synthResult.Status,
            SynthIdConfidence = synthResult.Confidence,
            SynthIdRawResponse = synthResult.RawResponse,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.MediaItems.Add(mediaItem);
        _dbContext.CaptureRecords.Add(captureRecord);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new MediaUploadResponse(
            MediaItemId: mediaItemId,
            FileName: fileName,
            MimeType: mimeType,
            MediaType: mediaType,
            FileSizeBytes: mediaItem.FileSizeBytes,
            OriginalSha256: originalSha256,
            SignedSha256: signedSha256,
            SourceType: sourceType,
            C2paStatus: captureRecord.C2paStatus,
            SynthIdStatus: synthResult.Status,
            SynthIdConfidence: synthResult.Confidence,
            Message: "Media uploaded and bound with C2PA Content Credentials successfully."
        ));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> StreamMedia(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var mediaItem = await _dbContext.MediaItems.FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted, cancellationToken);
        if (mediaItem == null)
        {
            return NotFound(new { message = "Media item not found." });
        }

        var rangeHeader = Request.Headers.Range.ToString();
        var rangeResult = await _storageService.GetMediaRangeStreamAsync(mediaItem.StoragePath, rangeHeader, cancellationToken);
        if (rangeResult == null)
        {
            return NotFound(new { message = "Media file not found on disk." });
        }

        var (stream, totalLength, start, end) = rangeResult.Value;
        var contentLength = end - start + 1;

        Response.Headers.AcceptRanges = "bytes";

        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            Response.StatusCode = StatusCodes.Status206PartialContent;
            Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
            return File(stream, mediaItem.MimeType, enableRangeProcessing: false);
        }

        return File(stream, mediaItem.MimeType);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> DownloadMedia(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var mediaItem = await _dbContext.MediaItems.FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted, cancellationToken);
        if (mediaItem == null)
        {
            return NotFound(new { message = "Media item not found." });
        }

        var stream = await _storageService.GetMediaStreamAsync(mediaItem.StoragePath, cancellationToken);
        if (stream == null)
        {
            return NotFound(new { message = "Media file not found on disk." });
        }

        return File(stream, mediaItem.MimeType, mediaItem.OriginalFileName);
    }
}
