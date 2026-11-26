using Microsoft.EntityFrameworkCore;
using UIBackend.Domain.Entities;

namespace UIBackend.Application.Interfaces;

public interface IUIBackendDbContext
{
    DbSet<MediaItem> MediaItems { get; }
    DbSet<CaptureRecord> CaptureRecords { get; }
    DbSet<VerificationRecord> VerificationRecords { get; }
    DbSet<VerificationRun> VerificationRuns { get; }
    DbSet<ProvenanceCheck> ProvenanceChecks { get; }
    DbSet<AiDetectionCheck> AiDetectionChecks { get; }
    DbSet<ForensicCheck> ForensicChecks { get; }
    DbSet<MediaMatch> MediaMatches { get; }
    DbSet<ShareLink> ShareLinks { get; }
    DbSet<MediaAccessEvent> MediaAccessEvents { get; }
    DbSet<MonitoredDevice> MonitoredDevices { get; }
    DbSet<MonitoringJob> MonitoringJobs { get; }
    DbSet<DetectionRecord> DetectionRecords { get; }
    DbSet<AlertRecord> AlertRecords { get; }
    DbSet<NotificationOutboxItem> NotificationOutboxItems { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
