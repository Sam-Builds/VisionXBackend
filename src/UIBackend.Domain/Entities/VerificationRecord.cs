using UIBackend.Domain.Enums;

namespace UIBackend.Domain.Entities;

public class VerificationRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? MediaItemId { get; set; }
    public string? VerifiedByUserId { get; set; }
    public string MediaSha256 { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public C2paStatus C2paStatus { get; set; }
    public bool HasValidHardBinding { get; set; }
    public bool MatchesRegisteredCapture { get; set; }
    public string? Issuer { get; set; }
    public string? SignerCertificateThumbprint { get; set; }
    public string? ClaimSummaryJson { get; set; }
    public SynthIdStatus SynthIdStatus { get; set; }
    public double? SynthIdConfidence { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string ClientIpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public DateTime VerifiedAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation property
    public MediaItem? MediaItem { get; set; }
}
