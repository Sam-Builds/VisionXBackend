using UIBackend.Domain.Enums;

namespace UIBackend.Domain.Entities;

public class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public MediaType MediaType { get; set; }
    public long FileSizeBytes { get; set; }
    public string OriginalSha256 { get; set; } = string.Empty;
    public string? SignedSha256 { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public string? ThumbnailStoragePath { get; set; }
    public SourceType SourceType { get; set; } = SourceType.Camera;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }
    public bool IsDeleted { get; set; } = false;

    // Navigation properties
    public CaptureRecord? CaptureRecord { get; set; }
    public ICollection<VerificationRecord> VerificationRecords { get; set; } = new List<VerificationRecord>();
    public ICollection<ShareLink> ShareLinks { get; set; } = new List<ShareLink>();
    public ICollection<MediaAccessEvent> AccessEvents { get; set; } = new List<MediaAccessEvent>();
}
