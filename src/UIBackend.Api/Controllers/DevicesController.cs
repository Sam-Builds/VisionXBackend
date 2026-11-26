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
[Route("api/devices")]
[Authorize]
public class DevicesController : ControllerBase
{
    private readonly UIBackendDbContext _dbContext;
    private readonly IFeedUrlValidator _urlValidator;
    private readonly IStreamCredentialProtector _credentialProtector;
    private readonly IMlServiceClient _mlClient;
    private readonly ILogger<DevicesController> _logger;

    public DevicesController(
        UIBackendDbContext dbContext,
        IFeedUrlValidator urlValidator,
        IStreamCredentialProtector credentialProtector,
        IMlServiceClient mlClient,
        ILogger<DevicesController> logger)
    {
        _dbContext = dbContext;
        _urlValidator = urlValidator;
        _credentialProtector = credentialProtector;
        _mlClient = mlClient;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DeviceDto>>> GetDevices(CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var devices = await _dbContext.MonitoredDevices
            .Where(d => d.OwnerUserId == userId && !d.IsDeleted)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return Ok(devices.Select(MapToDto).ToList());
    }

    [HttpGet("{deviceId:guid}")]
    public async Task<ActionResult<DeviceDto>> GetDevice(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);

        if (device == null) return NotFound(new { message = "Device not found." });
        return Ok(MapToDto(device));
    }

    [HttpPost]
    public async Task<ActionResult<DeviceDto>> CreateDevice(
        [FromBody] CreateDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Device name is required." });
        }

        var (isValidUrl, urlError) = await _urlValidator.ValidateAsync(request.FeedUrl, cancellationToken);
        if (!isValidUrl)
        {
            return BadRequest(new { message = urlError });
        }

        var userId = GetCurrentUserId();
        var now = DateTime.UtcNow;

