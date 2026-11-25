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
            entity.HasIndex(e => e.CreatedAtUtc);

            entity.Property(e => e.OriginalFileName).HasMaxLength(500).IsRequired();
            entity.Property(e => e.MimeType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.OriginalSha256).HasMaxLength(64).IsRequired();
            entity.Property(e => e.SignedSha256).HasMaxLength(64);
            entity.Property(e => e.StoragePath).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.ThumbnailStoragePath).HasMaxLength(1000);
            entity.Property(e => e.UserId).HasMaxLength(256).IsRequired();

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

        modelBuilder.Entity<ShareLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Token).IsUnique();
            entity.HasIndex(e => e.MediaItemId);
            entity.HasIndex(e => e.CreatedByUserId);

            entity.Property(e => e.Token).HasMaxLength(64).IsRequired();
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
