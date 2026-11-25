using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UIBackend.Application.DTOs;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Persistence;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("verify")]
public class VerifyController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;
    private readonly IC2paService _c2paService;
    private readonly ISynthIdDetectionProvider _synthIdProvider;
    private readonly IMediaStorageService _storageService;
    private readonly ILogger<VerifyController> _logger;

    public VerifyController(
        UIBackendDbContext dbContext,
        IC2paService c2paService,
        ISynthIdDetectionProvider synthIdProvider,
        IMediaStorageService storageService,
        ILogger<VerifyController> logger)
    {
        _dbContext = dbContext;
        _c2paService = c2paService;
        _synthIdProvider = synthIdProvider;
        _storageService = storageService;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(200_000_000)]
    public async Task<ActionResult<VerifyMediaResponse>> VerifyMedia(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No media file provided for verification." });
        }

        var fileName = Path.GetFileName(file.FileName);
        var mimeType = file.ContentType;
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            mimeType = fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "image/jpeg";
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var mediaBytes = ms.ToArray();

        // 1. Calculate SHA-256
        var sha256 = _storageService.ComputeSha256(new MemoryStream(mediaBytes));

        // 2. C2PA Content Credentials Check
        var c2paResult = await _c2paService.VerifyMediaAsync(mediaBytes, fileName, mimeType, cancellationToken);

        // 3. SynthID Watermark Check
        var synthResult = await _synthIdProvider.InspectMediaAsync(mediaBytes, fileName, mimeType, cancellationToken);

        // 4. Soft-binding check against registered media items
        var registeredItem = await _dbContext.MediaItems
            .Include(m => m.CaptureRecord)
            .FirstOrDefaultAsync(m => (m.OriginalSha256 == sha256 || m.SignedSha256 == sha256) && !m.IsDeleted, cancellationToken);

        var matchesRegistered = registeredItem != null;
        var finalC2paStatus = c2paResult.Status;

        // If hard-binding didn't find manifest in file, but hash matches a registered camera original capture in VisionX DB
        if (finalC2paStatus == C2paStatus.NoCredentials && matchesRegistered)
        {
            finalC2paStatus = registeredItem!.SourceType == SourceType.Camera 
                ? C2paStatus.VerifiedOriginal 
                : C2paStatus.VerifiedProvenanceTransformed;
        }

        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = Request.Headers.UserAgent.ToString();
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

        var verificationRecord = new VerificationRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = registeredItem?.Id,
            VerifiedByUserId = userId,
            MediaSha256 = sha256,
            FileName = fileName,
            FileSizeBytes = file.Length,
            C2paStatus = finalC2paStatus,
            HasValidHardBinding = c2paResult.HardBindingValid,
            MatchesRegisteredCapture = matchesRegistered,
            Issuer = c2paResult.Issuer ?? registeredItem?.CaptureRecord?.C2paIssuer,
            SignerCertificateThumbprint = c2paResult.SignerThumbprint,
            ClaimSummaryJson = c2paResult.ClaimSummary,
            SynthIdStatus = synthResult.Status,
            SynthIdConfidence = synthResult.Confidence,
            StatusMessage = c2paResult.Message,
            ClientIpAddress = clientIp,
            UserAgent = userAgent,
            VerifiedAtUtc = DateTime.UtcNow
        };

        _dbContext.VerificationRecords.Add(verificationRecord);
        await _dbContext.SaveChangesAsync(cancellationToken);

        RegisteredMediaSummary? summary = null;
        if (registeredItem != null)
        {
            var maskedUser = MaskUserId(registeredItem.UserId);
            summary = new RegisteredMediaSummary(
                MediaItemId: registeredItem.Id,
                OriginalFileName: registeredItem.OriginalFileName,
                SourceType: registeredItem.SourceType,
                CapturedAtUtc: registeredItem.CaptureRecord?.CapturedAtUtc ?? registeredItem.CreatedAtUtc,
                OwnerUserIdMasked: maskedUser
            );
        }

        return Ok(new VerifyMediaResponse(
            VerificationId: verificationRecord.Id,
            MediaSha256: sha256,
            FileName: fileName,
            FileSizeBytes: file.Length,
            C2paStatus: finalC2paStatus,
            HasValidHardBinding: c2paResult.HardBindingValid,
            MatchesRegisteredCapture: matchesRegistered,
            Issuer: verificationRecord.Issuer,
            SignerCertificateThumbprint: verificationRecord.SignerCertificateThumbprint,
            ClaimSummaryJson: verificationRecord.ClaimSummaryJson,
            SynthIdStatus: synthResult.Status,
            SynthIdConfidence: synthResult.Confidence,
            StatusMessage: verificationRecord.StatusMessage,
            VerifiedAtUtc: verificationRecord.VerifiedAtUtc,
            RegisteredMedia: summary
        ));
    }

    [HttpPost("hash")]
    public async Task<ActionResult<VerifyMediaResponse>> VerifyHash(
        [FromBody] VerifyHashRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sha256) || request.Sha256.Length != 64)
        {
            return BadRequest(new { message = "Invalid SHA-256 hash provided." });
        }

        var normalizedHash = request.Sha256.Trim().ToLowerInvariant();

        var registeredItem = await _dbContext.MediaItems
            .Include(m => m.CaptureRecord)
            .FirstOrDefaultAsync(m => (m.OriginalSha256 == normalizedHash || m.SignedSha256 == normalizedHash) && !m.IsDeleted, cancellationToken);

        var matchesRegistered = registeredItem != null;
        var status = matchesRegistered
            ? (registeredItem!.SourceType == SourceType.Camera ? C2paStatus.VerifiedOriginal : C2paStatus.VerifiedProvenanceTransformed)
            : C2paStatus.NoCredentials;

        var message = matchesRegistered
            ? $"Hash matched registered {registeredItem!.SourceType} media on VisionX network."
            : "No registered media record found matching this SHA-256 digest.";

        RegisteredMediaSummary? summary = null;
        if (registeredItem != null)
        {
            summary = new RegisteredMediaSummary(
                MediaItemId: registeredItem.Id,
                OriginalFileName: registeredItem.OriginalFileName,
                SourceType: registeredItem.SourceType,
                CapturedAtUtc: registeredItem.CaptureRecord?.CapturedAtUtc ?? registeredItem.CreatedAtUtc,
                OwnerUserIdMasked: MaskUserId(registeredItem.UserId)
            );
        }

        return Ok(new VerifyMediaResponse(
            VerificationId: Guid.NewGuid(),
            MediaSha256: normalizedHash,
            FileName: registeredItem?.OriginalFileName ?? "Unknown file",
            FileSizeBytes: registeredItem?.FileSizeBytes ?? 0,
            C2paStatus: status,
            HasValidHardBinding: matchesRegistered,
            MatchesRegisteredCapture: matchesRegistered,
            Issuer: registeredItem?.CaptureRecord?.C2paIssuer,
            SignerCertificateThumbprint: null,
            ClaimSummaryJson: null,
            SynthIdStatus: registeredItem?.CaptureRecord?.SynthIdStatus ?? SynthIdStatus.NotDetected,
            SynthIdConfidence: registeredItem?.CaptureRecord?.SynthIdConfidence,
            StatusMessage: message,
            VerifiedAtUtc: DateTime.UtcNow,
            RegisteredMedia: summary
        ));
    }

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<VerifyMediaResponse>>> GetVerificationHistory(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var safeLimit = Math.Clamp(limit, 1, 100);

        var query = _dbContext.VerificationRecords
            .Include(v => v.MediaItem)
            .ThenInclude(m => m!.CaptureRecord)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(userId))
        {
            query = query.Where(v => v.VerifiedByUserId == userId);
        }

        var records = await query
            .OrderByDescending(v => v.VerifiedAtUtc)
            .Take(safeLimit)
            .ToListAsync(cancellationToken);

        var response = records.Select(v => new VerifyMediaResponse(
            VerificationId: v.Id,
            MediaSha256: v.MediaSha256,
            FileName: v.FileName,
            FileSizeBytes: v.FileSizeBytes,
            C2paStatus: v.C2paStatus,
            HasValidHardBinding: v.HasValidHardBinding,
            MatchesRegisteredCapture: v.MatchesRegisteredCapture,
            Issuer: v.Issuer,
            SignerCertificateThumbprint: v.SignerCertificateThumbprint,
            ClaimSummaryJson: v.ClaimSummaryJson,
            SynthIdStatus: v.SynthIdStatus,
            SynthIdConfidence: v.SynthIdConfidence,
            StatusMessage: v.StatusMessage,
            VerifiedAtUtc: v.VerifiedAtUtc,
            RegisteredMedia: v.MediaItem != null ? new RegisteredMediaSummary(
                MediaItemId: v.MediaItem.Id,
                OriginalFileName: v.MediaItem.OriginalFileName,
                SourceType: v.MediaItem.SourceType,
                CapturedAtUtc: v.MediaItem.CaptureRecord?.CapturedAtUtc ?? v.MediaItem.CreatedAtUtc,
                OwnerUserIdMasked: MaskUserId(v.MediaItem.UserId)
            ) : null
        )).ToList();

        return Ok(response);
    }

    [HttpPost("synthid")]
    [RequestSizeLimit(200_000_000)]
    public async Task<ActionResult<SynthIdDetectionResult>> InspectSynthId(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No file provided for SynthID inspection." });
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var bytes = ms.ToArray();

        var result = await _synthIdProvider.InspectMediaAsync(bytes, file.FileName, file.ContentType, cancellationToken);
        return Ok(result);
    }

    private static string MaskUserId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return "Anonymous";
        if (userId.Length <= 4) return "****";
        return $"{userId[..2]}****{userId[^2..]}";
    }
}
