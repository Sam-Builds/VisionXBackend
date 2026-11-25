using UIBackend.Domain.Enums;

namespace UIBackend.Application.DTOs;

public record CreateShareLinkRequest(
    bool IsPublic = true,
    bool AllowDownload = true,
    int? ExpiresInDays = 7
);

public record ShareLinkResponse(
    Guid Id,
    Guid MediaItemId,
    string Token,
    string ShareUrl,
    bool IsPublic,
    bool AllowDownload,
    DateTime? ExpiresAtUtc,
    int AccessCount,
    bool IsRevoked,
    DateTime CreatedAtUtc
);

public record PublicShareViewResponse(
    string Token,
    Guid MediaItemId,
    string FileName,
    string MimeType,
    MediaType MediaType,
    long FileSizeBytes,
    SourceType SourceType,
    DateTime CapturedAtUtc,
    C2paStatus C2paStatus,
    SynthIdStatus SynthIdStatus,
    double? SynthIdConfidence,
    string C2paIssuer,
    string OriginalSha256,
    bool AllowDownload,
    string MediaUrl,
    string StreamUrl
);
