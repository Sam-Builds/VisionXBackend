using Microsoft.EntityFrameworkCore;
using UIBackend.Application.Interfaces;
using UIBackend.Domain.Entities;

namespace UIBackend.Infrastructure.Persistence;

public class UIBackendDbContext : DbContext, IUIBackendDbContext
{
    public UIBackendDbContext(DbContextOptions<UIBackendDbContext> options) : base(options)
    {
    }

    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<CaptureRecord> CaptureRecords => Set<CaptureRecord>();
    public DbSet<VerificationRecord> VerificationRecords => Set<VerificationRecord>();
    public DbSet<VerificationRun> VerificationRuns => Set<VerificationRun>();
    public DbSet<ProvenanceCheck> ProvenanceChecks => Set<ProvenanceCheck>();
    public DbSet<AiDetectionCheck> AiDetectionChecks => Set<AiDetectionCheck>();
    public DbSet<ForensicCheck> ForensicChecks => Set<ForensicCheck>();
    public DbSet<MediaMatch> MediaMatches => Set<MediaMatch>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<MediaAccessEvent> MediaAccessEvents => Set<MediaAccessEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MediaItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.OriginalSha256);
            entity.HasIndex(e => e.SignedSha256);
            entity.HasIndex(e => e.PerceptualHash);
            entity.HasIndex(e => e.CreatedAtUtc);

            entity.Property(e => e.OriginalFileName).HasMaxLength(500).IsRequired();
            entity.Property(e => e.MimeType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.OriginalSha256).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SignedSha256).HasMaxLength(64);
            entity.Property(e => e.PerceptualHash).HasMaxLength(32);
            entity.Property(e => e.StoragePath).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.OriginalArtifactPath).HasMaxLength(1000);
            entity.Property(e => e.ThumbnailStoragePath).HasMaxLength(1000);
            entity.Property(e => e.UserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.AppVersion).HasMaxLength(64);
            entity.Property(e => e.DeviceKeyId).HasMaxLength(256);
            entity.Property(e => e.PlatformAttestationStatus).HasMaxLength(128);

            entity.HasOne(e => e.CaptureRecord)
                  .WithOne(c => c.MediaItem)
                  .HasForeignKey<CaptureRecord>(c => c.MediaItemId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.VerificationRecords)
                  .WithOne(v => v.MediaItem)
                  .HasForeignKey(v => v.MediaItemId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.ShareLinks)
                  .WithOne(s => s.MediaItem)
                  .HasForeignKey(s => s.MediaItemId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.AccessEvents)
                  .WithOne(a => a.MediaItem)
                  .HasForeignKey(a => a.MediaItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CaptureRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MediaItemId).IsUnique();
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CapturedAtUtc);

            entity.Property(e => e.UserId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CaptureDeviceId).HasMaxLength(256);
            entity.Property(e => e.DeviceModel).HasMaxLength(256);
            entity.Property(e => e.DeviceOs).HasMaxLength(128);
            entity.Property(e => e.LocationLatLong).HasMaxLength(128);
            entity.Property(e => e.C2paIssuer).HasMaxLength(256).IsRequired();
            entity.Property(e => e.C2paClaimAlgorithm).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<VerificationRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MediaSha256);
            entity.HasIndex(e => e.VerifiedAtUtc);
            entity.HasIndex(e => e.VerifiedByUserId);

            entity.Property(e => e.MediaSha256).HasMaxLength(64).IsRequired();
            entity.Property(e => e.FileName).HasMaxLength(500);
            entity.Property(e => e.Issuer).HasMaxLength(256);
            entity.Property(e => e.SignerCertificateThumbprint).HasMaxLength(128);
            entity.Property(e => e.ClientIpAddress).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
        });

        modelBuilder.Entity<VerificationRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.InputHash);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.StartedAtUtc);

            entity.Property(e => e.InputFileName).HasMaxLength(500);
            entity.Property(e => e.InputHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.InputPerceptualHash).HasMaxLength(32);
            entity.Property(e => e.InputFormat).HasMaxLength(64);

            entity.HasOne(e => e.MediaItem)
                  .WithMany()
                  .HasForeignKey(e => e.MediaItemId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasMany(e => e.ProvenanceChecks)
                  .WithOne(p => p.VerificationRun)
                  .HasForeignKey(p => p.VerificationRunId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.AiDetectionChecks)
                  .WithOne(a => a.VerificationRun)
                  .HasForeignKey(a => a.VerificationRunId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ForensicChecks)
                  .WithOne(f => f.VerificationRun)
                  .HasForeignKey(f => f.VerificationRunId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.MediaMatches)
                  .WithOne(m => m.VerificationRun)
                  .HasForeignKey(m => m.VerificationRunId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProvenanceCheck>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(128).IsRequired();
            entity.Property(e => e.SignerIdentity).HasMaxLength(256);
        });

        modelBuilder.Entity<AiDetectionCheck>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ProviderVersion).HasMaxLength(64);
            entity.Property(e => e.ResultLabel).HasMaxLength(128);
            entity.Property(e => e.ModelDetected).HasMaxLength(128);
        });

        modelBuilder.Entity<ForensicCheck>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<MediaMatch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.MatchedMediaItem)
                  .WithMany()
                  .HasForeignKey(e => e.MatchedMediaItemId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ShareLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => e.ShareTokenHash);
            entity.HasIndex(e => e.MediaItemId);
            entity.HasIndex(e => e.CreatedByUserId);

            entity.Property(e => e.Token).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ShareTokenHash).HasMaxLength(64);
            entity.Property(e => e.CreatedByUserId).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<MediaAccessEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MediaItemId);
            entity.HasIndex(e => e.AccessedAtUtc);
            entity.HasIndex(e => e.ShareLinkId);

            entity.Property(e => e.IpAddress).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
            entity.Property(e => e.Referer).HasMaxLength(1000);
            entity.Property(e => e.AccessingUserId).HasMaxLength(256);
        });
    }
}
