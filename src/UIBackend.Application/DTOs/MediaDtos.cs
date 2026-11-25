using UIBackend.Domain.Enums;

namespace UIBackend.Application.DTOs;

public record MediaUploadResponse(
    Guid MediaItemId,
    string FileName,
    string MimeType,
    MediaType MediaType,
    long FileSizeBytes,
    string OriginalSha256,
    string? SignedSha256,
    SourceType SourceType,
    C2paStatus C2paStatus,
    SynthIdStatus SynthIdStatus,
    double? SynthIdConfidence,
    string Message
);
