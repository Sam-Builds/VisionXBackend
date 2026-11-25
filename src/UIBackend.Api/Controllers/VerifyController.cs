using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UIBackend.Application.DTOs;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Persistence;
using UIBackend.Infrastructure.Verification;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("verify")]
public class VerifyController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;
    private readonly IVerificationOrchestrator _orchestrator;
    private readonly IMediaStorageService _storageService;
    private readonly ISynthIdDetectionProvider _synthIdProvider;

    public VerifyController(
        UIBackendDbContext dbContext,
        IVerificationOrchestrator orchestrator,
        IMediaStorageService storageService,
        ISynthIdDetectionProvider synthIdProvider)
    {
        _dbContext = dbContext;
        _orchestrator = orchestrator;
        _storageService = storageService;
        _synthIdProvider = synthIdProvider;
    }

    [HttpPost]
    [RequestSizeLimit(200_000_000)]
    public async Task<ActionResult<EvidenceVerificationResponse>> VerifyMedia(
        [FromForm] IFormFile? file,
        [FromForm] string? shareUrl = null,
        [FromQuery(Name = "shareUrl")] string? shareUrlQuery = null,
        CancellationToken cancellationToken = default)
    {
        shareUrl ??= shareUrlQuery;
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");

        byte[] mediaBytes;
        string fileName;
        string mimeType;

        if (file != null && file.Length > 0)
        {
            fileName = Path.GetFileName(file.FileName);
            mimeType = string.IsNullOrWhiteSpace(file.ContentType)
                ? (fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "image/jpeg")
                : file.ContentType;

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, cancellationToken);
            mediaBytes = ms.ToArray();
        }
        else if (!string.IsNullOrWhiteSpace(shareUrl))
        {
            // Extract share token from pasted VisionX share URL (/a/s/{token} or /a/share/{token})
            var token = shareUrl.Trim().TrimEnd('/').Split('/').LastOrDefault();
            if (string.IsNullOrWhiteSpace(token))
            {
                return BadRequest(new { message = "Invalid VisionX share link." });
            }

            var shareLink = await _dbContext.ShareLinks
                .Include(s => s.MediaItem)
                .FirstOrDefaultAsync(s => s.Token == token && !s.IsRevoked, cancellationToken);

            if (shareLink == null || shareLink.MediaItem.IsDeleted)
            {
                return NotFound(new { message = "Shared media not found or link revoked." });
            }

            if (shareLink.ExpiresAtUtc.HasValue && shareLink.ExpiresAtUtc.Value < DateTime.UtcNow)
            {
                return StatusCode(StatusCodes.Status410Gone, new { message = "Share link has expired." });
            }

            await using var stream = await _storageService.GetMediaStreamAsync(shareLink.MediaItem.StoragePath, cancellationToken);
            if (stream == null)
            {
                return NotFound(new { message = "Media file not found on storage." });
            }

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            mediaBytes = ms.ToArray();
            fileName = shareLink.MediaItem.OriginalFileName;
            mimeType = shareLink.MediaItem.MimeType;
        }
        else
        {
            return BadRequest(new { message = "Provide a media file or a valid VisionX share link to verify." });
        }

        var response = await _orchestrator.VerifyMediaAsync(mediaBytes, fileName, mimeType, userId, cancellationToken);
        return Ok(response);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EvidenceVerificationResponse>> GetVerificationRun(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var run = await _orchestrator.GetVerificationRunAsync(id, cancellationToken);
        if (run == null)
        {
            return NotFound(new { message = "Verification run not found." });
        }
        return Ok(run);
    }

    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<EvidenceVerificationResponse>>> GetVerificationHistory(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var safeLimit = Math.Clamp(limit, 1, 100);

        var query = _dbContext.VerificationRuns.AsQueryable();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            query = query.Where(r => r.UserId == userId);
        }

        var ids = await query
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(safeLimit)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        var results = new List<EvidenceVerificationResponse>();
        foreach (var id in ids)
        {
            var item = await _orchestrator.GetVerificationRunAsync(id, cancellationToken);
            if (item != null) results.Add(item);
        }

        return Ok(results);
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
}
