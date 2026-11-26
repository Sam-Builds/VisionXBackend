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
    private readonly IC2paVerificationProvider _c2paProvider;
    private readonly ISynthIdDetectionProvider _synthIdProvider;
    private readonly IMediaFingerprintProvider _fingerprintProvider;
    private readonly IImageForensicsProvider _forensicsProvider;
    private readonly ILogger<MediaController> _logger;

    public MediaController(
        UIBackendDbContext dbContext,
        IMediaStorageService storageService,
        IC2paVerificationProvider c2paProvider,
        ISynthIdDetectionProvider synthIdProvider,
        IMediaFingerprintProvider fingerprintProvider,
        IImageForensicsProvider forensicsProvider,
        ILogger<MediaController> logger)
    {
        _dbContext = dbContext;
        _storageService = storageService;
        _c2paProvider = c2paProvider;
        _synthIdProvider = synthIdProvider;
        _fingerprintProvider = fingerprintProvider;
        _forensicsProvider = forensicsProvider;
        _logger = logger;
    }

    [HttpPost("upload")]
    [Authorize]
    [RequestSizeLimit(200_000_000)]
    public async Task<ActionResult<MediaUploadResponse>> Upload(
        [FromForm] IFormFile file,
        [FromForm] SourceType sourceType = SourceType.Camera,
        [FromForm] string? deviceModel = null,
        [FromForm] string? location = null,
        [FromForm] string? appVersion = null,
        [FromForm] string? deviceKeyId = null,
        [FromForm] string? platformAttestationStatus = null,
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

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var rawBytes = ms.ToArray();

        var originalSha256 = _storageService.ComputeSha256(new MemoryStream(rawBytes));
        var perceptualHash = _fingerprintProvider.ComputePerceptualDHash(rawBytes, mimeType);
        var synthResult = await _synthIdProvider.InspectMediaAsync(rawBytes, fileName, mimeType, cancellationToken);

        var mediaItemId = Guid.NewGuid();
        string storagePath;
        string? originalArtifactPath = null;
        string? signedSha256 = null;
        string manifestJson = string.Empty;
        string manifestSignature = string.Empty;
        string issuer = "None";
        C2paStatus c2paStatus;
        string responseMessage;

        if (sourceType == SourceType.Import)
        {
            var (isPhoneOrigin, reason) = _forensicsProvider.ValidatePhoneCameraOrigin(rawBytes, fileName, mimeType);
            if (!isPhoneOrigin)
            {
                return UnprocessableEntity(new
                {
                    code = "OUTSIDE_SOURCE_REJECTED",
                    message = reason
                });
            }
        }

        // Preserve original bytes separately AND generate signed VisionX C2PA artifact
        originalArtifactPath = await _storageService.StoreMediaAsync(
            userId,
            mediaItemId,
            $"original_{fileName}",
            new MemoryStream(rawBytes),
            cancellationToken
        );

        var manifestResult = await _c2paProvider.CreateAndEmbedManifestAsync(
            rawBytes,
            fileName,
            mimeType,
            userId,
            SourceType.Camera,
            deviceModel,
            location,
            cancellationToken
        );

        signedSha256 = _storageService.ComputeSha256(new MemoryStream(manifestResult.EmbeddedMediaBytes));
        storagePath = await _storageService.StoreMediaAsync(
            userId,
            mediaItemId,
            fileName,
            new MemoryStream(manifestResult.EmbeddedMediaBytes),
            cancellationToken
        );

        manifestJson = manifestResult.ManifestJson;
        manifestSignature = manifestResult.Signature;
        issuer = manifestResult.Issuer;
        c2paStatus = C2paStatus.VerifiedOriginal;
        responseMessage = sourceType == SourceType.Camera
            ? "Registered as captured through VisionX with C2PA Content Credentials."
            : "Photo verified as taken on this phone. C2PA Content Credentials and cryptographic signature added.";

        var mediaItem = new MediaItem
        {
            Id = mediaItemId,
            UserId = userId,
            OriginalFileName = fileName,
            MimeType = mimeType,
            MediaType = mediaType,
            FileSizeBytes = new FileInfo(storagePath).Exists ? new FileInfo(storagePath).Length : rawBytes.Length,
            OriginalSha256 = originalSha256,
            SignedSha256 = signedSha256,
            PerceptualHash = perceptualHash,
            StoragePath = storagePath,
            OriginalArtifactPath = originalArtifactPath,
            SourceType = sourceType,
            AppVersion = appVersion,
            DeviceKeyId = deviceKeyId,
            PlatformAttestationStatus = platformAttestationStatus ?? "Unattested",
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
            C2paManifestJson = manifestJson,
            C2paManifestSignature = manifestSignature,
            C2paIssuer = issuer,
            C2paClaimAlgorithm = sourceType == SourceType.Camera ? "RS256" : "None",
            C2paStatus = c2paStatus,
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
            C2paStatus: c2paStatus,
            SynthIdStatus: synthResult.Status,
            SynthIdConfidence: synthResult.Confidence,
            Message: responseMessage
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
