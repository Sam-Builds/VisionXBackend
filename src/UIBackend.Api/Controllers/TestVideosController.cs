using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;
using UIBackend.Domain.Enums;
using UIBackend.Infrastructure.Persistence;

namespace UIBackend.Api.Controllers;

[ApiController]
[Route("api/test-videos")]
public class TestVideosController : ControllerBase
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"
    };

    private readonly UIBackendDbContext _dbContext;
    private readonly IMlServiceClient _mlClient;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TestVideosController> _logger;

    public TestVideosController(
        UIBackendDbContext dbContext,
        IMlServiceClient mlClient,
        IConfiguration configuration,
        IWebHostEnvironment env,
        ILogger<TestVideosController> logger)
    {
        _dbContext = dbContext;
        _mlClient = mlClient;
        _configuration = configuration;
        _env = env;
        _logger = logger;
    }

    [HttpGet]
    [Authorize]
    public IActionResult ListTestVideos()
    {
        var dir = ResolveTestVideosDirectory();
        if (dir == null)
        {
            return NotFound(new { message = "Test videos directory (testvids/) not found on server." });
        }

        var files = GetVideoFiles(dir);
        if (files.Count == 0)
        {
            return NotFound(new { message = "No supported video files found in testvids/." });
        }

        return Ok(new { count = files.Count, files = files.Select(Path.GetFileName).ToList() });
    }

    [HttpPost("start")]
    [Authorize]
    public async Task<IActionResult> StartRandomTestFeed(CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();

        // Prevent multiple simultaneous test video sessions per user
        var activeTestDevice = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d =>
                d.OwnerUserId == userId &&
                !d.IsDeleted &&
                d.Name.StartsWith("[TEST VIDEO]") &&
                d.MonitoringEnabled &&
                (d.Status == DeviceStatus.Starting || d.Status == DeviceStatus.Online || d.Status == DeviceStatus.Pending),
                cancellationToken);

        if (activeTestDevice != null)
        {
            return Conflict(new
            {
                message = "A test video session is already running. Stop it before starting a new one.",
                deviceId = activeTestDevice.Id,
                deviceName = activeTestDevice.Name
            });
        }

        var dir = ResolveTestVideosDirectory();
        if (dir == null)
        {
            return NotFound(new { message = "Test videos directory (testvids/) is missing or inaccessible." });
        }

        var files = GetVideoFiles(dir);
        if (files.Count == 0)
        {
            return NotFound(new { message = "No supported test videos found in testvids/." });
        }

        var chosenPath = files[Random.Shared.Next(files.Count)];
        var fileName = Path.GetFileName(chosenPath);

        var deviceId = Guid.NewGuid();
        var jobId = $"testvid-{deviceId:N}";
        var streamUrl = BuildSignedStreamUrl(fileName);

        var device = new MonitoredDevice
        {
            Id = deviceId,
            OwnerUserId = userId,
            Name = $"[TEST VIDEO] {fileName}",
            FeedUrl = streamUrl,
            Status = DeviceStatus.Starting,
            MonitoringEnabled = true,
            ActiveJobId = jobId,
            ConfigurationVersion = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _dbContext.MonitoredDevices.Add(device);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var mlResult = await _mlClient.StartMonitoringAsync(
            device,
            username: null,
            password: null,
            idempotencyKey: $"testvid-start-{deviceId:N}",
            cancellationToken);

        var job = new MonitoringJob
        {
            Id = Guid.NewGuid(),
            DeviceId = device.Id,
            ExternalJobId = mlResult.JobId ?? jobId,
            IdempotencyKey = $"testvid-start-{deviceId:N}",
            State = mlResult.Success ? MonitoringJobState.Active : MonitoringJobState.Failed,
            LastError = mlResult.ErrorMessage,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        _dbContext.MonitoringJobs.Add(job);

        device.ActiveJobId = job.ExternalJobId;
        device.MlWorkerId = mlResult.WorkerId;
        device.Status = mlResult.Success ? mlResult.ResultingStatus : DeviceStatus.Error;
        device.LastError = mlResult.ErrorMessage;
        device.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (!mlResult.Success)
        {
            return StatusCode(502, new
            {
                deviceId = device.Id,
                fileName,
                streamUrl,
                sessionStatus = "Failed",
                message = mlResult.ErrorMessage ?? "Failed to start ML processing on test video."
            });
        }

        return Ok(new
        {
            deviceId = device.Id,
            deviceName = device.Name,
            fileName,
            streamUrl,
            jobId = device.ActiveJobId,
            sessionStatus = device.Status == DeviceStatus.Online ? "Running" : "Starting",
            startedAtUtc = device.CreatedAtUtc
        });
    }

    [HttpPost("{deviceId:guid}/stop")]
    [Authorize]
    public async Task<IActionResult> StopTestFeed(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);

        if (device == null) return NotFound(new { message = "Test video session not found." });

        if (!string.IsNullOrWhiteSpace(device.ActiveJobId))
        {
            await _mlClient.StopMonitoringAsync(device.Id, device.ActiveJobId, cancellationToken);
        }

        device.MonitoringEnabled = false;
        device.Status = DeviceStatus.Stopped;
        device.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            deviceId = device.Id,
            sessionStatus = "Stopped",
            message = "Test video session stopped."
        });
    }

    [HttpGet("{deviceId:guid}/status")]
    [Authorize]
    public async Task<IActionResult> GetTestFeedStatus(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);

        if (device == null) return NotFound(new { message = "Test video session not found." });

        string sessionStatus;
        if (!device.MonitoringEnabled && device.Status == DeviceStatus.Stopped)
        {
            sessionStatus = "Stopped";
        }
        else if (device.Status == DeviceStatus.Error)
        {
            sessionStatus = "Failed";
        }
        else if (!string.IsNullOrWhiteSpace(device.ActiveJobId))
        {
            var mlStatus = await _mlClient.GetJobStatusAsync(device.Id, device.ActiveJobId, cancellationToken);
            if (mlStatus.Reachable)
            {
                if (mlStatus.Status == DeviceStatus.Stopped && device.MonitoringEnabled)
                {
                    // ML finished playing the test video file
                    device.MonitoringEnabled = false;
                    device.Status = DeviceStatus.Stopped;
                    device.UpdatedAtUtc = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    sessionStatus = "Completed";
                }
                else
                {
                    device.Status = mlStatus.Status;
                    device.LastError = mlStatus.ErrorMessage;
                    if (mlStatus.LastSuccessfulInferenceAtUtc.HasValue)
                        device.LastSuccessfulInferenceAtUtc = mlStatus.LastSuccessfulInferenceAtUtc;
                    device.UpdatedAtUtc = DateTime.UtcNow;
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    sessionStatus = device.Status switch
                    {
                        DeviceStatus.Online => "Running",
                        DeviceStatus.Starting or DeviceStatus.Pending => "Starting",
                        DeviceStatus.Error => "Failed",
                        DeviceStatus.Stopped => "Completed",
                        _ => "Running"
                    };
                }
            }
            else
            {
                sessionStatus = device.Status == DeviceStatus.Online ? "Running" : "Starting";
            }
        }
        else
        {
            sessionStatus = "Stopped";
        }

        var detectionsCount = await _dbContext.DetectionRecords.CountAsync(d => d.DeviceId == device.Id, cancellationToken);
        var alertsCount = await _dbContext.AlertRecords.CountAsync(a => a.DeviceId == device.Id, cancellationToken);
        var latestDetection = await _dbContext.DetectionRecords
            .Where(d => d.DeviceId == device.Id)
            .OrderByDescending(d => d.ObservedAtUtc)
            .Select(d => new { d.DetectionType, d.Confidence, d.AnomalyScore, d.ObservedAtUtc })
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(new
        {
            deviceId = device.Id,
            fileName = device.Name.Replace("[TEST VIDEO] ", ""),
            sessionStatus,
            monitoringEnabled = device.MonitoringEnabled,
            detectionsCount,
            alertsCount,
            latestDetection,
            lastError = device.LastError
        });
    }

    [HttpGet("stream/{fileName}")]
    [AllowAnonymous]
    public IActionResult StreamTestVideo(
        string fileName,
        [FromQuery] long? exp,
        [FromQuery] string? sig)
    {
        if (!ValidateStreamSignature(fileName, exp, sig))
        {
            return Unauthorized(new { message = "Invalid or expired test video stream signature." });
        }

        var dir = ResolveTestVideosDirectory();
        if (dir == null) return NotFound(new { message = "Test videos directory not found." });

        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || !SupportedExtensions.Contains(Path.GetExtension(safeName)))
        {
            return BadRequest(new { message = "Unsupported test video file." });
        }

        var fullDir = Path.GetFullPath(dir);
        var fullPath = Path.GetFullPath(Path.Combine(fullDir, safeName));
        if (!fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return BadRequest(new { message = "Invalid file path." });
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(new { message = "Test video file not found." });
        }

        var contentType = Path.GetExtension(safeName).ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            _ => "application/octet-stream"
        };

        return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
    }

    private string? ResolveTestVideosDirectory()
    {
        var configured = _configuration["TestVideos:Directory"]
            ?? Environment.GetEnvironmentVariable("TEST_VIDEOS_DIR");

        var candidates = new[]
        {
            configured,
            "/app/testvids",
            Path.Combine(_env.ContentRootPath, "testvids"),
            Path.Combine(_env.ContentRootPath, "..", "testvids"),
            Path.Combine(_env.ContentRootPath, "..", "..", "testvids"),
            Path.Combine(_env.ContentRootPath, "..", "..", "..", "testvids"),
        }.Where(p => !string.IsNullOrWhiteSpace(p));

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static List<string> GetVideoFiles(string dir)
    {
        return Directory.EnumerateFiles(dir)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string BuildSignedStreamUrl(string fileName)
    {
        var exp = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var sig = ComputeStreamSig(fileName, exp);

        var publicBase = (_configuration["PublicBaseUrl"]
            ?? Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")
            ?? "https://visionx.samstack.site/a").TrimEnd('/');

        return $"{publicBase}/api/test-videos/stream/{Uri.EscapeDataString(fileName)}?exp={exp}&sig={sig}";
    }

    private bool ValidateStreamSignature(string fileName, long? exp, string? sig)
    {
        if (!exp.HasValue || string.IsNullOrWhiteSpace(sig)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp.Value) return false;

        var expected = ComputeStreamSig(fileName, exp.Value);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(sig.Trim());
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private string ComputeStreamSig(string fileName, long exp)
    {
        var secret = _configuration["MlService:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ML_SERVICE_API_KEY")
            ?? "VisionX_ML_Service_Key_2026";
        var payload = $"{Path.GetFileName(fileName)}:{exp}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private string GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? throw new UnauthorizedAccessException("User identity missing.");
    }
}