        var device = new MonitoredDevice
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            Name = request.Name.Trim(),
            FeedUrl = request.FeedUrl.Trim(),
            EncryptedStreamCredentials = _credentialProtector.EncryptCredentials(request.StreamUsername, request.StreamPassword),
            Status = request.MonitoringEnabled ? DeviceStatus.Starting : DeviceStatus.Stopped,
            MonitoringEnabled = request.MonitoringEnabled,
            ConfigurationVersion = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsTest = request.IsTest
        };

        // Persist device before attempting ML monitoring start
        _dbContext.MonitoredDevices.Add(device);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (request.MonitoringEnabled)
        {
            await StartJobInternalAsync(device, request.StreamUsername, request.StreamPassword, cancellationToken);
        }

        return CreatedAtAction(nameof(GetDevice), new { deviceId = device.Id }, MapToDto(device));
    }

    [HttpPut("{deviceId:guid}")]
    public async Task<ActionResult<DeviceDto>> UpdateDevice(
        Guid deviceId,
        [FromBody] UpdateDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);

        if (device == null) return NotFound(new { message = "Device not found." });

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Device name is required." });
        }

        var (isValidUrl, urlError) = await _urlValidator.ValidateAsync(request.FeedUrl, cancellationToken);
        if (!isValidUrl)
        {
            return BadRequest(new { message = urlError });
        }

        device.Name = request.Name.Trim();
        device.FeedUrl = request.FeedUrl.Trim();
        if (!string.IsNullOrEmpty(request.StreamUsername) || !string.IsNullOrEmpty(request.StreamPassword))
        {
            device.EncryptedStreamCredentials = _credentialProtector.EncryptCredentials(request.StreamUsername, request.StreamPassword);
        }
        device.ConfigurationVersion++;
        device.UpdatedAtUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (request.MonitoringEnabled.HasValue && request.MonitoringEnabled.Value != device.MonitoringEnabled)
        {
            if (request.MonitoringEnabled.Value)
            {
                var (u, p) = _credentialProtector.DecryptCredentials(device.EncryptedStreamCredentials);
                await StartJobInternalAsync(device, u, p, cancellationToken);
            }
            else
            {
                await StopJobInternalAsync(device, cancellationToken);
            }
        }

        return Ok(MapToDto(device));
    }

    [HttpDelete("{deviceId:guid}")]
    public async Task<IActionResult> DeleteDevice(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);

        if (device == null) return NotFound(new { message = "Device not found." });

        if (!string.IsNullOrWhiteSpace(device.ActiveJobId))
        {
            await _mlClient.StopMonitoringAsync(device.Id, device.ActiveJobId, cancellationToken);
        }

        device.MonitoringEnabled = false;
        device.Status = DeviceStatus.Stopped;
        device.IsDeleted = true;
        device.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { message = "Device deleted." });
    }

    [HttpPost("{deviceId:guid}/start")]
    public async Task<ActionResult<DeviceDto>> StartMonitoring(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);

        if (device == null) return NotFound(new { message = "Device not found." });

        // Idempotent: if already Online or Starting with active job, reconcile rather than creating duplicate job
        if (device.MonitoringEnabled && (device.Status == DeviceStatus.Online || device.Status == DeviceStatus.Starting) && !string.IsNullOrWhiteSpace(device.ActiveJobId))
        {
            return Ok(MapToDto(device));
        }

        device.MonitoringEnabled = true;
        device.Status = DeviceStatus.Starting;
        device.LastError = null;
        device.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var (u, p) = _credentialProtector.DecryptCredentials(device.EncryptedStreamCredentials);
        await StartJobInternalAsync(device, u, p, cancellationToken);

        return Ok(MapToDto(device));
    }

    [HttpPost("{deviceId:guid}/stop")]
    public async Task<ActionResult<DeviceDto>> StopMonitoring(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);

        if (device == null) return NotFound(new { message = "Device not found." });

        await StopJobInternalAsync(device, cancellationToken);
        return Ok(MapToDto(device));
    }

    [HttpGet("{deviceId:guid}/status")]
    public async Task<ActionResult<DeviceStatusDto>> GetDeviceStatus(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var device = await _dbContext.MonitoredDevices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);

        if (device == null) return NotFound(new { message = "Device not found." });

        if (device.MonitoringEnabled && !string.IsNullOrWhiteSpace(device.ActiveJobId))
        {
            var mlStatus = await _mlClient.GetJobStatusAsync(device.Id, device.ActiveJobId, cancellationToken);
            if (mlStatus.Reachable)
            {
                device.Status = mlStatus.Status;
                device.MlWorkerId = mlStatus.WorkerId ?? device.MlWorkerId;
                device.LastSeenAtUtc = DateTime.UtcNow;
                if (mlStatus.LastSuccessfulInferenceAtUtc.HasValue)
                {
                    device.LastSuccessfulInferenceAtUtc = mlStatus.LastSuccessfulInferenceAtUtc;
                }
                device.LastError = mlStatus.ErrorMessage;
            }
            else
            {
                device.Status = DeviceStatus.Offline;
                device.LastError = mlStatus.ErrorMessage;
            }
            device.UpdatedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(new DeviceStatusDto(
            device.Id,
            device.Status,
            device.MonitoringEnabled,
            device.ActiveJobId,
            device.MlWorkerId,
            device.LastSeenAtUtc,
            device.LastSuccessfulInferenceAtUtc,
            device.LastError
        ));
    }

    [HttpGet("{deviceId:guid}/detections")]
    public async Task<ActionResult<PagedResponse<DetectionDto>>> GetDeviceDetections(
        Guid deviceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var owns = await _dbContext.MonitoredDevices.AnyAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);
        if (!owns) return NotFound(new { message = "Device not found." });

        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, 100);
        var query = _dbContext.DetectionRecords.Where(d => d.DeviceId == deviceId && d.OwnerUserId == userId);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(d => d.ObservedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(d => new DetectionDto(
                d.Id, d.EventId, d.DeviceId, d.JobId, d.DetectionType,
                d.Confidence, d.AnomalyScore, d.ModelName, d.ModelVersion,
                d.Description, d.SnapshotReference, d.ObservedAtUtc, d.IsTest
            ))
            .ToListAsync(cancellationToken);

        return Ok(new PagedResponse<DetectionDto>(items, total, safePage, safeSize));
    }

    [HttpGet("{deviceId:guid}/alerts")]
    public async Task<ActionResult<PagedResponse<AlertDto>>> GetDeviceAlerts(
        Guid deviceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var userId = GetCurrentUserId();
        var device = await _dbContext.MonitoredDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.OwnerUserId == userId && !d.IsDeleted, cancellationToken);
        if (device == null) return NotFound(new { message = "Device not found." });

        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, 100);
        var query = _dbContext.AlertRecords.Where(a => a.DeviceId == deviceId && a.OwnerUserId == userId);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(a => new AlertDto(
                a.Id, a.DeviceId, device.Name, a.DetectionId, a.Title, a.Description,
                a.Severity, a.IsRead, a.NotificationStatus, a.NotificationId,
                a.OccurrenceCount, a.LastOccurrenceAtUtc, a.CreatedAtUtc, a.IsTest
            ))
            .ToListAsync(cancellationToken);

        return Ok(new PagedResponse<AlertDto>(items, total, safePage, safeSize));
    }

    private async Task StartJobInternalAsync(MonitoredDevice device, string? username, string? password, CancellationToken ct)
    {
        var idempotencyKey = $"{device.Id:N}-v{device.ConfigurationVersion}";
        var existingJob = await _dbContext.MonitoringJobs
            .FirstOrDefaultAsync(j => j.DeviceId == device.Id && j.IdempotencyKey == idempotencyKey, ct);

        var jobId = existingJob?.ExternalJobId ?? $"job-{device.Id:N}-{device.ConfigurationVersion}";
        device.ActiveJobId = jobId;

        var startResult = await _mlClient.StartMonitoringAsync(device, username, password, idempotencyKey, ct);

        if (existingJob == null)
        {
            existingJob = new MonitoringJob
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                ExternalJobId = startResult.JobId ?? jobId,
                IdempotencyKey = idempotencyKey,
                CreatedAtUtc = DateTime.UtcNow
            };
            _dbContext.MonitoringJobs.Add(existingJob);
        }

        existingJob.Attempts++;
        existingJob.UpdatedAtUtc = DateTime.UtcNow;

        if (startResult.Success)
        {
            device.ActiveJobId = startResult.JobId ?? jobId;
            device.MlWorkerId = startResult.WorkerId;
            // Only Online if ML service explicitly confirmed Online; otherwise Starting/Pending
            device.Status = startResult.ResultingStatus;
            device.LastError = null;
            device.LastSeenAtUtc = DateTime.UtcNow;
            existingJob.State = MonitoringJobState.Active;
            existingJob.LastError = null;
        }
        else
        {
            device.Status = startResult.Reachable ? DeviceStatus.Error : DeviceStatus.Offline;
            device.LastError = startResult.ErrorMessage;
            existingJob.State = MonitoringJobState.Failed;
            existingJob.LastError = startResult.ErrorMessage;
        }

        device.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
    }

    private async Task StopJobInternalAsync(MonitoredDevice device, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(device.ActiveJobId))
        {
            await _mlClient.StopMonitoringAsync(device.Id, device.ActiveJobId, ct);
            var activeJobs = await _dbContext.MonitoringJobs
                .Where(j => j.DeviceId == device.Id && j.State == MonitoringJobState.Active)
                .ToListAsync(ct);
            foreach (var j in activeJobs)
            {
                j.State = MonitoringJobState.Stopped;
                j.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        device.MonitoringEnabled = false;
        device.Status = DeviceStatus.Stopped;
        device.LastError = null;
        device.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
    }

    private string GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier) ??
               User.FindFirstValue("sub") ??
               "anonymous_user";
    }

    private static DeviceDto MapToDto(MonitoredDevice d)
    {
        return new DeviceDto(
            DeviceId: d.Id,
            Name: d.Name,
            FeedUrl: d.FeedUrl,
            HasCredentials: !string.IsNullOrWhiteSpace(d.EncryptedStreamCredentials),
            Status: d.Status,
            MonitoringEnabled: d.MonitoringEnabled,
            MlWorkerId: d.MlWorkerId,
            ActiveJobId: d.ActiveJobId,
            LastSeenAtUtc: d.LastSeenAtUtc,
            LastSuccessfulInferenceAtUtc: d.LastSuccessfulInferenceAtUtc,
            LastError: d.LastError,
            ConfigurationVersion: d.ConfigurationVersion,
            CreatedAtUtc: d.CreatedAtUtc,
            UpdatedAtUtc: d.UpdatedAtUtc,
            IsTest: d.IsTest
        );
    }
}
