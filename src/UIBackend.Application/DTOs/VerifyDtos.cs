using UIBackend.Domain.Enums;

namespace UIBackend.Application.DTOs;

public record VerifyHashRequest(string Sha256);

public record RegisteredMediaSummary(
    Guid MediaItemId,
    string OriginalFileName,
    SourceType SourceType,
    DateTime CapturedAtUtc,
    string OwnerUserIdMasked
);

public record VerifyMediaResponse(
    Guid VerificationId,
    string MediaSha256,
    string FileName,
    long FileSizeBytes,
    C2paStatus C2paStatus,
    bool HasValidHardBinding,
    bool MatchesRegisteredCapture,
    string? Issuer,
    string? SignerCertificateThumbprint,
    string? ClaimSummaryJson,
    SynthIdStatus SynthIdStatus,
    double? SynthIdConfidence,
    string StatusMessage,
    DateTime VerifiedAtUtc,
    RegisteredMediaSummary? RegisteredMedia
);
