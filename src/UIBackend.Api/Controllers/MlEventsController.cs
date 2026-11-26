using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UIBackend.Application.DTOs;
using UIBackend.Application.Interfaces;
using UIBackend.Infrastructure.Persistence;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("api/ml")]
[AllowAnonymous]
public class MlEventsController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;
    private readonly IAlertClassificationService _classificationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MlEventsController> _logger;

    public MlEventsController(
        UIBackendDbContext dbContext,
        IAlertClassificationService classificationService,
        IConfiguration configuration,
        ILogger<MlEventsController> logger)
    {
        _dbContext = dbContext;
        _classificationService = classificationService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("events")]
    [RequestSizeLimit(1_048_576)]
    public async Task<ActionResult<MlIngestionResponse>> IngestEvent(
        [FromBody] MlDetectionEventRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateMlServiceCredential())
        {
            _logger.LogWarning("Rejected ML event ingestion due to invalid or missing service credential.");
            return Unauthorized(new { code = "INVALID_ML_SERVICE_KEY", message = "Untrusted ML service credential." });
        }

        var result = await _classificationService.ProcessDetectionEventAsync(request, isTest: false, cancellationToken: cancellationToken);
        if (!result.Accepted)
        {
            return BadRequest(result);
        }
        return Ok(result);
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> IngestHeartbeat(
        [FromBody] MlHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateMlServiceCredential())
        {
            return Unauthorized(new { code = "INVALID_ML_SERVICE_KEY", message = "Untrusted ML service credential." });
        }

        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == request.DeviceId && !d.IsDeleted, cancellationToken);

        if (device == null) return NotFound(new { message = "Device not found." });

        device.Status = request.Status;
        device.MlWorkerId = request.WorkerId;
        device.LastSeenAtUtc = DateTime.UtcNow;
        if (request.LastSuccessfulInferenceAtUtc.HasValue)
        {
            device.LastSuccessfulInferenceAtUtc = request.LastSuccessfulInferenceAtUtc.Value.ToUniversalTime();
        }
        device.LastError = request.ErrorMessage;
        device.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { ok = true, deviceId = device.Id, status = device.Status });
    }

    private bool ValidateMlServiceCredential()
    {
        var configuredKey = _configuration["MlService:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ML_SERVICE_API_KEY")
            ?? "VisionX_ML_Service_Key_2026";

        string? provided = null;
        if (Request.Headers.TryGetValue("X-ML-Service-Key", out var headerKey) &&
            !string.IsNullOrWhiteSpace(headerKey))
        {
            provided = headerKey.ToString().Trim();
        }
        else if (Request.Headers.TryGetValue("Authorization", out var authHeader) &&
                 !string.IsNullOrWhiteSpace(authHeader))
        {
            var rawAuth = authHeader.ToString().Trim();
            if (rawAuth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                provided = rawAuth.Substring("Bearer ".Length).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(configuredKey);
        var actualBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
