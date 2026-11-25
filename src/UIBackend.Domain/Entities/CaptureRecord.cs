using UIBackend.Domain.Enums;

namespace UIBackend.Domain.Entities;

public class CaptureRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaItemId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string CaptureDeviceId { get; set; } = string.Empty;
    public string? DeviceModel { get; set; }
    public string? DeviceOs { get; set; }
    public SourceType SourceType { get; set; } = SourceType.Camera;
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public string LocationLatLong { get; set; } = string.Empty;
    public string C2paManifestJson { get; set; } = string.Empty;
    public string C2paManifestSignature { get; set; } = string.Empty;
    public string C2paIssuer { get; set; } = "VisionX Trust Authority";
    public string C2paClaimAlgorithm { get; set; } = "RS256";
    public C2paStatus C2paStatus { get; set; } = C2paStatus.VerifiedOriginal;
    public SynthIdStatus SynthIdStatus { get; set; } = SynthIdStatus.NotDetected;
    public double? SynthIdConfidence { get; set; }
    public string? SynthIdRawResponse { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // Navigation property
    public MediaItem MediaItem { get; set; } = null!;
}
